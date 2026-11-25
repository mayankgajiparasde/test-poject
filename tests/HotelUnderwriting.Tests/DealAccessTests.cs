using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

/// <summary>The in-memory access store and the worker's grants (story 10a).</summary>
public class DealAccessTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FailingRunGrant(IDealAccess inner) : IDealAccess
    {
        public int Failures;
        public void Grant(DealResource kind, string resourceId, IReadOnlyCollection<string> dealIds)
        {
            if (kind == DealResource.Run && Failures-- > 0) throw new IOException("access store unavailable");
            inner.Grant(kind, resourceId, dealIds);
        }
        public IReadOnlyCollection<string> DealsOf(DealResource kind, string resourceId) => inner.DealsOf(kind, resourceId);
    }

    private sealed class Log : IWorkLog
    {
        public readonly List<(string Level, string Template, object?[] Args)> Lines = new();
        public void Info(string template, params object?[] args) => Lines.Add(("Info", template, args));
        public void Warn(string template, params object?[] args) => Lines.Add(("Warn", template, args));
        public IDisposable? Scope(string? correlationId, string? jobId, string? runKey) => null;
    }

    private sealed class Rig
    {
        public readonly Log Log = new();
        public readonly FakeClock Clock = new();
        public readonly InMemoryJobStore Jobs = new();
        public readonly InMemoryRunStore Runs = new();
        public readonly IDealAccess Access;
        public readonly SweepWorker Worker;
        private readonly string _setId;

        public Rig(IDealAccess? access = null)
        {
            Access = access ?? new InMemoryDealAccess();
            var sets = new InMemoryAssumptionSetStore();
            _setId = new PublishAssumptionSet(sets, Clock).Publish(A).Record.Id;
            Worker = new SweepWorker(Jobs, sets, new SubmitRun(Runs, Clock), Clock, log: Log, access: Access);
        }

        /// <summary>One good case and one the engine rejects.</summary>
        public JobRecord Submit(bool dealScoped) => new SubmitSweep(Jobs, Clock).Submit(
            new SweepRequest(_setId, "Base", [new SweepCase([new ScenarioOverride("interestRate", 0.061)]), new SweepCase([new ScenarioOverride("holdMonths", 5)])]), dealScoped: dealScoped).Job;
    }

    [Fact]
    public void Grants_are_idempotent_additive_and_separate_per_kind()
    {
        var access = new InMemoryDealAccess();
        access.Grant(DealResource.Set, "x", ["d1"]);
        access.Grant(DealResource.Set, "x", ["d1", "d2"]);
        Assert.Equal(["d1", "d2"], access.DealsOf(DealResource.Set, "x").Order());
        Assert.Empty(access.DealsOf(DealResource.Run, "x"));
        Assert.Empty(access.DealsOf(DealResource.Job, "x"));
    }

    [Fact]
    public void A_deal_scoped_job_without_a_grant_is_not_computed_and_is_retried_later()
    {
        var rig = new Rig();
        var job = rig.Submit(dealScoped: true);
        Assert.True(rig.Worker.ProcessOne("w"));
        Assert.Equal(0, rig.Jobs.GetProgress(job.Id)!.CompletedCases);
        Assert.False(rig.Worker.ProcessOne("w"));   // held back: attempts are not used up at once
        rig.Access.Grant(DealResource.Job, job.Id, ["d1", "d2"]);
        rig.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(rig.Worker.ProcessOne("w"));
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 2, 1), (p.Status, p.CompletedCases, p.FailedCases));
        Assert.Equal(["d1", "d2"], rig.Access.DealsOf(DealResource.Run, rig.Jobs.GetResults(job.Id, -1, 10)[0].RunKey!).Order());
    }

    [Theory]
    [InlineData(0, 1, "Info")]
    [InlineData(40, 20, "Info")]
    [InlineData(59.9, 29.95, "Info")]
    [InlineData(60, 30, "Warn")]
    [InlineData(600, 60, "Warn")]
    public void The_deferral_grows_with_the_job_age_between_1_and_60_seconds(double age, double delay, string level)
    {
        var rig = new Rig();
        rig.Submit(dealScoped: true);
        rig.Clock.Now += TimeSpan.FromSeconds(age);
        var claimed = rig.Clock.Now;
        Assert.True(rig.Worker.ProcessOne("w"));
        Assert.Equal(level, Assert.Single(rig.Log.Lines, l => l.Template.Contains("no deal grant")).Level);
        Assert.Null(rig.Jobs.TryClaimChunk(claimed + TimeSpan.FromSeconds(delay - 0.01), SweepWorker.Lease, "other"));
        Assert.Equal(1, rig.Jobs.TryClaimChunk(claimed + TimeSpan.FromSeconds(delay), SweepWorker.Lease, "other")!.Attempts); // given back
    }

    [Fact]
    public void An_ungranted_job_never_dies_and_completes_once_granted()
    {
        var rig = new Rig();
        var job = rig.Submit(dealScoped: true);
        for (var i = 0; i < 3 * SweepLimits.MaxAttempts; i++)
        {
            Assert.True(rig.Worker.ProcessOne("w"));
            rig.Clock.Now += SweepWorker.Lease;
        }
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal((0, 1, 0, 0), (p.Dead, p.Leased, p.Done, p.CompletedCases));
        Assert.False(rig.Runs.TryGet(RunKey.Compute(A, A.Scenarios.First(s => s.Name == "Base") with { InterestRate = 0.061 }, EngineInfo.Version, WorkbookQuirks.None), out _));
        rig.Access.Grant(DealResource.Job, job.Id, ["d1"]);
        Assert.True(rig.Worker.ProcessOne("w"));
        Assert.Equal("succeeded", rig.Jobs.GetProgress(job.Id)!.Status);
    }

    [Fact]
    public void Only_the_lease_owner_can_defer_a_chunk()
    {
        var rig = new Rig();
        rig.Submit(dealScoped: true);
        var now = rig.Clock.Now;
        var chunk = rig.Jobs.TryClaimChunk(now, SweepWorker.Lease, "owner")!;
        rig.Jobs.DeferChunk(chunk.ChunkId, "stranger", now);
        Assert.Null(rig.Jobs.TryClaimChunk(now + TimeSpan.FromSeconds(1), SweepWorker.Lease, "other"));
        rig.Jobs.DeferChunk(chunk.ChunkId, "owner", now + TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Jobs.TryClaimChunk(now + TimeSpan.FromSeconds(1), SweepWorker.Lease, "other")!.Attempts);
    }

    [Fact]
    public void A_failed_run_grant_fails_the_chunk_and_the_retry_grants_it()
    {
        var access = new FailingRunGrant(new InMemoryDealAccess()) { Failures = 1 };
        var rig = new Rig(access);
        var job = rig.Submit(dealScoped: true);
        access.Grant(DealResource.Job, job.Id, ["d1"]);
        Assert.True(rig.Worker.ProcessOne("w"));
        var p = rig.Jobs.GetProgress(job.Id)!;
        Assert.Equal((0, 0, 1), (p.Done, p.CompletedCases, p.Queued));   // never complete with an ungranted run
        Assert.True(rig.Worker.ProcessOne("w"));
        Assert.Equal("succeeded", rig.Jobs.GetProgress(job.Id)!.Status);
        Assert.Equal(["d1"], access.DealsOf(DealResource.Run, rig.Jobs.GetResults(job.Id, -1, 10)[0].RunKey!));
    }

    [Fact]
    public void A_job_without_deals_runs_as_before_and_grants_nothing()
    {
        var rig = new Rig();
        var job = rig.Submit(dealScoped: false);
        Assert.True(rig.Worker.ProcessOne("w"));
        Assert.Equal("succeeded", rig.Jobs.GetProgress(job.Id)!.Status);
        Assert.Empty(rig.Access.DealsOf(DealResource.Run, rig.Jobs.GetResults(job.Id, -1, 10)[0].RunKey!));
    }
}
