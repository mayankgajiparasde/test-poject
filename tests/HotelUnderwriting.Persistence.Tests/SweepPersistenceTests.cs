using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Xunit.Abstractions;

namespace HotelUnderwriting.Persistence.Tests;

public class SweepPersistenceTests(ITestOutputHelper output)
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private sealed class Rig : IDisposable
    {
        public readonly TempSchema T = new();
        public readonly PostgresJobStore Jobs;
        public readonly SubmitSweep Submit;
        public readonly SweepWorker Worker;
        public readonly string SetId;

        public Rig()
        {
            var clock = new SystemClock();
            var ledger = new PostgresLedger(T.Cs, T.Schema);
            var sets = new PostgresAssumptionSetStore(T.Cs, T.Schema);
            Jobs = new PostgresJobStore(T.Cs, T.Schema);
            SetId = new PublishAssumptionSet(sets, clock, ledger).Publish(A).Record.Id;
            Submit = new SubmitSweep(Jobs, clock);
            Worker = new SweepWorker(Jobs, sets, new SubmitRun(new PostgresRunStore(T.Cs, T.Schema), clock, ledger: ledger), clock);
        }

        public void Dispose() => T.Dispose();
        public JobRecord Go(SweepRequest r, string? key = null) => Submit.Submit(r, key).Job;
        public SweepRequest Cases(params SweepCase[] cases) => new(SetId, "Base", cases, null);
    }

    private static SweepCase Rate(double v) => new([new ScenarioOverride("interestRate", v)]);

    [PgFact]
    public void Cases_sweep_results_match_direct_runs_and_ledger_rows_exist()
    {
        using var rig = new Rig();
        var job = rig.Go(rig.Cases(Rate(0.06), Rate(0.07), new SweepCase([new ScenarioOverride("holdMonths", 5)])));
        while (rig.Worker.ProcessOne("w1")) { }
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 3, 1), (p.Status, p.CompletedCases, p.FailedCases));
        var res = rig.Jobs.GetResults(job.Id, -1, 100);
        var baseS = A.Scenarios.First(s => s.Name == "Base");
        Assert.Equal(RunKey.Compute(A, baseS with { InterestRate = 0.07 }, EngineInfo.Version, WorkbookQuirks.None), res[1].RunKey);
        Assert.Contains("hold period 5", res[2].Error);
        var t = rig.T;
        Assert.Equal(1, t.Scalar<int>("select count(*) from ledger where action = 'sweep.submitted'"));
        Assert.Equal(2, t.Scalar<int>("select count(*) from ledger where action = 'run.computed'"));
        Assert.Equal(2, t.Scalar<int>("select count(*) from runs"));
        var row = t.Scalar<string>("select correlation_id from ledger where action = 'sweep.submitted'");
        Assert.Equal(job.CorrelationId, row);
        Assert.Equal(2, t.Scalar<int>($"select count(*) from ledger where actor = 'worker' and correlation_id = '{row}'"));
    }

    [PgFact]
    public void Four_workers_claim_each_of_20_chunks_once_and_drain_the_job()
    {
        using var rig = new Rig();
        var job = rig.Go(new SweepRequest(rig.SetId, "Base", null, new[]
        {
            new GridAxis("interestRate", Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.06 : 0.07).ToArray()),
            new GridAxis("exitCapRate", Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.075 : 0.08).ToArray()),
        }));
        Assert.Equal((10_000, 20), (job.CaseCount, job.ChunkCount));
        Assert.Equal(20, rig.T.Scalar<int>("select count(*) from job_chunks"));

        // pure claim race: every chunk leased exactly once, none twice
        var claimed = new ConcurrentBag<long>();
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w =>
        {
            while (rig.Jobs.TryClaimChunk(DateTimeOffset.UtcNow, SweepWorker.Lease, "claimer" + w) is { } c) claimed.Add(c.ChunkId);
        });
        Assert.Equal(20, claimed.Count);
        Assert.Equal(20, claimed.Distinct().Count());
        Assert.Equal(20, rig.T.Scalar<int>("select count(*) from job_chunks where state = 'leased' and attempts = 1"));
        Assert.Equal(20, rig.T.Scalar<int>("select count(distinct id) from job_chunks where lease_owner like 'claimer%'"));

        // release them (a fresh clock far in the future expires every lease), then drain with real workers
        var handled = 0;
        var far = DateTimeOffset.UtcNow.AddHours(1);
        var sets = new PostgresAssumptionSetStore(rig.T.Cs, rig.T.Schema);
        var worker = new SweepWorker(rig.Jobs, sets, new SubmitRun(new PostgresRunStore(rig.T.Cs, rig.T.Schema), new FakeClock(far), ledger: new PostgresLedger(rig.T.Cs, rig.T.Schema)), new FakeClock(far));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w => { while (worker.ProcessOne("w" + w)) Interlocked.Increment(ref handled); });
        output.WriteLine($"10000 cases, 4 workers, Postgres: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(20, handled);
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 20, 10_000), (p.Status, p.Done, p.CompletedCases));
        Assert.Equal(10_000, rig.T.Scalar<int>("select count(*) from job_results"));
        Assert.Equal(10_000, rig.T.Scalar<int>("select count(distinct case_index) from job_results"));
        Assert.Equal(20, rig.T.Scalar<int>("select count(*) from job_chunks where state = 'done' and attempts = 2"));
        Assert.Equal(4, rig.T.Scalar<int>("select count(*) from ledger where action = 'run.computed'"));
    }

    [PgFact]
    public void Expired_lease_is_reclaimed_attempts_counted_and_a_stale_completion_is_ignored()
    {
        using var rig = new Rig();
        var job = rig.Go(rig.Cases(Rate(0.06), Rate(0.07)));
        var lease = TimeSpan.FromSeconds(60);
        var first = rig.Jobs.TryClaimChunk(T0, lease, "w1")!;
        Assert.Null(rig.Jobs.TryClaimChunk(T0.AddSeconds(59), lease, "w2"));
        var second = rig.Jobs.TryClaimChunk(T0.AddSeconds(61), lease, "w2")!;
        Assert.Equal((first.ChunkId, 2), (second.ChunkId, second.Attempts));
        var rows = new[] { new CaseResult(0, "k0", null), new CaseResult(1, "k1", null) };
        Assert.False(rig.Jobs.CompleteChunk(first.ChunkId, "w1", rows));
        Assert.Equal(0, rig.T.Scalar<int>("select count(*) from job_results"));
        Assert.True(rig.Jobs.CompleteChunk(second.ChunkId, "w2", rows));
        Assert.False(rig.Jobs.CompleteChunk(second.ChunkId, "w2", rows));
        Assert.Equal(2, rig.T.Scalar<int>("select count(*) from job_results"));
        Assert.Equal("succeeded", rig.Jobs.GetProgress(job.Id)!.Status);

        // results are an upsert: a later completion of a re-run chunk replaces, never duplicates
        var job2 = rig.Go(rig.Cases(Rate(0.05)));
        var c = rig.Jobs.TryClaimChunk(T0, lease, "w3")!;
        Assert.True(rig.Jobs.CompleteChunk(c.ChunkId, "w3", [new CaseResult(0, "k", null)]));
        rig.T.Exec($"update job_chunks set state = 'leased', lease_owner = 'w4' where id = {c.ChunkId}");
        Assert.True(rig.Jobs.CompleteChunk(c.ChunkId, "w4", [new CaseResult(0, "k-new", null)]));
        Assert.Equal(("k-new", 1), (rig.Jobs.GetResults(job2.Id, -1, 10)[0].RunKey, rig.T.Scalar<int>($"select count(*) from job_results where job_id = '{job2.Id}'")));
    }

    [PgFact]
    public void A_chunk_is_dead_after_5_attempts_and_FailChunk_releases_for_retry()
    {
        using var rig = new Rig();
        var job = rig.Go(rig.Cases(Rate(0.06)));
        for (var i = 1; i <= 5; i++)
        {
            var c = rig.Jobs.TryClaimChunk(T0.AddMinutes(i * 10), SweepWorker.Lease, "w1")!;
            Assert.Equal(i, c.Attempts);
            if (i % 2 == 0) rig.Jobs.FailChunk(c.ChunkId, "w1", "boom"); // released at once; odd attempts just expire
            Assert.Equal(i % 2 == 0 ? "queued" : "leased", rig.T.Scalar<string>("select state from job_chunks"));
        }
        Assert.Null(rig.Jobs.TryClaimChunk(T0.AddHours(5), SweepWorker.Lease, "w1"));
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("failed", 1), (p.Status, p.Dead));
        Assert.Null(rig.Jobs.TryClaimChunk(T0.AddHours(6), SweepWorker.Lease, "w1"));
    }

    [PgFact]
    public void Idempotency_key_is_honoured_even_when_submitted_concurrently()
    {
        using var rig = new Rig();
        var req = rig.Cases(Rate(0.06));
        var ids = new ConcurrentBag<string>();
        Parallel.For(0, 10, new ParallelOptions { MaxDegreeOfParallelism = 10 }, _ => ids.Add(rig.Submit.Submit(req, "same-key").Job.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, rig.T.Scalar<int>("select count(*) from jobs"));
        Assert.Equal(1, rig.T.Scalar<int>("select count(*) from job_chunks"));
        Assert.Equal(1, rig.T.Scalar<int>("select count(*) from ledger where action = 'sweep.submitted'"));
        Assert.Throws<IdempotencyConflictException>(() => rig.Submit.Submit(rig.Cases(Rate(0.07)), "same-key"));
        Assert.Equal(1, rig.T.Scalar<int>("select count(*) from jobs"));
    }

    private static async Task<WebApplication> Start(string cs, params string[] extra)
    {
        var app = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0", $"--Persistence:ConnectionString={cs}" }.Concat(extra).ToArray());
        await app.StartAsync();
        return app;
    }

    private static string SweepBody(string setId) =>
        $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","cases":[{"overrides":[{"path":"interestRate","value":0.06}]},{"overrides":[{"path":"interestRate","value":0.07}]}]}""";

    private static async Task<(HttpResponseMessage Res, JsonObject Json)> Send(HttpClient c, HttpMethod m, string url, string? body = null, string? key = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        if (key is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var res = await c.SendAsync(req);
        return (res, JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject());
    }

    private static async Task<string> Publish(HttpClient c, Assumptions a) =>
        (await Send(c, HttpMethod.Post, "/v1/assumption-sets", JsonSerializer.Serialize(a, ApiHost.Json))).Json["id"]!.GetValue<string>();

    private static async Task<JsonObject> WaitFor(HttpClient c, string jobId)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            var (_, j) = await Send(c, HttpMethod.Get, $"/v1/jobs/{jobId}");
            if (j["status"]!.GetValue<string>() == "succeeded") return j;
            await Task.Delay(100);
        }
        throw new TimeoutException($"job {jobId} never succeeded");
    }

    [PgFact]
    public async Task A_failing_sweep_ledger_row_leaves_no_job_and_no_chunks()
    {
        using var t = new TempSchema(migrate: false);
        await using var app = await Start(t.SchemaCs, "--Role=api");
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var setId = await Publish(c, A);
        t.Exec("alter table ledger add constraint no_sweeps check (action <> 'sweep.submitted')");
        var (res, json) = await Send(c, HttpMethod.Post, "/v1/sweeps", SweepBody(setId));
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal((0, 0, 0), (t.Scalar<int>("select count(*) from jobs"), t.Scalar<int>("select count(*) from job_chunks"), t.Scalar<int>("select count(*) from job_results")));
        t.Exec("alter table ledger drop constraint no_sweeps");
        var (ok, _) = await Send(c, HttpMethod.Post, "/v1/sweeps", SweepBody(setId));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal((1, 1), (t.Scalar<int>("select count(*) from jobs"), t.Scalar<int>("select count(*) from job_chunks")));
    }

    [PgFact]
    public async Task A_new_host_finishes_a_job_submitted_before_the_old_host_stopped()
    {
        using var t = new TempSchema(migrate: false);
        string jobId;
        await using (var api = await Start(t.SchemaCs, "--Role=api"))
        {
            using var c = new HttpClient { BaseAddress = new Uri(api.Urls.First()) };
            var setId = await Publish(c, A);
            var (res, json) = await Send(c, HttpMethod.Post, "/v1/sweeps", SweepBody(setId), "durable-1");
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            jobId = json["jobId"]!.GetValue<string>();
            await Task.Delay(1000);
            Assert.Equal("queued", (await Send(c, HttpMethod.Get, $"/v1/jobs/{jobId}")).Json["status"]!.GetValue<string>());
        }
        await using var worker = await Start(t.SchemaCs); // Role=all
        using var c2 = new HttpClient { BaseAddress = new Uri(worker.Urls.First()) };
        var job = await WaitFor(c2, jobId);
        Assert.Equal(2, job["completedCases"]!.GetValue<int>());
        var (_, results) = await Send(c2, HttpMethod.Get, $"/v1/jobs/{jobId}/results");
        Assert.Equal(2, results["items"]!.AsArray().Count);
        Assert.NotNull(results["items"]![0]!["summary"]!["leveredIrr"]);
        // the same key on the new host still finds the job
        var (again, aj) = await Send(c2, HttpMethod.Post, "/v1/sweeps", SweepBody(t.Scalar<string>("select id from assumption_sets")), "durable-1");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(jobId, aj["jobId"]!.GetValue<string>());
    }

}
