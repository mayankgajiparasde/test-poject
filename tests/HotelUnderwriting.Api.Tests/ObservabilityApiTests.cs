using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace HotelUnderwriting.Api.Tests;

/// <summary>Probes, golden self-test, export switch, plan reuse and shutdown on the in-memory host. Each test builds its own host so the shared fixture is untouched.</summary>
public class ObservabilityApiTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now = now;
        public DateTimeOffset UtcNow => Now;
    }

    private static async Task<WebApplication> Start(params string[] extra)
    {
        var app = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0" }.Concat(extra).ToArray());
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.First()) };

    [Fact]
    public async Task With_no_endpoint_no_opentelemetry_provider_is_registered()
    {
        await using var app = await Start();
        Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.Null(app.Services.GetService<MeterProvider>());
    }

    [Fact]
    public async Task With_an_endpoint_the_host_serves_requests_even_though_export_fails()
    {
        await using var app = await Start("--OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:1");
        Assert.NotNull(app.Services.GetService<TracerProvider>());
        Assert.NotNull(app.Services.GetService<MeterProvider>());
        using var c = Client(app);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Http_protobuf_export_reaches_the_traces_and_metrics_paths()
    {
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = b.Build();
        collector.Run(ctx => { paths.Add(ctx.Request.Path.Value!); return Task.CompletedTask; });
        await collector.StartAsync();
        await using var app = await Start($"--OTEL_EXPORTER_OTLP_ENDPOINT={collector.Urls.First()}", "--OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf");
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        // The server's span ends after the client has the response, and a flush can time out on a loaded machine: flush until both arrive.
        for (var i = 0; i < 20 && !(paths.Contains("/v1/traces") && paths.Contains("/v1/metrics")); i++)
        {
            app.Services.GetRequiredService<TracerProvider>().ForceFlush(5000);
            app.Services.GetRequiredService<MeterProvider>().ForceFlush(5000);
            await Task.Delay(250);
        }
        Assert.Contains("/v1/traces", paths);
        Assert.Contains("/v1/metrics", paths);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://collector.example/path")]
    [InlineData("/relative")]
    public void A_bad_endpoint_is_a_startup_error_that_does_not_echo_the_value(string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(new[] { $"--OTEL_EXPORTER_OTLP_ENDPOINT={value}" }));
        Assert.DoesNotContain(value, e.ToString());
        Assert.DoesNotContain("collector.example", e.ToString());
    }

    [Fact]
    public async Task Startup_probe_is_200_at_once_with_in_memory_persistence()
    {
        await using var app = await Start();
        using var c = Client(app);
        var res = await c.GetAsync("/health/startup");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("started", JsonNode.Parse(await res.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ready_runs_the_golden_scenario_and_a_corrupted_constant_turns_it_503_without_a_figure()
    {
        await using var app = await Start();
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
        var golden = app.Services.GetRequiredService<GoldenSelfTest>();
        Assert.Equal(1, golden.Evaluations);

        golden.ExpectedLeveredIrr += 0.01;   // test hook: resets the cached verdict
        var bad = await c.GetAsync("/health/ready");
        var text = await bad.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        Assert.Equal(503, JsonNode.Parse(text)!["status"]!.GetValue<int>());
        foreach (var figure in new[] { "0.18", "0.19", "24966415", "24,966" }) Assert.DoesNotContain(figure, text);

        golden.ExpectedLeveredIrr = GoldenSelfTest.GoldenLeveredIrr;
        golden.ExpectedEquityAtClose += 1;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/health/ready")).StatusCode);   // equity is checked too
        golden.ExpectedEquityAtClose = GoldenSelfTest.GoldenEquityAtClose;
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public void The_golden_verdict_is_cached_for_60_seconds()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        var golden = new GoldenSelfTest(clock);
        Assert.True(golden.Check());
        Assert.True(golden.Check());
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.True(golden.Check());
        Assert.Equal(1, golden.Evaluations);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(golden.Check());
        Assert.Equal(2, golden.Evaluations);
    }

    [Fact]
    public async Task The_openapi_document_lists_the_startup_probe()
    {
        await using var app = await Start();
        using var c = Client(app);
        var doc = JsonNode.Parse(await c.GetStringAsync("/openapi/v1.json"))!;
        Assert.NotNull(doc["paths"]!["/health/startup"]!["get"]);
        Assert.NotNull(doc["paths"]!["/health/startup"]!["get"]!["responses"]!["503"]);
        // method and path equality with the registered routes is asserted by ApiTests.OpenApi_document_is_served_and_matches_registered_routes
    }

    private static string Sweep(string setId, int cases) =>
        $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","cases":[{{string.Join(",", Enumerable.Repeat("""{"overrides":[{"path":"interestRate","value":0.0712}]}""", cases))}}]}""";

    [Fact]
    public async Task A_three_chunk_job_is_planned_once_and_the_results_endpoint_reuses_the_plan()
    {
        await using var app = await Start();
        using var c = Client(app);
        var set = A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + 77 } : s).ToArray() };
        var setId = await SweepApiTests.Publish(c, set);
        var (res, json) = await SweepApiTests.Send(c, HttpMethod.Post, "/v1/sweeps", Sweep(setId, 2 * SweepLimits.ChunkSize + 1));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(3, json["chunkCount"]!.GetValue<int>());
        var jobId = json["jobId"]!.GetValue<string>();
        await SweepApiTests.WaitFor(c, jobId);
        var plans = app.Services.GetRequiredService<SweepPlanCache>();
        Assert.Equal(1, plans.Builds);   // the three chunks built it once
        var (_, first) = await SweepApiTests.Send(c, HttpMethod.Get, $"/v1/jobs/{jobId}/results?limit=400");
        var next = first["next"]!.GetValue<int>();
        var (_, second) = await SweepApiTests.Send(c, HttpMethod.Get, $"/v1/jobs/{jobId}/results?limit=400&after={next}");
        Assert.Equal((400, 400), (first["items"]!.AsArray().Count, second["items"]!.AsArray().Count));
        Assert.Equal(1, plans.Builds);   // pages reused the cached plan
    }

    private sealed class HookRunStore(IRunStore inner, Action<int> afterSave) : IRunStore
    {
        private int _saves;
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record) { var r = inner.SaveIfAbsent(record); afterSave(Interlocked.Increment(ref _saves)); return r; }
    }

    [Fact]
    public async Task Stopping_the_worker_service_mid_chunk_releases_the_chunk_within_5_seconds()
    {
        var clock = new SystemClock();
        var ledger = new InMemoryLedger();
        var sets = new InMemoryAssumptionSetStore(ledger);
        var jobs = new InMemoryJobStore(ledger);
        var setId = new PublishAssumptionSet(sets, clock, ledger).Publish(A).Record.Id;
        var started = new TaskCompletionSource();
        var store = new HookRunStore(new InMemoryRunStore(ledger), n => { if (n == 10) started.TrySetResult(); Thread.Sleep(5); });
        var worker = new SweepWorker(jobs, sets, new SubmitRun(store, clock, ledger: ledger), clock);
        var cases = Enumerable.Range(0, SweepLimits.ChunkSize).Select(i => new SweepCase([new ScenarioOverride("interestRate", 0.05 + i * 0.00001)])).ToArray();
        var job = new SubmitSweep(jobs, clock).Submit(new SweepRequest(setId, "Base", cases, null)).Job;

        var service = new SweepWorkerService(worker, 1, NullLogger<SweepWorkerService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stop took {watch.Elapsed}");
        var p = jobs.GetProgress(job.Id)!;
        Assert.Equal((1, 0, 0), (p.Queued, p.Leased, p.Done));
        Assert.Equal(1, jobs.TryClaimChunk(clock.UtcNow, SweepWorker.Lease, "next")!.Attempts);   // released: the attempt was given back
    }
}
