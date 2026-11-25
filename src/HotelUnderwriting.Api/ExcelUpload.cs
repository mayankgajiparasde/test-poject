using HotelUnderwriting.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace HotelUnderwriting.Api;

/// <summary>The body of <c>POST /v1/assumption-sets/excel</c>: a raw .xlsx file, held in memory only and parsed by <see cref="WorkbookParser"/>.</summary>
public static partial class ApiHost
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int MaxUploadBytes = 10 * MaxBodyBytes;

    private static bool IsXlsx(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type) && type.MediaType.Equals(XlsxContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Call only after the header and authorization checks: nothing before this reads the body.</summary>
    private static async Task<IResult> Upload(HttpContext ctx, WorkbookParser parser, UploadLimits limits, IHostApplicationLifetime life, Func<Assumptions, IResult> publish)
    {
        static IResult TooLarge() => Problem(413, "Request body exceeds 10 MB.");
        IResult Unavailable(string detail)
        {
            ctx.Response.Headers.RetryAfter = "5";
            return Problem(503, detail);
        }

        // The server's cap sits above ours so chunk framing never counts against the 10 MB; the exact cap is applied while reading.
        // It also lets the server discard the unread body of a refused upload.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = MaxUploadBytes + MaxBodyBytes;
        if (ctx.Request.ContentLength > MaxUploadBytes) return TooLarge();
        if (!parser.Slots.Wait(0)) return Unavailable("Too many uploads are being processed. Try again shortly.");
        try
        {
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
            try
            {
                var file = new MemoryStream();
                using (var late = new CancellationTokenSource(limits.BodyTimeout))
                using (var receiving = CancellationTokenSource.CreateLinkedTokenSource(abort.Token, late.Token))
                {
                    try
                    {
                        var chunk = new byte[81920];
                        int n;
                        while ((n = await ctx.Request.Body.ReadAsync(chunk, receiving.Token)) > 0)
                        {
                            if (file.Length + n > MaxUploadBytes) return TooLarge();
                            file.Write(chunk, 0, n);
                        }
                    }
                    catch (OperationCanceledException) when (late.IsCancellationRequested && !abort.IsCancellationRequested)
                    {
                        // HTTP/2 and later have no Connection header: there only this request ends.
                        if (HttpProtocol.IsHttp11(ctx.Request.Protocol)) ctx.Response.Headers.Connection = "close";
                        return Problem(408, "The request body was not received in time.");
                    }
                    catch (IOException e) when (e is not BadHttpRequestException) // the client reset the connection: an abort, not a server fault
                    {
                        ctx.Abort();
                        return Results.Empty;
                    }
                }
                var (assumptions, refused) = await parser.Parse(file.ToArray(), abort.Token);
                if (assumptions is not null) return publish(assumptions);
                return refused is not null ? Problem(422, refused) : Problem(500, "An unexpected error occurred.");
            }
            catch (BadHttpRequestException e) when (e.StatusCode == 413) { return TooLarge(); }
            catch (OperationCanceledException) when (life.ApplicationStopping.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
            {
                return Unavailable("The service is stopping.");
            }
        }
        finally { parser.Slots.Release(); }
    }
}
