using System.Net;
using System.Text.Json;
using HotelUnderwriting.Api;
using HotelUnderwriting.Api.Tests;
using HotelUnderwriting.Application;
using HotelUnderwriting.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static HotelUnderwriting.Api.Tests.AuthRig;

namespace HotelUnderwriting.Persistence.Tests;

/// <summary>Per-deal access on PostgreSQL: the same flows as in memory (story 10a).</summary>
public class DealAccessPersistenceTests
{
    [PgFact]
    public void Grant_is_idempotent_and_the_migration_may_run_twice()
    {
        using var t = new TempSchema();
        PostgresMigrator.Apply(t.Cs, t.Schema);
        var access = new PostgresDealAccess(t.Cs, t.Schema);
        access.Grant(DealResource.Set, "x", ["d1"]);
        access.Grant(DealResource.Set, "x", ["d1", "d2", "d2"]);
        access.Grant(DealResource.Set, "x", []);
        Assert.Equal(["d1", "d2"], access.DealsOf(DealResource.Set, "x").Order());
        Assert.Empty(access.DealsOf(DealResource.Run, "x"));
        Assert.Equal(2, t.Scalar<int>("select count(*) from deal_resources where kind = 'set'"));
        // the script itself is also safe to run again
        using var script = new StreamReader(typeof(PostgresMigrator).Assembly.GetManifestResourceStream("Migrations/003_deal_access.sql")!);
        t.Exec(script.ReadToEnd());
        Assert.Equal(2, t.Scalar<int>("select count(*) from deal_resources"));
    }

    [PgFact]
    public async Task Isolation_grants_and_ledger_actors_hold_on_postgres()
    {
        using var t = new TempSchema();
        await using var app = await Start(null, $"--Persistence:ConnectionString={t.SchemaCs}");
        Assert.IsType<PostgresDealAccess>(app.Services.GetRequiredService<IDealAccess>());
        using var c = Client(app);
        await Isolation(c);
        await SameContentTwoDeals(c);
        await CachedRun(c);
        await SweepGrants(c);
        await IdempotencyScope(c);
        // that flow leaves jobs queued or running; wait so they do not write ledger rows into the window below
        var until = DateTime.UtcNow.AddSeconds(60);
        while (t.Scalar<int>("select count(*) from job_chunks where state <> 'done'") > 0 && DateTime.UtcNow < until) await Task.Delay(100);

        var before = t.Scalar<long>("select max(id) from ledger");
        var set = await LedgerActivity(c);
        int Rows(string where) => t.Scalar<int>($"select count(*) from ledger where id > {before} and {where}");
        Assert.Equal(1, Rows($"action = 'assumption_set.published' and actor = 'alice' and subject_id = '{set}'"));
        Assert.Equal(1, Rows("action = 'sweep.submitted' and actor = 'alice'"));
        Assert.Equal(1, Rows("action like 'run.%' and actor = 'alice'"));
        Assert.Equal(2, Rows("action like 'run.%' and actor = 'worker'"));
    }

