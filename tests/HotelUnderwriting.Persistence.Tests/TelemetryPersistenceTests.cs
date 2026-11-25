using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace HotelUnderwriting.Persistence.Tests;

/// <summary>Queue statistics, lease renewal, gauges and the startup probe against PostgreSQL.</summary>
public class TelemetryPersistenceTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset Now = now; public DateTimeOffset UtcNow => Now; }

    private static SweepRequest Cases(string setId, int n) =>
        new(setId, "Base", Enumerable.Range(0, n).Select(i => new SweepCase([new ScenarioOverride("interestRate", 0.05 + i * 0.00001)])).ToArray(), null);

    private static double Observe(QueueGauges g, string name)
    {
        double? v = null;
        using var l = new MeterListener();
        l.InstrumentPublished = (i, ls) => { if (ReferenceEquals(i.Meter, g.Meter) && i.Name == name) ls.EnableMeasurementEvents(i); };
        l.SetMeasurementEventCallback<double>((_, m, _, _) => v = m);
        l.Start();
        l.RecordObservableInstruments();
        return v ?? double.NaN;
    }

    [PgFact]
    public void Queue_stats_and_gauges_agree_with_the_in_memory_store()
    {
        using var t = new TempSchema();
        var clock = new FakeClock(T0);
        var jobs = new PostgresJobStore(t.SchemaCs, t.Schema);
        var setId = new PublishAssumptionSet(new PostgresAssumptionSetStore(t.SchemaCs, t.Schema), clock, new PostgresLedger(t.SchemaCs, t.Schema)).Publish(A).Record.Id;
        var submit = new SubmitSweep(jobs, clock);
        // 4 chunks created at T0; chunk 1 used every attempt and is dead, 3 wait; the clock then reads T0 + 90 s
        submit.Submit(Cases(setId, 4 * SweepLimits.ChunkSize));
        for (var i = 0; i < SweepLimits.MaxAttempts; i++) jobs.FailChunk(jobs.TryClaimChunk(clock.Now, SweepWorker.Lease, "w")!.ChunkId, "w", "x");
        var next = jobs.TryClaimChunk(clock.Now, SweepWorker.Lease, "w")!;
        jobs.FailChunk(next.ChunkId, "w", "x");
        clock.Now += TimeSpan.FromSeconds(90);
        Assert.Equal((3, 90d, 1), jobs.GetQueueStats(clock.Now));

        using var gauges = new QueueGauges(jobs, clock);
        Assert.Equal((3d, 90d, 1d), (Observe(gauges, "hue.queue.depth"), Observe(gauges, "hue.queue.oldest_age"), Observe(gauges, "hue.queue.dead")));

        // a leased chunk whose lease ran out counts as waiting, a live lease does not
        var leased = jobs.TryClaimChunk(clock.Now, SweepWorker.Lease, "w2")!;
        Assert.Equal(2, jobs.GetQueueStats(clock.Now).Queued);
        Assert.Equal(3, jobs.GetQueueStats(clock.Now + SweepWorker.Lease).Queued);
        Assert.Equal(2, leased.Attempts);
    }

    [PgFact]
    public void Renewal_extends_only_a_live_owned_lease_and_a_second_worker_cannot_claim_a_renewed_chunk()
    {
        using var t = new TempSchema();
        var clock = new FakeClock(T0);
        var jobs = new PostgresJobStore(t.SchemaCs, t.Schema);
        var setId = new PublishAssumptionSet(new PostgresAssumptionSetStore(t.SchemaCs, t.Schema), clock, new PostgresLedger(t.SchemaCs, t.Schema)).Publish(A).Record.Id;
        new SubmitSweep(jobs, clock).Submit(Cases(setId, 3));
        var c = jobs.TryClaimChunk(T0, SweepWorker.Lease, "a")!;
        Assert.False(jobs.RenewLease(c.ChunkId, "b", T0, SweepWorker.Lease));                      // not the owner
        Assert.True(jobs.RenewLease(c.ChunkId, "a", T0.AddSeconds(50), SweepWorker.Lease));        // live: now T+110
        Assert.Null(jobs.TryClaimChunk(T0.AddSeconds(70), SweepWorker.Lease, "b"));                // inside the renewed lease
        Assert.False(jobs.RenewLease(c.ChunkId, "a", T0.AddSeconds(111), SweepWorker.Lease));      // expired: not live any more
        Assert.NotNull(jobs.TryClaimChunk(T0.AddSeconds(111), SweepWorker.Lease, "b"));
        Assert.False(jobs.RenewLease(c.ChunkId, "a", T0.AddSeconds(112), SweepWorker.Lease));      // reclaimed by b: lost
        jobs.ReleaseChunk(c.ChunkId, "a");                                                          // not the owner: ignored
        Assert.Equal(1, t.Scalar<int>("select count(*) from job_chunks where state = 'leased' and attempts = 2"));
        jobs.ReleaseChunk(c.ChunkId, "b");                                                          // released: the attempt is given back
        Assert.Equal(1, t.Scalar<int>("select count(*) from job_chunks where state = 'queued' and attempts = 1"));
        Assert.False(jobs.CompleteChunk(c.ChunkId, "a", [new CaseResult(0, "k", null)]));
        Assert.Equal(0, t.Scalar<int>("select count(*) from job_results"));
    }

    [PgFact]
    public void A_worker_renews_during_a_long_chunk_and_completes_it_once_on_postgres()
    {
        using var t = new TempSchema();
        var clock = new FakeClock(T0);
        var ledger = new PostgresLedger(t.SchemaCs, t.Schema);
        var sets = new PostgresAssumptionSetStore(t.SchemaCs, t.Schema);
        var jobs = new PostgresJobStore(t.SchemaCs, t.Schema);
        var setId = new PublishAssumptionSet(sets, clock, ledger).Publish(A).Record.Id;
        var polls = new List<bool>();
        SweepWorker? worker = null;
        var saves = 0;
        var runs = new HookStore(new PostgresRunStore(t.SchemaCs, t.Schema), () =>
        {
            saves++;
            if (saves == 50) clock.Now += TimeSpan.FromSeconds(50);
            if (saves == 70) { clock.Now += TimeSpan.FromSeconds(20); polls.Add(worker!.ProcessOne("w2")); }
        });
        worker = new SweepWorker(jobs, sets, new SubmitRun(runs, clock, ledger: ledger), clock);
        var job = new SubmitSweep(jobs, clock).Submit(Cases(setId, 120)).Job;
        Assert.True(worker.ProcessOne("w1"));
        Assert.Equal(new[] { false }, polls);
        var p = jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 120), (p.Status, p.CompletedCases));
        Assert.Equal(120, t.Scalar<int>("select count(*) from job_results"));
        Assert.Equal(120, t.Scalar<int>("select count(*) from ledger where action = 'run.computed'"));
    }

    private sealed class HookStore(IRunStore inner, Action afterSave) : IRunStore
    {
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record) { var r = inner.SaveIfAbsent(record); afterSave(); return r; }
    }

    [PgFact]
    public async Task Startup_is_503_while_the_migration_is_blocked_then_200_after_ready_migrates()
    {
        using var t = new TempSchema(migrate: false);
        var cs = new NpgsqlConnectionStringBuilder(t.SchemaCs) { CommandTimeout = 1 }.ConnectionString;
        WebApplication app;
        using (var blocker = new NpgsqlConnection(t.Cs))
        {
            blocker.Open();
            using var tx = blocker.BeginTransaction();
            using (var lockCmd = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext('hue_migrate:' || @s))", blocker, tx)) { lockCmd.Parameters.AddWithValue("s", t.Schema); lockCmd.ExecuteNonQuery(); }
            app = ApiHost.Build(["--urls", "http://127.0.0.1:0", $"--Persistence:ConnectionString={cs}"]);   // its migration waits on our lock, times out and is logged
            await app.StartAsync();
            using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var blocked = await c.GetAsync("/health/startup");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.Equal("application/problem+json", blocked.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/health/startup")).StatusCode);   // the probe retries the migration, which is still blocked
            Assert.Equal(0, t.Scalar<int>("select count(*) from information_schema.tables where table_schema = '" + t.Schema + "'"));
        } // lock released (rolled back)
        var (hostCs, hostSchema) = PostgresConnection.Parse(cs);
        await using (app)
        {
            using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            // ready migrates; under load a migration command can exceed this test's 1 s timeout, so poll
            var ready = await c.GetAsync("/health/ready");
            for (var i = 1; i < 20 && ready.StatusCode != HttpStatusCode.OK; i++)
            {
                await Task.Delay(500);
                ready = await c.GetAsync("/health/ready");
            }
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            var started = await c.GetAsync("/health/startup");
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            Assert.Equal("started", JsonNode.Parse(await started.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        }
        // the host's own pools (migrator and stores) are not the test schema's: release them so parallel tests stay under the server's 100 connections
        foreach (var pooled in new[] { hostCs, new NpgsqlConnectionStringBuilder(hostCs) { SearchPath = hostSchema }.ConnectionString })
        {
            using var pool = new NpgsqlConnection(pooled);
            NpgsqlConnection.ClearPool(pool);
        }
    }
}
