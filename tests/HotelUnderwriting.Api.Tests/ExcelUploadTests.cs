using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static HotelUnderwriting.Api.Tests.AuthRig;
using static HotelUnderwriting.Api.Tests.HostileWorkbooks;

namespace HotelUnderwriting.Api.Tests;

/// <summary>The Excel upload endpoint (story 10b), one or more tests per matrix row.</summary>
public class ExcelUploadTests
{
    private const string Url = "/v1/assumption-sets/excel";
    private const int TenMb = 10 * 1024 * 1024;

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient C { get; init; }
        public required AuthTests.Capture Log { get; init; }
        public IReadOnlyList<LedgerEntry> Ledger => ((InMemoryLedger)App.Services.GetRequiredService<ILedger>()).Entries;

        public async ValueTask DisposeAsync()
        {
            C.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<Host> Start(UploadLimits? limits = null, string[]? args = null)
    {
        var log = new AuthTests.Capture();
        var app = ApiHost.Build(args ?? ["--urls", "http://127.0.0.1:0"], s =>
        {
            s.AddSingleton<ILoggerProvider>(log);
            if (limits is not null) s.AddSingleton(limits);
        });
        await app.StartAsync();
        return new Host { App = app, C = Client(app), Log = log };
    }

    private static UploadLimits Limits(double seconds = 30, int heapMb = 512, int concurrent = 2) => new(TimeSpan.FromSeconds(seconds), heapMb, concurrent);

    private static async Task<Reply> Post(HttpClient c, byte[] body, string? type = ApiHost.XlsxContentType, bool chunked = false, string? token = null, string? deal = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new ByteArrayContent(body) };
        if (type is not null) req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        if (chunked) req.Headers.TransferEncodingChunked = true;
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (deal is not null) req.Headers.TryAddWithoutValidation("X-Deal-Id", deal);
        var res = await c.SendAsync(req, ct);
        return new Reply(res.StatusCode, await res.Content.ReadAsStringAsync(ct), res);
    }