    [PgFact]
    public async Task A_failed_grant_is_repaired_by_a_repeat_on_postgres()
    {
        using var t = new TempSchema();
        var access = new AuthRig.FlakyAccess(new PostgresDealAccess(t.Cs, t.Schema));
        await using var app = await Start(s => s.AddSingleton<IDealAccess>(access), $"--Persistence:ConnectionString={t.SchemaCs}");
        using var c = Client(app);
        await GrantFailure(app, c, access);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private static (PostgresJobStore Jobs, SweepWorker Worker, JobRecord Job) ScopedJob(TempSchema t, IClock clock, IDealAccess access)
    {
        var ledger = new PostgresLedger(t.Cs, t.Schema);
        var sets = new PostgresAssumptionSetStore(t.Cs, t.Schema);
        var jobs = new PostgresJobStore(t.Cs, t.Schema);
        var set = new PublishAssumptionSet(sets, clock, ledger).Publish(A).Record.Id;
        var worker = new SweepWorker(jobs, sets, new SubmitRun(new PostgresRunStore(t.Cs, t.Schema), clock, ledger: ledger), clock, access: access);
        var job = new SubmitSweep(jobs, clock).Submit(new SweepRequest(set, "Base", [new SweepCase([new ScenarioOverride("interestRate", 0.061)])]), dealScoped: true).Job;
        return (jobs, worker, job);
    }

    [PgFact]
    public void An_ungranted_job_keeps_its_attempts_on_postgres_and_completes_once_granted()
    {
        using var t = new TempSchema();
        var clock = new FakeClock();
        var access = new PostgresDealAccess(t.Cs, t.Schema);
        var (jobs, worker, job) = ScopedJob(t, clock, access);
        for (var i = 0; i < 3 * SweepLimits.MaxAttempts; i++)
        {
            Assert.True(worker.ProcessOne("w"));
            Assert.False(worker.ProcessOne("w")); // held until the deferral ends
            Assert.Equal(0, t.Scalar<int>("select attempts from job_chunks"));
            clock.Now += SweepWorker.Lease;
        }
        Assert.Equal(0, t.Scalar<int>("select count(*) from runs"));
        var chunk = jobs.TryClaimChunk(clock.Now, SweepWorker.Lease, "owner")!;
        jobs.DeferChunk(chunk.ChunkId, "stranger", clock.Now);
        Assert.Null(jobs.TryClaimChunk(clock.Now, SweepWorker.Lease, "other"));
        jobs.DeferChunk(chunk.ChunkId, "owner", clock.Now);
        access.Grant(DealResource.Job, job.Id, ["d1"]);
        Assert.True(worker.ProcessOne("w"));
        Assert.Equal("succeeded", jobs.GetProgress(job.Id)!.Status);
        Assert.Equal(1, t.Scalar<int>("select count(*) from deal_resources where kind = 'run' and deal_id = 'd1'"));
    }

    [PgFact]
    public void A_failed_run_grant_is_retried_and_granted_on_postgres()
    {
        using var t = new TempSchema();
        var clock = new FakeClock();
        var access = new AuthRig.FlakyAccess(new PostgresDealAccess(t.Cs, t.Schema));
        var (jobs, worker, job) = ScopedJob(t, clock, access);
        access.Grant(DealResource.Job, job.Id, ["d1"]);
        access.FailOnce = DealResource.Run;
        Assert.True(worker.ProcessOne("w"));
        var p = jobs.GetProgress(job.Id)!;
        Assert.Equal((0, 0, 1), (p.Done, p.CompletedCases, p.Queued));
        Assert.Equal(0, t.Scalar<int>("select count(*) from deal_resources where kind = 'run'"));
        Assert.True(worker.ProcessOne("w"));
        Assert.Equal("succeeded", jobs.GetProgress(job.Id)!.Status);
        Assert.Equal(["d1"], access.DealsOf(DealResource.Run, jobs.GetResults(job.Id, -1, 10)[0].RunKey!));
    }

    [PgFact]
    public async Task Data_made_in_mode_none_is_visible_to_admin_only_in_jwt_mode()
    {
        using var t = new TempSchema();
        string set, run, job;
        await using (var open = ApiHost.Build(["--urls", "http://127.0.0.1:0", $"--Persistence:ConnectionString={t.SchemaCs}"]))
        {
            await open.StartAsync();
            using var c = Client(open);
            set = (await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, JsonSerializer.Serialize(Tweaked(301), ApiHost.Json))).Str("id");
            run = (await Send(c, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{set}}","scenario":"Base"}""")).Str("runKey");
            job = (await Send(c, HttpMethod.Post, "/v1/sweeps", null, SweepBody(set))).Str("jobId");
            await WaitFor(c, null, job);
        }
        Assert.Equal(0, t.Scalar<int>("select count(*) from deal_resources"));
        await using var app = await Start(null, $"--Persistence:ConnectionString={t.SchemaCs}");
        using var jwt = Client(app);
        await OnlyAdminSees(jwt, set, run, job);
    }

    [PgFact]
    public async Task Readiness_covers_the_deal_table()
    {
        using var t = new TempSchema();
        await using var app = await Start(null, $"--Persistence:ConnectionString={t.SchemaCs}");
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/health/ready", null)).Status);
        t.Exec("drop table deal_resources");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(c, "/health/ready", null)).Status);
    }
}
