using System.Diagnostics;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;
using Xunit.Abstractions;

namespace HotelUnderwriting.Tests;

public class SweepTests(ITestOutputHelper output)
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly ScenarioInputs Base = A.Scenarios.First(s => s.Name == "Base");

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now = now;
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FlakyRunStore(IRunStore inner, Func<RunRecord, bool> fail) : IRunStore
    {
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record) => fail(record) ? throw new IOException("boom") : inner.SaveIfAbsent(record);
    }

    private sealed class Rig
    {
        public readonly InMemoryLedger Ledger = new();
        public readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        public readonly InMemoryRunStore Runs;
        public readonly IJobStore Jobs;
        public readonly SubmitSweep Submit;
        public readonly SweepWorker Worker;
        public readonly string SetId;

        public Rig(Func<IRunStore, IRunStore>? decorate = null)
        {
            var sets = new InMemoryAssumptionSetStore(Ledger);
            Runs = new InMemoryRunStore(Ledger);
            Jobs = new InMemoryJobStore(Ledger);
            SetId = new PublishAssumptionSet(sets, Clock, Ledger).Publish(A).Record.Id;
            var runStore = decorate?.Invoke(Runs) ?? Runs;
            Submit = new SubmitSweep(Jobs, Clock);
            Worker = new SweepWorker(Jobs, sets, new SubmitRun(runStore, Clock, ledger: Ledger), Clock);
        }

        public SweepRequest Cases(params SweepCase[] cases) => new(SetId, "Base", cases, null);
        public SweepRequest Grid(params GridAxis[] axes) => new(SetId, "Base", null, axes);
        public JobRecord Go(SweepRequest r, string? key = null) => Submit.Submit(r, key).Job;
        public void Drain(string worker = "w1") { while (Worker.ProcessOne(worker)) { } }
        public IReadOnlyList<CaseResult> Results(JobRecord j) => Jobs.GetResults(j.Id, -1, int.MaxValue);
        public int Count(string action) => Ledger.Entries.Count(e => e.Action == action);
    }

    private static SweepCase Rate(double v, string? name = null) => new([new ScenarioOverride("interestRate", v)], name);
    private static string KeyFor(Func<ScenarioInputs, ScenarioInputs> change) => RunKey.Compute(A, change(Base), EngineInfo.Version, WorkbookQuirks.None);

    [Fact]
    public void Cases_sweep_matches_direct_runs_run_keys_and_ledger()
    {
        var r = new Rig();
        var job = r.Go(r.Cases(Rate(0.06), Rate(0.07), Rate(0.08)));
        Assert.Equal(("queued", 1, 3), (r.Jobs.GetProgress(job.Id)!.Status, job.ChunkCount, job.CaseCount));
        r.Drain();
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 3, 0), (p.Status, p.CompletedCases, p.FailedCases));
        var results = r.Results(job);
        foreach (var (res, rate) in results.Zip(new[] { 0.06, 0.07, 0.08 }))
        {
            Assert.Equal(KeyFor(s => s with { InterestRate = rate }), res.RunKey);
            Assert.True(r.Runs.TryGet(res.RunKey!, out var run));
            Assert.Equal(Underwriter.Run(A, Base with { InterestRate = rate }).Summary.LeveredIrr, run.Result.Summary.LeveredIrr);
        }
        Assert.Equal(1, r.Count(LedgerEntry.SweepSubmitted));
        Assert.Equal(3, r.Count(LedgerEntry.RunComputed));
        var submitted = r.Ledger.Entries.Single(e => e.Action == LedgerEntry.SweepSubmitted);
        Assert.Equal((job.Id, job.RequestHash, job.CorrelationId), (submitted.SubjectId, submitted.InputHash, submitted.CorrelationId));
        Assert.All(r.Ledger.Entries.Where(e => e.Action == LedgerEntry.RunComputed), e => Assert.Equal(("worker", job.CorrelationId), (e.Actor, e.CorrelationId)));
    }

    [Fact]
    public void Grid_is_lazy_first_axis_slowest_and_matches_direct_runs()
    {
        var r = new Rig();
        var req = r.Grid(new GridAxis("interestRate", [0.06, 0.07, 0.08]), new GridAxis("exitCapRate", [0.07, 0.075, 0.08, 0.085]));
        var plan = SweepPlan.Create(req);
        Assert.Equal(12, plan.CaseCount);
        Assert.Equal(new[] { 0.06, 0.07 }, plan.CaseAt(0).Overrides.Select(o => o.Value));
        Assert.Equal(new[] { 0.07, 0.075 }, plan.CaseAt(5).Overrides.Select(o => o.Value));
        Assert.Equal(new[] { 0.08, 0.085 }, plan.CaseAt(11).Overrides.Select(o => o.Value));
        Assert.Equal(new[] { 0.06, 0.075 }, plan.CaseAt(1).Overrides.Select(o => o.Value)); // second axis varies fastest

        var job = r.Go(req);
        r.Drain();
        Assert.Equal("succeeded", r.Jobs.GetProgress(job.Id)!.Status);
        var results = r.Results(job);
        Assert.Equal(Enumerable.Range(0, 12), results.Select(x => x.CaseIndex));
        foreach (var (i, rate, cap) in new[] { (0, 0.06, 0.07), (5, 0.07, 0.075), (11, 0.08, 0.085) })
        {
            Assert.Equal(KeyFor(s => s with { InterestRate = rate, ExitCapRate = cap }), results[i].RunKey);
            Assert.True(r.Runs.TryGet(results[i].RunKey!, out var run));
            Assert.Equal(Underwriter.Run(A, Base with { InterestRate = rate, ExitCapRate = cap }).Summary.LeveredIrr, run.Result.Summary.LeveredIrr);
        }
    }

    [Fact]
    public void A_grid_at_the_case_limit_is_planned_without_expansion()
    {
        var plan = SweepPlan.Create(new SweepRequest("s", "Base", null, new[]
        {
            new GridAxis("interestRate", Enumerable.Repeat(0.07, 10).ToArray()),
            new GridAxis("exitCapRate", Enumerable.Repeat(0.075, 100).ToArray()),
            new GridAxis("promote", Enumerable.Repeat(0.2, 100).ToArray()),
        }));
        Assert.Equal((100_000, 200), (plan.CaseCount, plan.ChunkCount));
        Assert.Equal(3, plan.CaseAt(99_999).Overrides.Count);
    }

    [Fact]
    public void Large_grid_of_5000_cases_with_two_workers()
    {
        var r = new Rig();
        var job = r.Go(r.Grid(
            new GridAxis("interestRate", Enumerable.Range(0, 50).Select(i => 0.04 + i * 0.0005).ToArray()),
            new GridAxis("exitCapRate", Enumerable.Range(0, 100).Select(i => 0.06 + i * 0.0003).ToArray())));
        Assert.Equal((5000, 10), (job.CaseCount, job.ChunkCount));
        var sw = Stopwatch.StartNew();
        Parallel.For(0, 2, new ParallelOptions { MaxDegreeOfParallelism = 2 }, w => r.Drain("w" + w));
        output.WriteLine($"5000 cases, 2 workers: {sw.Elapsed.TotalSeconds:F1} s");
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 10, 5000, 0), (p.Status, p.Done, p.CompletedCases, p.FailedCases));
        Assert.Equal(5000, r.Results(job).Select(x => x.RunKey).Distinct().Count());
    }

    [Fact]
    public void Repeating_a_sweep_is_served_entirely_from_the_run_store()
    {
        var r = new Rig();
        var req = r.Cases(Rate(0.06), Rate(0.07), Rate(0.08));
        var first = r.Go(req);
        r.Drain();
        var second = r.Go(req);
        Assert.NotEqual(first.Id, second.Id);
        r.Drain();
        Assert.Equal("succeeded", r.Jobs.GetProgress(second.Id)!.Status);
        Assert.Equal(r.Results(first).Select(x => x.RunKey), r.Results(second).Select(x => x.RunKey));
        Assert.Equal((2, 3, 3), (r.Count(LedgerEntry.SweepSubmitted), r.Count(LedgerEntry.RunComputed), r.Count(LedgerEntry.RunServedFromStore)));
    }

    [Fact]
    public void Idempotency_key_returns_the_same_job_or_conflicts()
    {
        var r = new Rig();
        var req = r.Cases(Rate(0.06));
        var (a, createdA) = r.Submit.Submit(req, "key-1");
        var (b, createdB) = r.Submit.Submit(req, "key-1");
        Assert.True(createdA);
        Assert.False(createdB);
        Assert.Equal(a.Id, b.Id);
        Assert.Throws<IdempotencyConflictException>(() => r.Submit.Submit(r.Cases(Rate(0.07)), "key-1"));
        Assert.Equal(1, r.Count(LedgerEntry.SweepSubmitted));
        Assert.NotEqual(a.Id, r.Submit.Submit(req).Job.Id); // no key: always a new job
        Assert.True(SweepLimits.IsValidIdempotencyKey(new string('a', 128)));
        Assert.All(new[] { "", new string('a', 129), "has space", "slash/" }, k => Assert.False(SweepLimits.IsValidIdempotencyKey(k)));
    }

    [Fact]
    public void Invalid_requests_are_rejected_and_nothing_is_stored()
    {
        var r = new Rig();
        var ok = new[] { Rate(0.06) };
        var overrides21 = Enumerable.Range(0, 21).Select(_ => new ScenarioOverride("interestRate", 0.06)).ToArray();
        var bad = new Dictionary<string, SweepRequest>
        {
            ["unknown path"] = r.Cases(new SweepCase([new ScenarioOverride("bogus", 1)])),
            ["vector path"] = r.Cases(new SweepCase([new ScenarioOverride("occupancy", 1)])),
            ["path case"] = r.Cases(new SweepCase([new ScenarioOverride("InterestRate", 0.06)])),
            ["NaN"] = r.Cases(new SweepCase([new ScenarioOverride("interestRate", double.NaN)])),
            ["Infinity"] = r.Cases(new SweepCase([new ScenarioOverride("interestRate", double.PositiveInfinity)])),
            ["1.5 months"] = r.Cases(new SweepCase([new ScenarioOverride("holdMonths", 1.5)])),
            ["huge int"] = r.Cases(new SweepCase([new ScenarioOverride("holdMonths", 1e12)])),
            ["both"] = new SweepRequest(r.SetId, "Base", ok, [new GridAxis("interestRate", [0.06])]),
            ["neither"] = new SweepRequest(r.SetId, "Base"),
            ["empty cases"] = r.Cases(),
            ["empty grid"] = r.Grid(),
            ["empty axis"] = r.Grid(new GridAxis("interestRate", [])),
            ["grid over 100000"] = r.Grid(new GridAxis("interestRate", new double[1000]), new GridAxis("exitCapRate", new double[101])),
            ["21 axes"] = r.Grid(Enumerable.Range(0, 21).Select(_ => new GridAxis("interestRate", [0.06])).ToArray()),
            ["1001 values"] = r.Grid(new GridAxis("interestRate", new double[1001])),
            ["21 overrides"] = r.Cases(new SweepCase(overrides21)),
            ["name 101"] = r.Cases(new SweepCase([], new string('x', 101))),
            ["100001 cases"] = r.Cases(Enumerable.Range(0, 100_001).Select(_ => new SweepCase([])).ToArray()),
        };
        foreach (var (name, req) in bad) Assert.True(Throws(r, req), name);
        Assert.Equal(0, r.Count(LedgerEntry.SweepSubmitted));
        Assert.False(Throws(r, r.Cases(new SweepCase(overrides21[..20], new string('x', 100))))); // exactly at the limits is fine
        Assert.False(Throws(r, r.Grid(new GridAxis("holdMonths", [60, 61]))));
        Assert.Equal(2, r.Count(LedgerEntry.SweepSubmitted));
    }

    private static bool Throws(Rig r, SweepRequest req)
    {
        try { r.Submit.Submit(req); return false; }
        catch (InvalidAssumptionsException) { return true; }
    }

    [Fact]
    public void Overrides_apply_in_order_last_write_wins_and_leave_vectors_alone()
    {
        var s = SweepOverrides.Apply(Base, [new ScenarioOverride("interestRate", 0.01), new ScenarioOverride("interestRate", 0.09), new ScenarioOverride("holdMonths", 48)]);
        Assert.Equal((0.09, 48), (s.InterestRate, s.HoldMonths));
        Assert.Equal(Base with { InterestRate = 0.09, HoldMonths = 48 }, s);
        Assert.Same(Base.Occupancy, s.Occupancy);
    }

    [Fact]
    public void A_case_the_engine_rejects_is_recorded_and_the_job_still_succeeds()
    {
        var r = new Rig();
        var job = r.Go(r.Cases(Rate(0.06), new SweepCase([new ScenarioOverride("holdMonths", 5)], "short"), Rate(0.08)));
        r.Drain();
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 3, 1), (p.Status, p.CompletedCases, p.FailedCases));
        var res = r.Results(job);
        Assert.Null(res[1].RunKey);
        Assert.Contains("hold period 5", res[1].Error);
        Assert.DoesNotContain("Parameter", res[1].Error);
        Assert.All(new[] { res[0], res[2] }, x => { Assert.NotNull(x.RunKey); Assert.Null(x.Error); });
    }

    [Fact]
    public void Expired_lease_is_reclaimed_and_completing_twice_leaves_one_row_per_case()
    {
        var r = new Rig();
        var job = r.Go(r.Cases(Rate(0.06), Rate(0.07)));
        var lease = TimeSpan.FromSeconds(60);
        var first = r.Jobs.TryClaimChunk(r.Clock.Now, lease, "w1")!;
        Assert.Equal(1, first.Attempts);
        Assert.Null(r.Jobs.TryClaimChunk(r.Clock.Now.AddSeconds(59), lease, "w2")); // lease still live
        var second = r.Jobs.TryClaimChunk(r.Clock.Now.AddSeconds(61), lease, "w2")!;
        Assert.Equal((first.ChunkId, 2), (second.ChunkId, second.Attempts));

        var rows = new[] { new CaseResult(0, "k0", null), new CaseResult(1, "k1", null) };
        Assert.False(r.Jobs.CompleteChunk(first.ChunkId, "w1", rows)); // lost the lease
        Assert.Empty(r.Results(job));
        Assert.True(r.Jobs.CompleteChunk(second.ChunkId, "w2", rows));
        Assert.False(r.Jobs.CompleteChunk(second.ChunkId, "w2", rows)); // already done
        Assert.Equal(2, r.Results(job).Count);
        Assert.Equal("succeeded", r.Jobs.GetProgress(job.Id)!.Status);
    }

    [Fact]
    public void A_chunk_failing_twice_is_retried_and_then_succeeds()
    {
        var saves = 0;
        var r = new Rig(inner => new FlakyRunStore(inner, _ => Interlocked.Increment(ref saves) <= 2));
        var job = r.Go(r.Cases(Rate(0.06), Rate(0.07), Rate(0.08)));
        Assert.True(r.Worker.ProcessOne("w1"));
        Assert.Equal(("running", 1), (r.Jobs.GetProgress(job.Id)!.Status, r.Jobs.GetProgress(job.Id)!.Queued)); // failed once, released for retry
        r.Drain();
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 3), (p.Status, p.CompletedCases));
    }

    private sealed class BrokenStore(IRunStore inner) : IRunStore
    {
        public int Saves;
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record) => Interlocked.Increment(ref Saves) == 1 ? throw new InvalidOperationException("pool exhausted") : inner.SaveIfAbsent(record);
    }

    [Fact]
    public void A_store_InvalidOperation_retries_the_chunk_and_is_not_recorded_as_a_case_error()
    {
        var r = new Rig(inner => new BrokenStore(inner));
        var job = r.Go(r.Cases(Rate(0.06), Rate(0.07)));
        Assert.True(r.Worker.ProcessOne("w1"));
        Assert.Equal(("running", 1), (r.Jobs.GetProgress(job.Id)!.Status, r.Jobs.GetProgress(job.Id)!.Queued)); // retried, not completed with an error
        r.Drain();
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 2, 0), (p.Status, p.CompletedCases, p.FailedCases));
    }

    [Fact]
    public void A_chunk_that_always_fails_is_dead_after_5_attempts_and_other_chunks_finish()
    {
        var poison = 0.0777;
        var r = new Rig(inner => new FlakyRunStore(inner, rec => rec.Result.Inputs.InterestRate == poison));
        var cases = Enumerable.Range(0, 1000).Select(i => Rate(i == 0 ? poison : 0.06 + (i % 10) * 0.001)).ToArray();
        var job = r.Go(r.Cases(cases));
        var attempts = 0;
        while (r.Worker.ProcessOne("w1")) attempts++;
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("failed", 1, 1, 0), (p.Status, p.Dead, p.Done, p.Queued));
        Assert.Equal(6, attempts); // 5 failed attempts on the poisoned chunk + 1 good chunk
        Assert.Equal(500, p.CompletedCases);
        Assert.Null(r.Jobs.TryClaimChunk(r.Clock.Now.AddHours(1), SweepWorker.Lease, "w1")); // dead stays dead
    }

    [Fact]
    public void Four_workers_claim_each_of_20_chunks_exactly_once()
    {
        var r = new Rig();
        var job = r.Go(r.Cases(Enumerable.Range(0, 10_000).Select(_ => new SweepCase([])).ToArray()));
        Assert.Equal(20, job.ChunkCount);
        var claimed = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w =>
        {
            while (r.Jobs.TryClaimChunk(r.Clock.Now, SweepWorker.Lease, "w" + w) is { } c) claimed.Add(c.ChunkId);
        });
        Assert.Equal(20, claimed.Distinct().Count());
        Assert.Equal(20, claimed.Count);
        Assert.Equal((0, 20), (r.Jobs.GetProgress(job.Id)!.Queued, r.Jobs.GetProgress(job.Id)!.Leased));
    }

    [Fact]
    public void Four_workers_drain_a_20_chunk_job_with_one_result_row_per_case()
    {
        var r = new Rig();
        var job = r.Go(r.Grid(new GridAxis("interestRate", Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.06 : 0.07).ToArray()),
                              new GridAxis("exitCapRate", Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.075 : 0.08).ToArray())));
        var handled = 0;
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w => { while (r.Worker.ProcessOne("w" + w)) Interlocked.Increment(ref handled); });
        Assert.Equal(20, handled); // each chunk handled once: no lease expired
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 20, 10_000), (p.Status, p.Done, p.CompletedCases));
        Assert.Equal(Enumerable.Range(0, 10_000), r.Results(job).Select(x => x.CaseIndex));
        Assert.Equal(10_000, r.Count(LedgerEntry.RunComputed) + r.Count(LedgerEntry.RunServedFromStore));
        Assert.Equal(4, r.Count(LedgerEntry.RunComputed));
    }
}