    private static async Task<NetworkStream> Open(WebApplication app, TcpClient tcp, int length, string? token = null, string? deal = null, string type = ApiHost.XlsxContentType)
    {
        var uri = new Uri(app.Urls.First());
        await tcp.ConnectAsync(IPAddress.Loopback, uri.Port);
        var head = new StringBuilder($"POST {Url} HTTP/1.1\r\nHost: localhost\r\nContent-Type: {type}\r\nContent-Length: {length}\r\n");
        if (token is not null) head.Append($"Authorization: Bearer {token}\r\n");
        if (deal is not null) head.Append($"X-Deal-Id: {deal}\r\n");
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()));
        return stream;
    }

    /// <summary>Sends the headers and no body at all, so any answer was decided without reading one. Returns the response head.</summary>
    private static async Task<string> HeadersOnly(WebApplication app, string? token = null, string? deal = null, string type = ApiHost.XlsxContentType, int length = 5_000_000)
    {
        using var tcp = new TcpClient();
        var stream = await Open(app, tcp, length, token, deal, type);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // a server that waited for the body would answer 408 instead
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer, timeout.Token);
            if (n == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        return head.ToString();
    }

    private static void Problem(Reply r, HttpStatusCode status, string detail = "")
    {
        Assert.True(r.Status == status, $"expected {status}, got {r.Status}: {r.Text}");
        Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
        Assert.Contains(detail, r.Str("detail"));
        Assert.DoesNotContain("Exception", r.Text);
    }

    private static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static async Task Until(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(done(), what);
    }

    /// <summary>Holds the first child before it gets the file (it sits waiting on stdin) until released or killed.</summary>
    private sealed class Hold
    {
        public readonly TaskCompletionSource<int> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Hook(Process child, CancellationToken kill)
        {
            if (Interlocked.Increment(ref _calls) != 1) return Task.CompletedTask;
            Started.SetResult(child.Id);
            return Release.Task.WaitAsync(kill);
        }
    }

    // ---- reference workbook

    /// <summary>Taken from the code as it was before the upload story: neither the reader nor the publish path may change them.</summary>
    [Fact]
    public async Task The_reference_workbook_keeps_its_set_id_and_run_keys()
    {
        await using var h = await Start();
        var set = await Post(h.C, Reference);
        Assert.Equal("bddceda5e0021a3a43bdda20bbfc61f1fcb9cea6a737f13442280ad3993c2f99", set.Str("id"));
        foreach (var (scenario, key) in new[]
        {
            ("Base", "974d28bf9084e5375243e7607dd1be3156eb5bf8d1892757e62b63d7c7be3929"),
            ("Upside", "efe5398d16ec4fc6016de1d4963eb54ecbaa35b1f14d000c5d237f3560cb8c79"),
            ("Downside", "d15745b50a9220a6d79ab50b9f9abd33f3f9c9a6fabb203ae929c0edbeb02f70"),
        })
        {
            var run = await Send(h.C, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{set.Str("id")}}","scenario":"{{scenario}}"}""");
            Assert.Equal(key, run.Str("runKey"));
        }
    }

    [Fact]
    public async Task The_reference_workbook_publishes_the_same_set_as_its_json_and_repeats_as_200()
    {
        await using var h = await Start();
        var first = await Post(h.C, Reference);
        Assert.True(first.Status == HttpStatusCode.Created, first.Text);
        Assert.Equal(AssumptionSetId.Compute(A), first.Str("id"));
        Assert.Equal($"/v1/assumption-sets/{first.Str("id")}", first.Message.Headers.Location!.ToString());
        Assert.Equal(A.Scenarios.Select(s => s.Name), first.Json["scenarios"]!.AsArray().Select(n => n!.GetValue<string>()));

        var json = await Send(h.C, HttpMethod.Post, "/v1/assumption-sets", null, JsonSerializer.Serialize(A, ApiHost.Json));
        var again = await Post(h.C, Reference, ApiHost.XlsxContentType.ToUpperInvariant(), chunked: true);
        Assert.All(new[] { json, again }, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(first.Text, json.Text);
        Assert.Equal(first.Text, again.Text);
        Assert.Equal(3, h.Ledger.Count);
        Assert.All(h.Ledger, e => Assert.Equal("anonymous", e.Actor));
    }

    [Fact]
    public async Task The_child_returns_exactly_the_assumptions_the_reader_reads_at_the_smallest_allowed_heap()
    {
        await using var h = await Start(Limits(heapMb: UploadLimits.MinHeapMb));
        var (parsed, refused) = await h.App.Services.GetRequiredService<WorkbookParser>().Parse(Reference, default);
        Assert.Null(refused);
        Assert.Equal(JsonSerializer.Serialize(A, ApiHost.Json), JsonSerializer.Serialize(parsed, ApiHost.Json));
    }

    // ---- authentication, type, size

    [Fact]
    public async Task Jwt_mode_refuses_before_the_body_and_publishes_into_the_deal_as_the_subject()
    {
        var started = 0;
        await using var h = await Start(Limits() with { BeforeInput = (_, _) => { Interlocked.Increment(ref started); return Task.CompletedTask; } }, JwtArgs);
        foreach (var (status, token, deal) in new (int, string?, string?)[]
        {
            (401, null, "d1"), (403, Token("rita", "reviewer", "d1"), "d1"), (403, Token("n", "", "d1"), "d1"),
            (400, Alice, null), (400, Alice, "bad deal"), (403, Alice, "d2"), (400, Root, null),
        })
            Assert.StartsWith($"HTTP/1.1 {status} ", await HeadersOnly(h.App, token, deal));
        Assert.StartsWith("HTTP/1.1 415 ", await HeadersOnly(h.App, Alice, "d1", "application/json"));
        Assert.StartsWith("HTTP/1.1 413 ", await HeadersOnly(h.App, Alice, "d1", length: TenMb + 1));
        Assert.Equal(0, started);
        Assert.Empty(h.Ledger);

        var made = await Post(h.C, Reference, token: Alice, deal: "d1");
        Assert.True(made.Status == HttpStatusCode.Created, made.Text);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.C, Reference, token: Alice, deal: "d1")).Status);
        Assert.Equal(["d1"], h.App.Services.GetRequiredService<IDealAccess>().DealsOf(DealResource.Set, made.Str("id")));
        Assert.Equal(HttpStatusCode.NotFound, (await Get(h.C, $"/v1/assumption-sets/{made.Str("id")}", Bob)).Status);
        Assert.All(h.Ledger, e => Assert.Equal("alice", e.Actor));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.ms-excel.sheet.macroEnabled.12")]
    [InlineData(null)]
    public async Task Other_content_types_are_415(string? type)
    {
        await using var h = await Start();
        Problem(await Post(h.C, Reference, type), HttpStatusCode.UnsupportedMediaType, "Content-Type");
        Assert.Empty(h.Ledger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Over_10_mb_is_413_and_exactly_10_mb_is_read(bool chunked)
    {
        await using var h = await Start(Limits(concurrent: 1));
        Problem(await Post(h.C, new byte[TenMb + 1], chunked: chunked), HttpStatusCode.RequestEntityTooLarge, "10 MB");
        Problem(await Post(h.C, new byte[TenMb], chunked: chunked), HttpStatusCode.UnprocessableEntity, "zip");
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference, chunked: chunked)).Status); // the slot is free again
    }

    [Fact]
    public async Task The_json_route_keeps_its_1_mb_limit()
    {
        await using var h = await Start();
        var r = await Send(h.C, HttpMethod.Post, "/v1/assumption-sets", null, "{\"x\":\"" + new string('a', 1_200_000) + "\"}");
        Problem(r, HttpStatusCode.RequestEntityTooLarge, "1 MB");
    }

    // ---- hostile files

    public static TheoryData<string> Hostile()
    {
        var data = new TheoryData<string>();
        foreach (var name in All.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task A_hostile_file_is_422_for_its_own_reason_and_publishes_nothing(string name)
    {
        var (detail, make) = All[name];
        var file = make();
        await using var h = await Start();
        var watch = Stopwatch.StartNew();
        Problem(await Post(h.C, file), HttpStatusCode.UnprocessableEntity, detail);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Empty(h.Ledger);
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Warning && e.Text.StartsWith("Workbook")); // refused for the rule, not by a limit
    }

    [Fact]
    public void The_limits_themselves_are_allowed()
    {
        PackageInspector.Inspect(Reference);
        PackageInspector.Inspect(Rows(PackageInspector.MaxRowsPerSheet));
        var used = 0;
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(Reference)))
        {
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/")))
            {
                using var r = new StreamReader(e.Open());
                used += System.Text.RegularExpressions.Regex.Count(r.ReadToEnd(), "<c[ />]");
            }
        }
        PackageInspector.Inspect(Cells(PackageInspector.MaxCells - used));
        Assert.Throws<WorkbookException>(() => PackageInspector.Inspect(Cells(PackageInspector.MaxCells - used + 1)));
        PackageInspector.Inspect(ListedSheets(PackageInspector.MaxSheets - 11));
        int entries;
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(Reference))) entries = zip.Entries.Count;
        PackageInspector.Inspect(Package(z => { for (var i = entries; i < PackageInspector.MaxEntries; i++) Add(z, $"junk/{i}.bin"); }));
        PackageInspector.Inspect(Package(z => Add(z, "xl/media/big.bin", new byte[PackageInspector.MaxEntryBytes])));
        // a real calculation chain is not a sheet: its cell references do not count
        PackageInspector.Inspect(Package(z => Add(z, "xl/calcChain.xml", ManyChained),
            Package(z => Change(z, "[Content_Types].xml", t => t.Replace("</Types>", $"<Override PartName=\"/xl/calcChain.xml\" ContentType=\"{CalcChainType}\"/></Types>")))));
        PackageInspector.Inspect(Package(z => Change(z, "xl/_rels/workbook.xml.rels", t => t.Replace("Id=\"rId1\"", "TargetMode=\"Internal\" Id=\"rId1\""))));
    }

    [Fact]
    public async Task Hyperlinks_and_loosely_formed_vml_are_allowed()
    {
        var linked = Edited(wb => wb.Worksheet("Inputs").Cell("H1").SetHyperlink(new XLHyperlink("http://127.0.0.1:1/never-followed")));
        PackageInspector.Inspect(linked);
        PackageInspector.Inspect(Package(z => Add(z, "xl/drawings/vmlDrawing9.vml", "<xml><div>a<br></div></xml>")));
        await using var h = await Start();
        var r = await Post(h.C, linked);
        Assert.True(r.Status == HttpStatusCode.Created, r.Text);
        Assert.Equal(AssumptionSetId.Compute(A), r.Str("id"));
    }

    // ---- formulas

    private const string InputsPart = "xl/worksheets/sheet2.xml";

    [Fact]
    public async Task A_formula_uses_its_cached_value_and_is_never_evaluated()
    {
        var cell = Address("Inputs", "Keys");
        await using var h = await Start();
        var r = await Post(h.C, RawCell(InputsPart, cell, $"<c r=\"{cell}\"><f>1+1</f><v>421</v></c>"));
        Assert.True(r.Status == HttpStatusCode.Created, r.Text);
        Assert.Equal(AssumptionSetId.Compute(A with { Property = A.Property with { Keys = 421 } }), r.Str("id"));
    }

    [Theory]
    [InlineData("<f>1+1</f>", "")]
    [InlineData("<f>1+1</f><v></v>", "")]
    [InlineData("<f>\"x\"</f>", " t=\"str\"")]
    public async Task A_needed_formula_without_a_cached_value_is_422_naming_the_cell(string content, string type)
    {
        var cell = Address("Inputs", "Keys");
        await using var h = await Start();
        var r = await Post(h.C, RawCell(InputsPart, cell, $"<c r=\"{cell}\"{type}>{content}</c>"));
        Problem(r, HttpStatusCode.UnprocessableEntity, $"'Inputs' cell {cell}: the formula has no cached value.");
        Assert.Empty(h.Ledger);
    }

    [Fact]
    public void A_label_that_is_an_uncached_formula_is_named()
    {
        var cell = Address("Inputs", "Keys", 1);
        var e = Assert.Throws<WorkbookException>(() => WorkbookReader.ReadAssumptions(new MemoryStream(RawCell(InputsPart, cell, $"<c r=\"{cell}\"><f>A1</f></c>"))));
        Assert.Contains($"'Inputs' cell {cell}", e.Message);
    }

    [Fact]
    public void Checkpoints_follow_the_same_formula_rule()
    {
        var names = A.Scenarios.Select(s => s.Name).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"hue-{Guid.NewGuid():N}.xlsx");
        try
        {
            File.WriteAllBytes(path, RawCell("xl/worksheets/sheet11.xml", "B5", "<c r=\"B5\"><f>1+1</f><v>123</v></c>"));
            Assert.Equal(123, WorkbookReader.ReadCheckpoints(path, names)[0].ByScenario["Base"]);
            File.WriteAllBytes(path, RawCell("xl/worksheets/sheet11.xml", "B5", "<c r=\"B5\"><f>1+1</f></c>"));
            Assert.Contains("'Reconciliation' cell B5", Assert.Throws<WorkbookException>(() => WorkbookReader.ReadCheckpoints(path, names)).Message);
        }
        finally { File.Delete(path); }
    }

    // ---- wrong content

    private const string Secret = "zq-secret-cell-value";

    [Theory]
    [InlineData("sheet", "Sheet 'Operating Inputs' is missing.")]
    [InlineData("label", "label 'Keys' not found")]
    [InlineData("text", "expected a number.")]
    [InlineData("date", "expected a date.")]
    [InlineData("month", "seasonality month must be a whole number from 1 to 12.")]
    [InlineData("scenario", "duplicate scenario.")]
    [InlineData("vector", "no row for the scenario in 'Inputs' cell")]
    [InlineData("engine", "hold period 5")]
    public async Task Wrong_content_is_422_naming_it_without_echoing_a_value(string what, string detail)
    {
        var file = Edited(wb =>
        {
            var inputs = wb.Worksheet("Inputs");
            var header = inputs.RowsUsed().First(r => r.Cell(1).GetString().Trim() == "INPUT PARAMETER");
            switch (what)
            {
                case "sheet": wb.Worksheet("Operating Inputs").Name = Secret; break;
                case "label": Labelled(wb, "Inputs", "Keys", 1).Value = Secret; break;
                case "text": Labelled(wb, "Inputs", "Keys").Value = Secret; break;
                case "date": Labelled(wb, "Inputs", "Acquisition date").Value = Secret; break;
                case "month":
                    var ops = wb.Worksheet("Operating Inputs");
                    var seasonality = ops.RowsUsed().First(r => r.Cell(4).GetString().Trim() == "Seasonality weight").RowNumber();
                    ops.Cell(seasonality + 1, 1).Value = 13;
                    break;
                case "scenario": header.Cell(3).Value = header.Cell(2).GetString(); break;
                case "vector": header.Cell(3).Value = Secret; break;
                case "engine":
                    inputs.RowsUsed().First(r => r.RowNumber() > header.RowNumber() && r.Cell(1).GetString().StartsWith("Hold period")).Cell(2).Value = 5;
                    break;
            }
        });
        await using var h = await Start(args: ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Trace", "--Logging:LogLevel:Microsoft.AspNetCore=Trace"]);
        var req = new ByteArrayContent(file);
        req.Headers.ContentType = new(ApiHost.XlsxContentType);
        req.Headers.ContentDisposition = new("attachment") { FileName = "zq-secret-file.xlsx" };
        var res = await h.C.PostAsync(Url, req);
        var r = new Reply(res.StatusCode, await res.Content.ReadAsStringAsync(), res);
        Problem(r, HttpStatusCode.UnprocessableEntity, detail);
        Assert.DoesNotContain("zq-secret", r.Text);
        Assert.NotEmpty(h.Log.Lines);
        Assert.DoesNotContain("zq-secret", string.Join("\n", h.Log.Lines));
        Assert.Empty(h.Ledger);
    }

    [Fact]
    public async Task A_file_the_library_cannot_read_gets_a_fixed_reason_that_quotes_nothing()
    {
        var cell = Address("Inputs", "Keys");
        var file = RawCell(InputsPart, cell, $"<c r=\"{cell}\" s=\"{Secret}\"><v>1</v></c>");
        PackageInspector.Inspect(file); // it is the library that fails, with a message that quotes the file
        var thrown = Assert.ThrowsAny<Exception>(() => WorkbookReader.ReadAssumptions(new MemoryStream(file)));
        Assert.IsNotType<WorkbookException>(thrown);
        Assert.Contains(Secret, thrown.ToString());

        await using var h = await Start(args: ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Trace", "--Logging:LogLevel:Microsoft.AspNetCore=Trace"]);
        var r = await Post(h.C, file);
        Problem(r, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("The workbook could not be read.", r.Str("detail"));
        Assert.DoesNotContain(Secret, r.Text);
        Assert.DoesNotContain(Secret, string.Join("\n", h.Log.Lines));
    }

    // ---- reader rules

    private static void Refused(Action<XLWorkbook> edit, string detail)
    {
        var e = Assert.Throws<WorkbookException>(() => WorkbookReader.ReadAssumptions(new MemoryStream(Edited(edit))));
        Assert.Contains(detail, e.Message);
    }

    private static int RowOf(IXLWorksheet ws, string text, int column = 1) =>
        ws.RowsUsed().First(r => r.Cell(column).GetString().Trim() == text).RowNumber();

    [Theory]
    [InlineData("Keys")]
    [InlineData("Base year (actuals)")]
    [InlineData("PIP start month")]
    [InlineData("PIP duration")]
    [InlineData("Interest-only period")]
    [InlineData("Hold period")]
    public void Every_whole_number_input_must_be_one_in_int_range(string label)
    {
        foreach (var bad in new[] { 24.5, 3e9, -3e9 })
            Refused(wb =>
            {
                var ws = wb.Worksheet("Inputs");
                ws.Cell(ws.RowsUsed().First(r => r.Cell(1).GetString().StartsWith(label)).RowNumber(), 2).Value = bad;
            }, "expected a whole number.");
    }

    [Fact]
    public void Vector_years_must_be_whole_numbers() => Refused(wb =>
    {
        var ws = wb.Worksheet("Inputs");
        var title = ws.RowsUsed().First(r => r.Cell(1).GetString().StartsWith("ANNUAL OCCUPANCY")).RowNumber();
        ws.Cell(title + 1, 2).Value = 2027.5;
    }, "expected a whole number.");

    [Theory]
    [InlineData("ANNUAL OCCUPANCY")]
    [InlineData("ANNUAL ADR GROWTH")]
    public void A_scenario_row_repeated_in_a_vector_table_is_refused(string table) => Refused(wb =>
    {
        var ws = wb.Worksheet("Inputs");
        var title = ws.RowsUsed().First(r => r.Cell(1).GetString().StartsWith(table)).RowNumber();
        ws.Cell(title + 3, 1).Value = ws.Cell(title + 2, 1).GetString();
    }, "duplicate scenario.");

    [Theory]
    [InlineData(0, "whole number from 1 to 12")]
    [InlineData(1.5, "whole number from 1 to 12")]
    [InlineData(1, "seasonality month is repeated.")]
    public void A_bad_or_repeated_seasonality_month_is_refused(double month, string detail) => Refused(wb =>
    {
        var ws = wb.Worksheet("Operating Inputs");
        ws.Cell(RowOf(ws, "Seasonality weight", 4) + 2, 1).Value = month; // the second month row
    }, detail);

    [Theory]
    [InlineData("rev")]
    [InlineData("OTHER")]
    public void An_unknown_operating_line_section_is_refused(string section) => Refused(wb =>
    {
        var ws = wb.Worksheet("Operating Inputs");
        ws.Cell(RowOf(ws, "Line item", 2) + 1, 3).Value = section;
    }, "unknown section");

    [Theory]
    [InlineData("name")]
    [InlineData("label column")]
    [InlineData("header")]
    public void An_error_value_used_as_text_is_refused_naming_the_cell(string where)
    {
        var address = "";
        Refused(wb =>
        {
            var inputs = wb.Worksheet("Inputs");
            var ops = wb.Worksheet("Operating Inputs");
            var cell = where switch
            {
                "name" => Labelled(wb, "Inputs", "Property"),
                "label column" => ops.Cell(RowOf(ops, "Line item", 2) + 1, 2),
                _ => inputs.Cell(RowOf(inputs, "INPUT PARAMETER"), 3),
            };
            cell.Value = XLError.CellReference;
            address = cell.Address.ToString()!;
        }, "the cell holds an error value.");
        Assert.NotEmpty(address);
    }

    // ---- publish rules, the same on both routes

    private static Task<Reply> Json(Host h, Assumptions a) => Send(h.C, HttpMethod.Post, "/v1/assumption-sets", null, JsonSerializer.Serialize(a, ApiHost.Json));

    private static Assumptions Each(Func<ScenarioInputs, ScenarioInputs> change) => A with { Scenarios = A.Scenarios.Select(change).ToArray() };

    private static IReadOnlyList<T> Times<T>(int n, Func<int, T> make) => Enumerable.Range(0, n).Select(make).ToArray();

    public static TheoryData<string, string> OverLimits() => new()
    {
        { "scenarios", "at most 20 scenarios" },
        { "lines", "at most 500 operating lines" },
        { "items", "at most 50 percent-of-revenue items" },
        { "years", "at most 50 years per scenario" },
        { "occupancy", "at most 50 years per scenario" },
        { "growth", "at most 50 years per scenario" },
        { "property", "at most 100 characters" },
        { "scenario name", "at most 100 characters" },
        { "line driver", "at most 100 characters" },
        { "section", "at most 100 characters" },
        { "base", "at most 100 characters" },
        { "control", "no control characters" },
        { "null line", "missing or invalid value" },
        { "unknown base", "unknown base" },
        { "overflow", "too large or not finite" },
        { "infinite flows", "too large or not finite" },
    };

    private static Assumptions Broken(string what)
    {
        var longText = new string('x', PublishAssumptionSet.MaxTextLength + 1);
        Assumptions Line(Func<OperatingLine, OperatingLine> change) => A with { Lines = [change(A.Lines[0]), .. A.Lines.Skip(1)] };
        Assumptions Item(Func<PercentItem, PercentItem> change) => A with { PercentItems = [change(A.PercentItems[0]), .. A.PercentItems.Skip(1)] };
        return what switch
        {
            "scenarios" => A with { Scenarios = Times(PublishAssumptionSet.MaxScenarios + 1, i => A.Scenarios[0] with { Name = "S" + i }) },
            "lines" => A with { Lines = Times(PublishAssumptionSet.MaxLines + 1, i => A.Lines[0] with { Code = "L" + i }) },
            "items" => A with { PercentItems = Times(PublishAssumptionSet.MaxPercentItems + 1, i => A.PercentItems[0] with { Code = "P" + i }) },
            "years" => Each(s => s with { Years = Times(51, i => 2027 + i) }),
            "occupancy" => Each(s => s with { Occupancy = Times(51, _ => 0.6) }),
            "growth" => Each(s => s with { AdrGrowth = Times(51, _ => 0.03) }),
            "property" => A with { Property = A.Property with { Name = longText } },
            "scenario name" => A with { Scenarios = [A.Scenarios[0] with { Name = longText }] },
            "line driver" => Line(l => l with { Driver = longText }),
            "section" => Line(l => l with { Section = longText }),
            "base" => Item(p => p with { Base = longText }),
            "control" => Line(l => l with { Name = "a\nb" }),
            "null line" => A with { Lines = [null!] },
            "unknown base" => Item(p => p with { Base = "zq-secret-base" }),
            // an infinite sale less an infinite cost of sale: the IRR solver throws ArithmeticException
            "overflow" => Each(s => s with { ExitCapRate = 1e-310 }),
            _ => Each(s => s with { PurchasePrice = 1.7e308, ClosingCosts = 1.7e308 }),
        };
    }

    [Theory]
    [MemberData(nameof(OverLimits))]
    public async Task A_set_over_a_limit_or_beyond_the_engine_is_422_on_the_json_route(string what, string detail)
    {
        await using var h = await Start();
        var r = await Json(h, Broken(what));
        Problem(r, HttpStatusCode.UnprocessableEntity, detail);
        Assert.DoesNotContain("zq-secret", r.Text);
        Assert.Empty(h.Ledger);
    }

    [Fact]
    public async Task Sets_at_the_limits_publish()
    {
        await using var h = await Start();
        var extra = Times(PublishAssumptionSet.MaxLines - A.Lines.Count, i => A.Lines[0] with { Code = "L" + i, Section = "FIXED", BaseAmount = 1, Driver = "OCC_ROOMS" });
        var items = Times(PublishAssumptionSet.MaxPercentItems - A.PercentItems.Count, i => A.PercentItems[0] with { Code = "P" + i, Pct = 0.001 }); // small rates: the rates must add up to at most 1
        var a = A with
        {
            Property = A.Property with { Name = new string('n', PublishAssumptionSet.MaxTextLength) },
            Lines = [.. A.Lines, .. extra],
            PercentItems = [.. A.PercentItems, .. items],
            Scenarios = Times(PublishAssumptionSet.MaxScenarios, i => A.Scenarios[0] with
            {
                Name = "S" + i,
                Years = Times(50, y => 2027 + y), Occupancy = Times(50, _ => 0.6), AdrGrowth = Times(50, _ => 0.03),
            }),
        };
        var r = await Json(h, a);
        Assert.True(r.Status == HttpStatusCode.Created, r.Text);
    }

    [Fact]
    public async Task The_excel_route_applies_the_same_publish_limits()
    {
        await using var h = await Start();
        var r = await Post(h.C, Edited(wb => Labelled(wb, "Inputs", "Property").Value = new string('x', 101)));
        Problem(r, HttpStatusCode.UnprocessableEntity, "at most 100 characters");
        Assert.Empty(h.Ledger);
    }

    [Theory]
    [InlineData("unknown base", "unknown base")]
    [InlineData("overflow", "too large or not finite")]
    public void A_run_reports_the_same_engine_failures_as_invalid_assumptions(string what, string detail)
    {
        var a = Broken(what);
        var submit = new SubmitRun(new InMemoryRunStore(), new SystemClock());
        var e = Assert.Throws<InvalidAssumptionsException>(() => submit.Submit(a, a.Scenarios[0]));
        Assert.Contains(detail, e.Message);
    }

    // ---- limits

    /// <summary>A valid workbook inside every package limit that takes real memory and time to load.</summary>
    private static readonly Lazy<byte[]> Heavy = new(() => Edited(wb =>
    {
        var ws = wb.AddWorksheet("Padding");
        for (var r = 1; r <= 17_000; r++)
            for (var c = 1; c <= 10; c++) ws.Cell(r, c).Value = $"padding {r} {c} {new string('x', 40)}";
    }));

    private static void LimitWarning(Host h, params string[] causes) =>
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && causes.Any(c => e.Text.StartsWith($"Workbook parsing failed: {c} ")));

    [Fact]
    public async Task A_heavy_workbook_publishes_with_the_default_limits()
    {
        await using var h = await Start();
        var r = await Post(h.C, Heavy.Value);
        Assert.True(r.Status == HttpStatusCode.Created, r.Text);
    }

    [Fact]
    public async Task Past_the_heap_limit_is_422_and_the_api_stays_healthy()
    {
        // 48 MB is below the configurable minimum: only this seam can set it.
        await using var h = await Start(Limits(heapMb: 48, concurrent: 1));
        Problem(await Post(h.C, Heavy.Value), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        LimitWarning(h, "MemoryLimit", "ChildCrash");
        Assert.Equal(HttpStatusCode.OK, (await h.C.GetAsync("/health/ready")).StatusCode);
        Problem(await Post(h.C, []), HttpStatusCode.UnprocessableEntity, "zip"); // a child still starts under that limit, and the slot is free
        Assert.Empty(h.Ledger);
    }

    [Fact]
    public async Task Past_the_time_limit_while_parsing_is_422_and_the_child_is_killed()
    {
        var pid = 0;
        await using var h = await Start(Limits(seconds: 0.7, concurrent: 1) with { BeforeInput = (p, _) => { pid = p.Id; return Task.CompletedTask; } });
        Problem(await Post(h.C, Heavy.Value), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        LimitWarning(h, "TimeLimit");
        Assert.NotEqual(0, pid);
        Assert.True(Gone(pid), "the child is still running");
        Assert.Empty(h.Ledger);
    }

    [Fact]
    public async Task A_child_that_never_finishes_is_killed_at_the_time_limit()
    {
        var hold = new Hold();
        // long enough for the real parse that follows, also on a loaded machine
        await using var h = await Start(Limits(seconds: 10, concurrent: 1) with { BeforeInput = hold.Hook });
        Problem(await Post(h.C, Reference), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        Assert.True(Gone(await hold.Started.Task), "the child is still running");
        LimitWarning(h, "TimeLimit");
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status); // slot released
    }

    [Fact]
    public async Task A_crashed_child_and_too_much_output_are_422_with_the_cause_logged()
    {
        await using var crash = await Start(Limits() with { BeforeInput = (p, _) => { p.Kill(); return Task.CompletedTask; } });
        Problem(await Post(crash.C, Reference), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        LimitWarning(crash, "ChildCrash");

        await using var chatty = await Start(Limits() with { MaxOutputBytes = 100 });
        Problem(await Post(chatty.C, Reference), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        LimitWarning(chatty, "OutputLimit");
    }

    [Fact]
    public async Task A_child_that_cannot_start_is_500_and_releases_the_slot()
    {
        await using var h = await Start(Limits(concurrent: 1) with { Executable = Path.Combine(AppContext.BaseDirectory, "no-such-program") });
        for (var i = 0; i < 2; i++) Problem(await Post(h.C, Reference), HttpStatusCode.InternalServerError, "unexpected");
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Text == "Workbook parser could not be started: Win32Exception");
    }

    /// <summary>A broken copy of the application: without its runtime configuration the host cannot start it; without a library, loading fails while parsing.</summary>
    [Theory]
    [InlineData("HotelUnderwriting.Api.runtimeconfig.json", "Workbook parser did not start up")]
    [InlineData("ClosedXML.dll", "Workbook parser failed on the server: FileNotFoundException")]
    public async Task A_broken_installation_is_500_not_a_bad_file(string missing, string logged)
    {
        var broken = Directory.CreateTempSubdirectory("hue-broken-").FullName;
        try
        {
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            {
                var name = Path.GetFileName(file);
                if (name == missing || name.EndsWith(".deps.json")) continue;
                if (name.EndsWith(".dll") || name.StartsWith("HotelUnderwriting.Api.")) File.Copy(file, Path.Combine(broken, name));
            }
            await using var h = await Start(Limits(concurrent: 1) with { ApplicationDirectory = broken });
            for (var i = 0; i < 2; i++) Problem(await Post(h.C, Reference), HttpStatusCode.InternalServerError, "unexpected");
            Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Text.StartsWith(logged));
            Assert.DoesNotContain(h.Log.Entries, e => e.Text.StartsWith("Workbook parsing failed"));
        }
        finally
        {
            try { Directory.Delete(broken, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_child_that_cannot_be_killed_does_not_keep_the_slot_and_ends_itself()
    {
        var hold = new Hold();
        await using var h = await Start(Limits(seconds: 1, concurrent: 1) with { BeforeInput = hold.Hook, Kill = _ => throw new System.ComponentModel.Win32Exception() });
        Problem(await Post(h.C, Reference), HttpStatusCode.UnprocessableEntity, WorkbookParser.LimitsMessage);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Text == "Workbook parser process could not be killed: Win32Exception");
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Text.StartsWith("Workbook parser did not finish"));
        Assert.Equal(1, h.App.Services.GetRequiredService<WorkbookParser>().Slots.CurrentCount);
        var pid = await hold.Started.Task;
        await Until(() => Gone(pid), "the child did not end itself");
    }

    // ---- busy, abort, stop

    [Fact]
    public async Task Over_the_concurrency_cap_is_503_with_retry_after_until_a_slot_is_free()
    {
        var hold = new Hold();
        await using var h = await Start(Limits(concurrent: 1) with { BeforeInput = hold.Hook });
        var first = Post(h.C, Reference);
        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var head = await HeadersOnly(h.App); // no body was sent, so none was buffered
        Assert.StartsWith("HTTP/1.1 503 ", head);
        Assert.Contains("\r\nRetry-After: 5\r\n", head);
        var busy = await Post(h.C, Reference);
        Problem(busy, HttpStatusCode.ServiceUnavailable, "Too many uploads");
        Assert.Equal(TimeSpan.FromSeconds(5), busy.Message.Headers.RetryAfter!.Delta);
        hold.Release.SetResult();
        Assert.Equal(HttpStatusCode.Created, (await first).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(h.C, Reference)).Status);
    }

    [Fact]
    public async Task A_client_that_leaves_mid_parse_gets_its_child_killed_and_its_slot_released()
    {
        var hold = new Hold();
        await using var h = await Start(Limits(concurrent: 1) with { BeforeInput = hold.Hook });
        using var leave = new CancellationTokenSource();
        var request = Post(h.C, Reference, ct: leave.Token);
        var pid = await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Until(() => Gone(pid), "the child is still running");
        var slots = h.App.Services.GetRequiredService<WorkbookParser>().Slots;
        await Until(() => slots.CurrentCount == 1, "the slot was not released");
        Assert.Empty(h.Ledger);
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status);
    }

    [Fact]
    public async Task A_client_that_leaves_while_the_child_is_really_parsing_gets_it_killed()
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await Start(Limits(concurrent: 1) with { BeforeInput = (p, _) => { started.TrySetResult(p.Id); return Task.CompletedTask; } });
        using var leave = new CancellationTokenSource();
        var request = Post(h.C, Heavy.Value, ct: leave.Token);
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500); // the heavy file takes seconds to load
        Assert.False(Gone(pid), "the child had already finished");
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Until(() => Gone(pid), "the child is still running");
        var slots = h.App.Services.GetRequiredService<WorkbookParser>().Slots;
        await Until(() => slots.CurrentCount == 1, "the slot was not released");
        Assert.Empty(h.Ledger);
    }

    private static async Task<string> ToEnd(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var text = new MemoryStream();
        try { await stream.CopyToAsync(text, timeout.Token); }
        catch (IOException) { } // the server closed the connection with a reset
        return Encoding.UTF8.GetString(text.ToArray());
    }

    [Fact]
    public async Task A_body_that_does_not_arrive_in_time_is_408_closes_the_connection_and_releases_the_slot()
    {
        var children = 0;
        await using var h = await Start(Limits(concurrent: 1) with { BodyTimeout = TimeSpan.FromSeconds(1), BeforeInput = (_, _) => { Interlocked.Increment(ref children); return Task.CompletedTask; } });
        var slots = h.App.Services.GetRequiredService<WorkbookParser>().Slots;
        using var tcp = new TcpClient();
        var stream = await Open(h.App, tcp, Reference.Length);
        await stream.WriteAsync(Reference.AsMemory(0, 1000));
        var answer = await ToEnd(stream); // returns only because the server closes the connection
        Assert.StartsWith("HTTP/1.1 408 ", answer);
        Assert.Contains("\r\nConnection: close\r\n", answer);
        Assert.Contains("not received in time", answer);
        await Until(() => slots.CurrentCount == 1, "the slot was not released");
        Assert.Equal(0, children);
        Assert.Empty(h.Ledger);
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status);
    }

    [Fact]
    public async Task A_client_reset_mid_body_is_not_a_server_error()
    {
        await using var h = await Start(Limits(concurrent: 1));
        var slots = h.App.Services.GetRequiredService<WorkbookParser>().Slots;
        using (var tcp = new TcpClient())
        {
            var stream = await Open(h.App, tcp, Reference.Length);
            await stream.WriteAsync(Reference.AsMemory(0, 1000));
            await Until(() => slots.CurrentCount == 0, "the upload never took the slot");
            tcp.LingerState = new LingerOption(true, 0); // close with a reset
        }
        await Until(() => slots.CurrentCount == 1, "the slot was not released");
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status);
        Assert.DoesNotContain(h.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_chunked_body_far_over_10_mb_gets_the_10_mb_message()
    {
        await using var h = await Start();
        var uri = new Uri(h.App.Urls.First());
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, uri.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST {Url} HTTP/1.1\r\nHost: localhost\r\nContent-Type: {ApiHost.XlsxContentType}\r\nTransfer-Encoding: chunked\r\n\r\n"));
        var sending = Task.Run(async () =>
        {
            var chunk = new byte[1 << 20];
            try
            {
                for (var i = 0; i < 12; i++)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("100000\r\n"));
                    await stream.WriteAsync(chunk);
                    await stream.WriteAsync("\r\n"u8.ToArray());
                }
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or OperationCanceledException) { } // the server stops reading once it has refused, and the test closes the client after reading the answer; the answer is asserted below
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var answer = new StringBuilder();
        var buffer = new byte[4096];
        int n;
        while (!answer.ToString().Contains('}') && (n = await stream.ReadAsync(buffer, timeout.Token)) > 0) answer.Append(Encoding.ASCII.GetString(buffer, 0, n));
        Assert.StartsWith("HTTP/1.1 413 ", answer.ToString());
        Assert.Contains("Request body exceeds 10 MB.", answer.ToString());
        tcp.Close();
        await sending;
    }

    [Fact]
    public async Task The_built_executable_serves_an_upload_with_itself_as_the_child()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "HotelUnderwriting.Api" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var info = new ProcessStartInfo(exe, ["--urls=http://127.0.0.1:0", "--Auth:Mode=none", "--Persistence:ConnectionString="])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory,
        };
        using var server = Process.Start(info)!;
        try
        {
            var url = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.Split("Now listening on: ") is [_, var address]) url.TrySetResult(address.Trim());
            };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            using var c = new HttpClient { BaseAddress = new Uri(await url.Task.WaitAsync(TimeSpan.FromSeconds(30))) };
            var r = await Post(c, Reference);
            Assert.True(r.Status == HttpStatusCode.Created, r.Text);
            Assert.Equal(AssumptionSetId.Compute(A), r.Str("id"));
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            Assert.True(server.WaitForExit(15000));
        }
    }

    [Fact]
    public async Task A_client_that_leaves_mid_body_releases_its_slot()
    {
        await using var h = await Start(Limits(concurrent: 1));
        var slots = h.App.Services.GetRequiredService<WorkbookParser>().Slots;
        using (var tcp = new TcpClient())
        {
            var stream = await Open(h.App, tcp, Reference.Length);
            await stream.WriteAsync(Reference.AsMemory(0, 1000));
            await Until(() => slots.CurrentCount == 0, "the upload never took the slot");
        }
        await Until(() => slots.CurrentCount == 1, "the slot was not released");
        Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status);
    }

    [Fact]
    public async Task Stopping_the_host_mid_parse_kills_the_child()
    {
        var hold = new Hold();
        await using var h = await Start(Limits(seconds: 120, concurrent: 1) with { BeforeInput = hold.Hook });
        var request = Post(h.C, Reference);
        var pid = await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        await h.App.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"stop took {watch.Elapsed}");
        Assert.True(Gone(pid), "the child is still running");
        Problem(await request, HttpStatusCode.ServiceUnavailable, "stopping");
        Assert.Equal(1, h.App.Services.GetRequiredService<WorkbookParser>().Slots.CurrentCount);
    }

    // ---- child environment, configuration, contract

    [Fact]
    public async Task The_child_gets_no_connection_string_auth_or_telemetry_secrets()
    {
        // made-up names under the sensitive prefixes: nothing reads them, so tests running alongside are not affected
        string[] secrets = ["Persistence__ZqSecret", "ConnectionStrings__ZqSecret", "Auth__ZqSecret", "OTEL_ZQ_HEADERS", "PGZQ_SECRET", "DOTNET_ZQ_SECRET"];
        var before = secrets.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Dictionary<string, string?>? seen = null;
        try
        {
            foreach (var n in secrets) Environment.SetEnvironmentVariable(n, "zq-secret");
            await using var h = await Start(Limits() with { BeforeInput = (p, _) => { seen = new(p.StartInfo.Environment); return Task.CompletedTask; } });
            Assert.Equal(HttpStatusCode.Created, (await Post(h.C, Reference)).Status);
        }
        finally
        {
            foreach (var (n, value) in before) Environment.SetEnvironmentVariable(n, value);
        }
        Assert.NotNull(seen);
        Assert.DoesNotContain("zq-secret", seen.Values);
        Assert.DoesNotContain(seen.Keys, k => new[] { "Persistence", "ConnectionString", "Auth", "OTEL", "PG" }.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("20000000", seen["DOTNET_GCHeapHardLimit"]); // 512 MB
    }

    [Theory]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "301")]
    [InlineData("TimeoutSeconds", "ten")]
    [InlineData("TimeoutSeconds", "")]
    [InlineData("MaxHeapMb", "383")]
    [InlineData("BodyTimeoutSeconds", "0")]
    [InlineData("BodyTimeoutSeconds", "301")]
    [InlineData("BodyTimeoutSeconds", "soon")]
    [InlineData("MaxHeapMb", "4097")]
    [InlineData("MaxConcurrent", "0")]
    [InlineData("MaxConcurrent", "17")]
    [InlineData("MaxConcurrent", "-1")]
    [InlineData("MaxConcurrent", "1.5")]
    public void A_bad_upload_setting_is_a_startup_error(string name, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ApiHost.Build([$"--Upload:{name}={value}"]));
        Assert.Contains($"Upload:{name} must be", e.Message);
    }

    [Fact]
    public async Task Upload_settings_have_defaults_and_accept_their_bounds()
    {
        await using var defaults = ApiHost.Build([]);
        Assert.Equal(Limits(), defaults.Services.GetRequiredService<UploadLimits>());
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.Services.GetRequiredService<UploadLimits>().BodyTimeout);
        await using var low = ApiHost.Build(["--Upload:TimeoutSeconds=1", "--Upload:MaxHeapMb=384", "--Upload:MaxConcurrent=1", "--Upload:BodyTimeoutSeconds=1"]);
        Assert.Equal(Limits(1, 384, 1) with { BodyTimeout = TimeSpan.FromSeconds(1) }, low.Services.GetRequiredService<UploadLimits>());
        await using var high = ApiHost.Build(["--Upload:TimeoutSeconds=300", "--Upload:MaxHeapMb=4096", "--Upload:MaxConcurrent=16", "--Upload:BodyTimeoutSeconds=300"]);
        Assert.Equal(Limits(300, 4096, 16) with { BodyTimeout = TimeSpan.FromSeconds(300) }, high.Services.GetRequiredService<UploadLimits>());
    }

    [Fact]
    public async Task OpenApi_documents_the_upload_content_type_and_statuses()
    {
        await using var h = await Start();
        var op = JsonNode.Parse(await h.C.GetStringAsync("/openapi/v1.json"))!["paths"]![Url]!["post"]!;
        Assert.Equal([ApiHost.XlsxContentType], op["requestBody"]!["content"]!.AsObject().Select(c => c.Key));
        var statuses = op["responses"]!.AsObject().Select(r => r.Key).ToArray();
        foreach (var status in new[] { "201", "200", "400", "401", "403", "413", "415", "422", "503" }) Assert.Contains(status, statuses);
        Assert.NotNull(op["responses"]!["503"]!["headers"]!["Retry-After"]);
    }
}
