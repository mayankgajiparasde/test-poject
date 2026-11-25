using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static HotelUnderwriting.Api.Tests.AuthRig;
using static HotelUnderwriting.Api.Tests.HostileWorkbooks;

namespace HotelUnderwriting.Api.Tests;

/// <summary>Stand-in for a store whose database can be switched off: every call throws what <see cref="Fault"/> returns, while it returns something.</summary>
public class Flaky : DispatchProxy
{
    public object Target { get; set; } = null!;
    public Func<Exception?> Fault { get; set; } = () => null;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Fault() is { } fault) throw fault;
        try { return method!.Invoke(Target, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    public static T Wrap<T>(T target, Func<Exception?> fault) where T : class
    {
        var proxy = Create<T, Flaky>();
        var flaky = (Flaky)(object)proxy;
        flaky.Target = target;
        flaky.Fault = fault;
        return proxy;
    }
}

/// <summary>Story 12 through the HTTP surface: rules (JSON and Excel route), charset, database outage.</summary>
public class HardeningApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private HttpClient C => server.Client;
    private static string Json(Assumptions a) => JsonSerializer.Serialize(a, ApiHost.Json);
    private static Assumptions WithBase(Func<ScenarioInputs, ScenarioInputs> change) => A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? change(s) : s).ToArray() };
    private static async Task<string> PublishSet(HttpClient c) => (await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, Json(A))).Str("id");
    private static string Char(int code) => ((char)code).ToString();

    private static void Refused(Reply r, string detail)
    {
        Assert.True(r.Status == HttpStatusCode.UnprocessableEntity, $"expected 422, got {r.Status}: {r.Text}");
        Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
        Assert.Contains(detail, r.Str("detail"));
        Assert.DoesNotContain("Exception", r.Text);
        Assert.DoesNotContain("Parameter", r.Text);
    }

    // ---- rules on the JSON route (every rule has its own unit test; these prove the 422 and the fixed text over HTTP)

    [Theory]
    [InlineData("purchasePrice", 0.0, "purchase price must be greater than 0")]
    [InlineData("loanToCost", 1.5, "loan-to-cost must be from 0 to 1")]
    [InlineData("exitCapRate", 5.0, "exit cap rate must be at most 1")]
    [InlineData("interestRate", 1e308, "interest rate must be from 0 to 1")]
    public async Task A_rule_violation_is_422_with_a_fixed_text_that_names_the_field(string field, double value, string detail)
    {
        var a = WithBase(s => field switch
        {
            "purchasePrice" => s with { PurchasePrice = value },
            "loanToCost" => s with { LoanToCost = value },
            "exitCapRate" => s with { ExitCapRate = value },
            _ => s with { InterestRate = value },
        });
        var r = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(a));
        Refused(r, detail);
        Assert.DoesNotContain("e+308", r.Text);
    }

    [Fact]
    public async Task Occupancy_over_1_and_a_pip_without_duration_are_422()
    {
        Refused(await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(WithBase(s => s with { Occupancy = [5, .. s.Occupancy.Skip(1)] }))), "occupancy must be from 0 to 1");
        Refused(await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(WithBase(s => s with { PipPerKey = 1000, PipDurationMonths = 0 }))), "PIP duration must be at least 1");
    }

    [Fact]
    public async Task Dates_that_are_not_the_first_of_a_month_or_overflow_are_422_with_a_fixed_text()
    {
        const string first = "The acquisition date must be the 1st of a month, at midnight.";
        foreach (var date in new[] { new DateTime(2027, 4, 15), new DateTime(2027, 4, 1, 12, 0, 0) })
            Refused(await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Property = A.Property with { AcquisitionDate = date } })), first);
        Refused(await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Property = A.Property with { AcquisitionDate = new DateTime(9999, 12, 1) } })), "too far in the future");

        // the Excel route: a mid-month date, a day with a time fraction (46478.5), and one that overflows
        foreach (var edit in new Action<IXLCell>[] { c => c.Value = new DateTime(2027, 4, 15), c => c.Value = c.Value.GetDateTime().AddHours(12), c => c.Value = new DateTime(9999, 12, 1) })
        {
            var r = await Upload(Edited(wb => edit(Labelled(wb, "Inputs", "Acquisition date"))));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            Assert.DoesNotContain("Exception", r.Text);
            Assert.Contains("acquisition date", r.Str("detail"));
        }
    }

    private Task<Reply> Upload(byte[] workbook, HttpClient? client = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/assumption-sets/excel") { Content = new ByteArrayContent(workbook) };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ApiHost.XlsxContentType);
        return Read((client ?? C).SendAsync(req));
    }

    private static async Task<Reply> Read(Task<HttpResponseMessage> response)
    {
        var res = await response;
        return new Reply(res.StatusCode, await res.Content.ReadAsStringAsync(), res);
    }

    [Fact]
    public async Task The_excel_route_applies_the_same_rules()
    {
        var r = await Upload(Edited(wb => Labelled(wb, "Inputs", "Purchase price").Value = 0));
        Refused(r, "purchase price must be greater than 0");
    }

    [Fact]
    public async Task Bad_names_are_422_on_both_routes()
    {
        foreach (var bad in new[] { Char(1), "a" + Char(0x202E) + "b", "a" + Char(0x200B) + "b", "", "Base " })
        {
            var r = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Scenarios = [A.Scenarios[0] with { Name = bad }, .. A.Scenarios.Skip(1)] }));
            Assert.True(r.Status == HttpStatusCode.UnprocessableEntity, $"name {bad.Length} chars: {r.Status} {r.Text}");
        }
        var excel = await Upload(Edited(wb => Labelled(wb, "Inputs", "Property").Value = "a" + Char(0x202E) + "b"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, excel.Status);
    }

    [Fact]
    public async Task A_sweep_case_name_follows_the_publish_text_rule()
    {
        var set = await PublishSet(C);
        var body = $$"""{"assumptionSetId":"{{set}}","scenario":"Base","cases":[{"name":"a‮b","overrides":[{"path":"interestRate","value":0.06}]}]}""";
        Refused(await Send(C, HttpMethod.Post, "/v1/sweeps", null, body), "no control characters");
    }

    // ---- charset

    [Theory]
    [InlineData("application/json; charset=nonsense")]
    [InlineData("application/json; charset=utf-7")]
    [InlineData("application/json; charset=")]
    public async Task A_charset_the_body_cannot_be_read_with_is_415_on_every_json_route(string contentType)
    {
        foreach (var url in new[] { "/v1/runs", "/v1/assumption-sets", "/v1/sweeps" })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            var r = await Read(C.SendAsync(req));
            Assert.True(r.Status == HttpStatusCode.UnsupportedMediaType, $"{url} with '{contentType}': {r.Status} {r.Text}");
            Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task A_utf8_charset_is_still_read()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"assumptionSetId":"nope","scenario":"Base"}""")) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        Assert.Equal(HttpStatusCode.NotFound, (await Read(C.SendAsync(req))).Status);
    }

    // ---- ids with control characters are unknown ids

    [Fact]
    public async Task An_id_with_a_NUL_is_404_everywhere()
    {
        foreach (var url in new[] { "/v1/assumption-sets/a%00b", "/v1/runs/a%00b", "/v1/jobs/a%00b", "/v1/jobs/a%00b/results" })
        {
            var r = await Send(C, HttpMethod.Get, url, null);
            Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{url}: {r.Status}");
        }
        var nul = "a\\u0000b";
        Assert.Equal(HttpStatusCode.NotFound, (await Send(C, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{nul}}","scenario":"Base"}""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(C, HttpMethod.Post, "/v1/sweeps", null, $$"""{"assumptionSetId":"{{nul}}","scenario":"Base","grid":[{"path":"promote","values":[0.2]}]}""")).Status);
    }

    // ---- database outage

    // The Excel parse runs in a child process with a 30 s default limit; on a loaded machine that alone gave a 422 "within limits" here (the flake of the first version of this test).
    private static readonly UploadLimits Patient = new(TimeSpan.FromMinutes(5), 512, 2) { BodyTimeout = TimeSpan.FromMinutes(5) };
    private static NpgsqlException Lost() => new("connection lost");

    private const string ClosedPort = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=2";

    private static void Unavailable(Reply r)
    {
        Assert.True(r.Status == HttpStatusCode.ServiceUnavailable, $"expected 503, got {r.Status}: {r.Text}");
        Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
        Assert.Equal("5", r.Message.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.DoesNotContain("Npgsql", r.Text);
        Assert.DoesNotContain("127.0.0.1", r.Text);
    }

    [Fact]
    public async Task With_the_database_unreachable_every_route_that_needs_it_is_503_with_retry_after()
    {
        var log = new AuthTests.Capture();
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", $"--Persistence:ConnectionString={ClosedPort}"], s => { s.AddSingleton<ILoggerProvider>(log); s.AddSingleton(Patient); });
        await app.StartAsync();
        using var c = Client(app);
        c.Timeout = TimeSpan.FromMinutes(5);
        Unavailable(await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, Json(A)));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/runs", null, """{"assumptionSetId":"abc","scenario":"Base"}"""));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/sweeps", null, """{"assumptionSetId":"abc","scenario":"Base","grid":[{"path":"promote","values":[0.2]}]}"""));
        Unavailable(await Send(c, HttpMethod.Get, "/v1/runs/abc", null));
        Unavailable(await Send(c, HttpMethod.Get, "/v1/jobs/abc", null));
        Unavailable(await Upload(Reference, c));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(c, HttpMethod.Get, "/health/ready", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Get, "/health/live", null)).Status);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Text.StartsWith("Database unavailable"));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("127.0.0.1"));
    }

    [Fact]
    public async Task A_failure_inside_a_sweep_submit_or_after_an_upload_is_parsed_is_503_and_the_service_recovers_without_a_restart()
    {
        var down = false;
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
        {
            s.AddSingleton(Patient);
            s.AddSingleton<IAssumptionSetStore>(Flaky.Wrap<IAtomicAssumptionSetStore>(new InMemoryAssumptionSetStore(), () => down ? Lost() : null));
            s.AddSingleton<IJobStore>(Flaky.Wrap<IJobStore>(new InMemoryJobStore(), () => down ? Lost() : null));
        });
        await app.StartAsync();
        using var c = Client(app);
        c.Timeout = TimeSpan.FromMinutes(5);
        var set = await PublishSet(c);
        var sweep = $$"""{"assumptionSetId":"{{set}}","scenario":"Base","grid":[{"path":"promote","values":[0.2]}]}""";

        // the jobs store alone is down: the set lookup works, the failure is inside the submit
        var jobsDown = false;
        await using var app2 = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
            s.AddSingleton<IJobStore>(Flaky.Wrap<IJobStore>(new InMemoryJobStore(), () => jobsDown ? Lost() : null)));
        await app2.StartAsync();
        using var c2 = Client(app2);
        var set2 = await PublishSet(c2);
        var sweep2 = sweep.Replace(set, set2);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(c2, HttpMethod.Post, "/v1/sweeps", null, sweep2)).Status);
        jobsDown = true;
        Unavailable(await Send(c2, HttpMethod.Post, "/v1/sweeps", null, sweep2.Replace("0.2", "0.3")));
        jobsDown = false;
        Assert.Equal(HttpStatusCode.Accepted, (await Send(c2, HttpMethod.Post, "/v1/sweeps", null, sweep2.Replace("0.2", "0.3"))).Status);

        // the whole database goes away and comes back
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Get, "/health/ready", null)).Status);
        down = true;
        Unavailable(await Send(c, HttpMethod.Get, $"/v1/assumption-sets/{set}", null));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, Json(A)));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{set}}","scenario":"Base"}"""));
        Unavailable(await Send(c, HttpMethod.Post, "/v1/sweeps", null, sweep));
        Unavailable(await Upload(Reference, c));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(c, HttpMethod.Get, "/health/ready", null)).Status);
        down = false;
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Get, "/health/ready", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Get, $"/v1/assumption-sets/{set}", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{set}}","scenario":"Base"}""")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Upload(Reference, c)).Status);
    }

    private static PostgresException Server(string sqlState) => new("server said no", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("08006", 503)] [InlineData("53300", 503)] [InlineData("57P01", 503)]
    [InlineData("23505", 500)] [InlineData("23514", 500)] [InlineData("42P01", 500)]
    public async Task A_postgres_answer_is_an_outage_only_for_connection_resource_and_shutdown_classes(string sqlState, int expected)
    {
        Exception? fail = null;
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
            s.AddSingleton<IAssumptionSetStore>(Flaky.Wrap<IAtomicAssumptionSetStore>(new InMemoryAssumptionSetStore(), () => fail)));
        await app.StartAsync();
        using var c = Client(app);
        fail = Server(sqlState);
        var r = await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null);
        Assert.Equal(expected, (int)r.Status);
        if (expected == 503) Unavailable(r);
    }

    [Fact]
    public async Task A_timeout_is_an_outage_only_when_the_database_driver_reports_it()
    {
        Exception? fail = null;
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
            s.AddSingleton<IAssumptionSetStore>(Flaky.Wrap<IAtomicAssumptionSetStore>(new InMemoryAssumptionSetStore(), () => fail)));
        await app.StartAsync();
        using var c = Client(app);
        fail = new NpgsqlException("command timed out", new TimeoutException()); // what Npgsql throws for a command timeout
        Unavailable(await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null));
        fail = new TimeoutException("some other timeout"); // thrown outside Npgsql: an ordinary fault
        Assert.Equal(HttpStatusCode.InternalServerError, (await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null)).Status);
        fail = new InvalidOperationException("a bug");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null)).Status);
    }

    // ---- sweep overflow over HTTP

    [Fact]
    public async Task A_sweep_of_overflowing_values_is_all_case_errors_with_no_run_and_no_non_finite_text()
    {
        var set = await PublishSet(C);
        const string cases = """
            [{"overrides":[{"path":"interestRate","value":1e308}]},{"overrides":[{"path":"loanToCost","value":1e308}]},
             {"overrides":[{"path":"expenseInflation","value":1e300}]},{"overrides":[{"path":"pipPerKey","value":1e308}]},
             {"overrides":[{"path":"purchasePrice","value":0}]},{"overrides":[{"path":"purchasePrice","value":1e308}]}]
            """;
        var job = (await Send(C, HttpMethod.Post, "/v1/sweeps", null, $$"""{"assumptionSetId":"{{set}}","scenario":"Base","cases":{{cases}}}""")).Str("jobId");
        await WaitFor(C, null, job);
        var results = await Send(C, HttpMethod.Get, $"/v1/jobs/{job}/results", null);
        var items = JsonDocument.Parse(results.Text).RootElement.GetProperty("items");
        Assert.Equal(6, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("runKey").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("error").ValueKind);
        }
        Assert.DoesNotContain("NaN", results.Text);
        Assert.DoesNotContain("Infinity", results.Text);
    }

    // ---- story 12b

    private async Task<Reply> PostWithType(string url, string contentType, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return await Read(C.SendAsync(req));
    }

    [Theory]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=\"UTF-8\"")]
    [InlineData("application/json; CHARSET=utf-8")]
    [InlineData("application/json; charset=utf-8;")]
    [InlineData("application/json;charset=utf-8; foo=bar")]
    public async Task A_quoted_or_oddly_written_utf8_charset_is_read(string contentType)
    {
        foreach (var url in new[] { "/v1/runs", "/v1/sweeps" })
        {
            var r = await PostWithType(url, contentType, """{"assumptionSetId":"nope","scenario":"Base"}""");
            Assert.True(r.Status == HttpStatusCode.NotFound, $"{url} with '{contentType}': {r.Status} {r.Text}");
        }
        var set = await PostWithType("/v1/assumption-sets", contentType, Json(A));
        Assert.True(set.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"publish with '{contentType}': {set.Status} {set.Text}");
    }

    [Fact]
    public async Task A_utf16_body_with_a_quoted_charset_is_transcoded()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = new ByteArrayContent(Encoding.Unicode.GetBytes("""{"assumptionSetId":"nope","scenario":"Ba\u00dfe"}""")) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=\"UTF-16\"");
        Assert.Equal(HttpStatusCode.NotFound, (await Read(C.SendAsync(req))).Status);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8; charset=utf-16")]
    [InlineData("application/json; charset=\"nonsense\"")]
    [InlineData("application/json; charset=\"\"")]
    [InlineData("application/json; charset=\"utf-7\"")]
    [InlineData("application/json; charset=utf-8x")]
    public async Task A_duplicate_or_unusable_charset_is_415(string contentType)
    {
        foreach (var url in new[] { "/v1/runs", "/v1/assumption-sets", "/v1/sweeps" })
        {
            var r = await PostWithType(url, contentType, "{}");
            Assert.True(r.Status == HttpStatusCode.UnsupportedMediaType, $"{url} with '{contentType}': {r.Status} {r.Text}");
        }
    }

    [Fact]
    public async Task No_Content_Type_value_produces_a_500_on_any_json_route()
    {
        var values = new List<string>
        {
            "application/json;", "application/json; ", "application/json;;", "application/json; charset", "application/json; charset=", "application/json; charset=;",
            "application/json; charset=\"", "application/json; charset=\"utf-8", "application/json; charset=utf-8\"", "application/json; charset='utf-8'",
            "application/json; charset=\"utf-8\"; charset=\"utf-8\"", "application/json; a=b; a=c", "application/json; =utf-8", "application/json; charset==utf-8",
            "application/json, application/json", "application/json; charset=utf-8, charset=utf-16", "application/json; charset=\"\\\"\"", "application/json; charset=" + new string('x', 5000),
            "application/json; " + string.Join("; ", Enumerable.Range(0, 300).Select(i => "p" + i + "=v")), new string('a', 8000), "application/", "/json", "*/*", "application/*", "application/json+x",
            "application/problem+json; charset=\"utf-8\"", "text/json; charset=utf-8", "application/json; charset=utf-8\t", "application/json\t; charset=utf-8", "APPLICATION/JSON; CHARSET=UTF-8",
            "application/json; charset=utf-32", "application/json; charset=us-ascii", "application/json; charset=iso-8859-1", "application/json; charset=utf-16be", "application/json; charset=utf-7",
            "application/json; charset=" + char.ConvertFromUtf32(0x1F3E8), "application/json; charset=\u0001",
        };
        var bad = 0;
        var seen = new List<string>();
        foreach (var url in new[] { "/v1/runs", "/v1/assumption-sets", "/v1/sweeps" })
            foreach (var v in values)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"assumptionSetId":"nope","scenario":"Base"}""")) };
                if (!req.Content.Headers.TryAddWithoutValidation("Content-Type", v)) continue; // the client itself refuses to send it
                HttpResponseMessage res;
                try { res = await C.SendAsync(req); }
                catch (HttpRequestException) { continue; }
                if ((int)res.StatusCode >= 500) { bad++; seen.Add($"{url} '{v[..Math.Min(v.Length, 60)]}' -> {(int)res.StatusCode}"); }
            }
        Assert.True(bad == 0, string.Join("; ", seen));
    }

    [Fact]
    public async Task A_404_or_400_quotes_a_value_only_when_it_looks_like_an_id()
    {
        var set = await PublishSet(C);
        var odd = "zq secret/free text";
        var r = await Send(C, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{odd}}","scenario":"Base"}""");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.DoesNotContain("zq", r.Text);
        r = await Send(C, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{set}}","scenario":"{{odd}}"}""");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.DoesNotContain("zq", r.Text);
        r = await Send(C, HttpMethod.Post, "/v1/sweeps", null, $$"""{"assumptionSetId":"{{set}}","scenario":"{{odd}}","grid":[{"path":"promote","values":[0.2]}]}""");
        Assert.DoesNotContain("zq", r.Text);
        foreach (var url in new[] { "/v1/assumption-sets/zq%20secret", "/v1/runs/zq%20secret", "/v1/jobs/zq%20secret", "/v1/jobs/zq%20secret/results" })
        {
            r = await Send(C, HttpMethod.Get, url, null);
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.DoesNotContain("zq", r.Text);
        }
        r = await Send(C, HttpMethod.Get, "/v1/runs/zzz?include=zq%20secret", null);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.DoesNotContain("zq", r.Text);
        // a plain id is still named
        r = await Send(C, HttpMethod.Get, "/v1/runs/plain-id_1.x", null);
        Assert.Contains("'plain-id_1.x'", r.Str("detail"));
        // an unknown driver is not quoted either
        var driver = A with { Lines = [A.Lines[0] with { Driver = "zq-secret-driver" }, .. A.Lines.Skip(1)] };
        var d = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(driver));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, d.Status);
        Assert.DoesNotContain("zq", d.Text);
    }

    [Fact]
    public async Task The_new_bounds_are_422_on_the_json_route_and_the_excel_route()
    {
        var json = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Lines = [A.Lines[0] with { FixedPct = 1.5 }, .. A.Lines.Skip(1)] }));
        Refused(json, "fixed share must be from 0 to 1");
        var excel = await Upload(Edited(wb => Labelled(wb, "Operating Inputs", "FB_REV", 5).Value = 1.5));
        Refused(excel, "fixed share must be from 0 to 1");
        Refused(await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(WithBase(s => s with { CostOfSalePct = 0.7, DispositionFeePct = 0.7 }))), "together must be at most 1");
    }

    [Fact]
    public async Task A_rule_violation_is_422_while_the_database_is_down_and_a_valid_set_is_503()
    {
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
            s.AddSingleton<IAssumptionSetStore>(Flaky.Wrap<IAtomicAssumptionSetStore>(new InMemoryAssumptionSetStore(), () => Lost())));
        await app.StartAsync();
        using var c = Client(app);
        Refused(await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, Json(WithBase(s => s with { PurchasePrice = 0 }))), "purchase price must be greater than 0");
        Unavailable(await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, Json(A)));
    }

    // ---- story 12c

    [Fact]
    public async Task Client_text_cannot_start_a_forged_line_in_the_log_and_real_warnings_are_still_logged()
    {
        var log = new AuthTests.Capture();
        var broken = false;
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0"], s =>
        {
            s.AddSingleton<ILoggerProvider>(log);
            s.AddSingleton<IAssumptionSetStore>(Flaky.Wrap<IAtomicAssumptionSetStore>(new InMemoryAssumptionSetStore(), () => broken ? new InvalidOperationException("a bug") : null));
        });
        await app.StartAsync();
        using var c = Client(app);
        await Send(c, HttpMethod.Get, "/v1/runs/zz%0AFAKE%20crit:%20x?q=a%0AFAKE2", null);
        await Send(c, HttpMethod.Get, "/v1/assumption-sets/zz%0D%0AFAKE", null);
        Assert.DoesNotContain(log.Entries, e => e.Text.Contains("FAKE"));
        broken = true;
        await Send(c, HttpMethod.Get, "/v1/assumption-sets/abc", null);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Text == "Unhandled exception");
    }

    [Fact]
    public async Task A_zero_base_driver_is_a_clear_422_on_both_routes()
    {
        var driver = A.Lines.First(l => l.Section == "REV");
        var user = A.Lines.First(l => l.Driver == driver.Code);
        var json = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Lines = A.Lines.Select(l => l.Code == driver.Code ? l with { BaseAmount = 0 } : l).ToArray() }));
        Refused(json, $"Line {user.Code}: its driver line {driver.Code} has a zero base amount.");
        var occupancy = await Send(C, HttpMethod.Post, "/v1/assumption-sets", null, Json(A with { Property = A.Property with { BaseOccupancy = 0 } }));
        Refused(occupancy, "because the base occupancy is 0");
    }
}
