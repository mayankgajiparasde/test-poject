using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

/// <summary>
/// Telemetry, queue gauges and worker hardening on the in-memory stores. Metric and span capture is filtered to the test's own thread
/// (the worker runs synchronously in it), so tests running in parallel do not see each other's measurements.
/// </summary>
public class TelemetryTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly ScenarioInputs Base = A.Scenarios.First(s => s.Name == "Base");

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now = now;
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>Runs a callback after each saved run, so a test can move the clock or cancel while a chunk is in flight.</summary>
    private sealed class HookRunStore(IRunStore inner, Action<int> afterSave) : IRunStore
    {
        private int _saves;
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record)
        {
            var r = inner.SaveIfAbsent(record);
            afterSave(++_saves);
            return r;
        }
    }

    private sealed class CapturingLog : IWorkLog
    {
        public readonly List<string> Lines = new();
        public readonly List<string> ScopeValues = new();
        private static readonly Regex Placeholder = new(@"\{[A-Za-z]+\}");
        public void Info(string template, params object?[] args) => Add(template, args);
        public void Warn(string template, params object?[] args) => Add(template, args);
        private void Add(string template, object?[] args) { var i = 0; lock (Lines) Lines.Add(Placeholder.Replace(template, _ => Convert.ToString(args[i++], CultureInfo.InvariantCulture) ?? "")); }
        public IDisposable? Scope(string? correlationId, string? jobId, string? runKey) { lock (ScopeValues) ScopeValues.AddRange(new[] { correlationId, jobId, runKey }.OfType<string>()); return null; }
    }

    /// <summary>Listens to every HotelUnderwriting instrument and span, on the creating thread only.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly MeterListener _meters = new();
        private readonly ActivityListener _spans;
        public readonly List<(string Name, double Value, Dictionary<string, string> Tags)> Measurements = new();
        public readonly List<Activity> Spans = new();

        public Capture()
        {
            _meters.InstrumentPublished = (i, l) => { if (i.Meter.Name == Telemetry.Name) l.EnableMeasurementEvents(i); };
            _meters.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _meters.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _meters.Start();
            _spans = new ActivityListener
            {
                ShouldListenTo = s => s.Name == Telemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { if (Environment.CurrentManagedThreadId == _thread) lock (Spans) Spans.Add(a); },
            };
            ActivitySource.AddActivityListener(_spans);
        }

        private void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (Environment.CurrentManagedThreadId != _thread) return;
            var d = tags.ToArray().ToDictionary(t => t.Key, t => Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? "");
            lock (Measurements) Measurements.Add((i.Name, v, d));
        }

        public double Sum(string name, string? outcome = null) => Measurements.Where(m => m.Name == name && (outcome is null || m.Tags.GetValueOrDefault("outcome") == outcome)).Sum(m => m.Value);
        public int Count(string name, string? outcome = null) => Measurements.Count(m => m.Name == name && (outcome is null || m.Tags.GetValueOrDefault("outcome") == outcome));

        public void Dispose() { _meters.Dispose(); _spans.Dispose(); }
    }

    private sealed class Rig
    {
        public readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        public readonly InMemoryLedger Ledger = new();
        public readonly InMemoryRunStore Runs;
        public readonly InMemoryJobStore Jobs;
        public readonly SubmitSweep Submit;
        public readonly SweepWorker Worker;
        public readonly SubmitRun SubmitRun;
        public readonly string SetId;

        public Rig(Func<IRunStore, IRunStore>? decorate = null, IWorkLog? log = null, SweepPlanCache? plans = null)
        {
            var sets = new InMemoryAssumptionSetStore(Ledger);
            Runs = new InMemoryRunStore(Ledger);
            Jobs = new InMemoryJobStore(Ledger);
            SetId = new PublishAssumptionSet(sets, Clock, Ledger).Publish(A).Record.Id;
            var store = decorate?.Invoke(Runs) ?? Runs;
            Submit = new SubmitSweep(Jobs, Clock);
            SubmitRun = new SubmitRun(store, Clock, ledger: Ledger);
            Worker = new SweepWorker(Jobs, sets, SubmitRun, Clock, plans, log);
        }

        public JobRecord Go(params SweepCase[] cases) => Submit.Submit(new SweepRequest(SetId, "Base", cases, null)).Job;
        public void Drain(string worker = "w1") { while (Worker.ProcessOne(worker)) { } }
    }

    private static SweepCase Rate(double v) => new([new ScenarioOverride("interestRate", v)]);
    private static SweepCase[] Rates(int n, double from = 0.05) => Enumerable.Range(0, n).Select(i => Rate(from + i * 0.00001)).ToArray();

    [Fact]
    public void Metrics_are_emitted_for_runs_and_a_sweep_with_one_bad_case()
    {
        using var cap = new Capture();
        var r = new Rig();
        Assert.False(r.SubmitRun.Submit(A, Base).WasCached);
        Assert.True(r.SubmitRun.Submit(A, Base).WasCached);
        Assert.Equal((1, 1, 0), (cap.Count("hue.runs", "computed"), cap.Count("hue.runs", "cached"), cap.Count("hue.runs", "rejected")));
        Assert.Equal(2, cap.Count("hue.run.duration"));
        Assert.All(cap.Measurements.Where(m => m.Name == "hue.run.duration"), m => Assert.True(m.Value >= 0));

        Assert.ThrowsAny<ArgumentException>(() => r.SubmitRun.Submit(A, Base with { HoldMonths = 5 }));
        Assert.Equal(1, cap.Count("hue.runs", "rejected"));

        var job = r.Go(Rate(0.06), Rate(0.07), new SweepCase([new ScenarioOverride("holdMonths", 5)]));
        r.Drain();
        Assert.Equal((2, 1), ((int)cap.Sum("hue.sweep.cases", "ok"), (int)cap.Sum("hue.sweep.cases", "error")));
        Assert.Equal(1, cap.Sum("hue.sweep.chunks", "done"));
        Assert.Equal(0, cap.Sum("hue.sweep.chunks", "failed") + cap.Sum("hue.sweep.chunks", "dead") + cap.Sum("hue.sweep.chunks", "lease_lost"));
        Assert.Equal("succeeded", r.Jobs.GetProgress(job.Id)!.Status);
    }

    [Fact]
    public void No_deal_figure_reaches_a_metric_tag_a_span_tag_or_a_log_line()
    {
        using var cap = new Capture();
        var log = new CapturingLog();
        var r = new Rig(log: log);
        var job = r.Go(Rate(0.06), Rate(0.07), new SweepCase([new ScenarioOverride("holdMonths", 5)]));
        r.Drain();
        r.SubmitRun.Submit(A, Base);

        var texts = cap.Measurements.SelectMany(m => m.Tags.Values)
            .Concat(cap.Spans.SelectMany(s => s.TagObjects.Select(t => Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? "")))
            .Concat(log.Lines).Concat(log.ScopeValues).ToList();
        Assert.True(texts.Count > 8 && log.Lines.Count >= 2); // not vacuous
        // ids and run keys are hex or GUID text that may contain digit runs by chance: strip exactly those, nothing else
        var ids = cap.Spans.SelectMany(s => s.TagObjects).Where(t => t.Key is "run_key" or "job_id" or "correlation_id")
            .Select(t => Convert.ToString(t.Value, CultureInfo.InvariantCulture)!).Append(job.Id).Append(job.CorrelationId).Distinct().ToList();
        var scrubbed = texts.Select(t => ids.Aggregate(t, (s, id) => s.Replace(id, ""))).ToList();
        var figures = new[]
        {
            Base.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture),
            Base.PurchasePrice.ToString("N0", CultureInfo.InvariantCulture),
            Underwriter.Run(A, Base).Summary.LeveredIrr!.Value.ToString("R", CultureInfo.InvariantCulture)[..6],
            "18.39",
        };
        foreach (var t in scrubbed) foreach (var f in figures) Assert.DoesNotContain(f, t);
        Assert.Contains(log.Lines, l => l.Contains(job.Id) && l.Contains("claimed"));
    }

    [Fact]
    public void Spans_carry_only_the_allowed_tags()
    {
        using var cap = new Capture();
        var r = new Rig();
        var job = r.Go(Rate(0.06));
        r.Drain();
        var submit = cap.Spans.Single(s => s.OperationName == "run.submit");
        var chunk = cap.Spans.Single(s => s.OperationName == "sweep.chunk");
        Assert.Equal(new[] { "correlation_id", "engine_version", "outcome", "run_key" }, submit.TagObjects.Select(t => t.Key).Order());
        Assert.Equal(new[] { "chunk_id", "correlation_id", "engine_version", "job_id", "outcome" }, chunk.TagObjects.Select(t => t.Key).Order());
        Assert.Equal(("computed", EngineInfo.Version, job.CorrelationId), (submit.GetTagItem("outcome"), submit.GetTagItem("engine_version"), submit.GetTagItem("correlation_id")));
        Assert.Equal(("done", job.Id), (chunk.GetTagItem("outcome"), chunk.GetTagItem("job_id")));
        Assert.Equal(chunk.Id, submit.ParentId); // the run is a child of its chunk
        Assert.Equal(EngineInfo.Version, Telemetry.Source.Version);
        Assert.Equal(EngineInfo.Version, Telemetry.Meter.Version);
    }

    /// <summary>The shared queue scenario: one job of 4 chunks created at T0; chunk 1 used every attempt and is dead, the other 3 wait. The clock is then T0 + 90 s.</summary>
    public static (int Queued, double Age, int Dead) ObserveQueueScenario(IJobStore jobs, Func<JobRecord> createJob, Action<TimeSpan> advance, Func<DateTimeOffset> now)
    {
        createJob();
        for (var i = 0; i < SweepLimits.MaxAttempts; i++) jobs.FailChunk(jobs.TryClaimChunk(now(), SweepWorker.Lease, "w")!.ChunkId, "w", "x");
        var next = jobs.TryClaimChunk(now(), SweepWorker.Lease, "w")!; // chunk 1 is marked dead on the way, chunk 2 is leased
        jobs.FailChunk(next.ChunkId, "w", "x");
        advance(TimeSpan.FromSeconds(90));
        return jobs.GetQueueStats(now());
    }

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

    [Fact]
    public void Queue_gauges_report_depth_oldest_age_and_dead_and_swallow_a_failing_read()
    {
        var r = new Rig();
        var stats = ObserveQueueScenario(r.Jobs, () => r.Go(Rates(4 * SweepLimits.ChunkSize)), d => r.Clock.Now += d, () => r.Clock.Now);
        Assert.Equal((3, 90d, 1), stats);

        using var gauges = new QueueGauges(r.Jobs, r.Clock);
        Assert.Equal((3d, 90d, 1d), (Observe(gauges, "hue.queue.depth"), Observe(gauges, "hue.queue.oldest_age"), Observe(gauges, "hue.queue.dead")));

        var log = new CapturingLog();
        using var broken = new QueueGauges(new ThrowingStats(), r.Clock, log);
        Assert.True(double.IsNaN(Observe(broken, "hue.queue.depth"))); // nothing reported, nothing thrown
        Assert.True(double.IsNaN(Observe(broken, "hue.queue.dead")));
        Assert.Single(log.Lines); // logged once
    }

    private sealed class ThrowingStats : IJobStore
    {
        public (int Queued, double OldestQueuedAgeSeconds, int Dead) GetQueueStats(DateTimeOffset now) => throw new IOException("db down");
        public (JobRecord Job, bool Created) Create(JobRecord job, string? idempotencyKey, LedgerEntry ledgerEntry) => throw new NotSupportedException();
        public JobRecord? TryGetJob(string jobId) => throw new NotSupportedException();
        public JobProgress? GetProgress(string jobId) => throw new NotSupportedException();
        public JobChunk? TryClaimChunk(DateTimeOffset now, TimeSpan lease, string workerId) => throw new NotSupportedException();
        public bool CompleteChunk(long chunkId, string workerId, IReadOnlyList<CaseResult> results) => throw new NotSupportedException();
        public void FailChunk(long chunkId, string workerId, string error) => throw new NotSupportedException();
        public void ReleaseChunk(long chunkId, string workerId) => throw new NotSupportedException();
        public void DeferChunk(long chunkId, string workerId, DateTimeOffset until) => throw new NotSupportedException();
        public bool RenewLease(long chunkId, string workerId, DateTimeOffset now, TimeSpan lease) => throw new NotSupportedException();
        public IReadOnlyList<CaseResult> GetResults(string jobId, int afterCaseIndex, int limit) => throw new NotSupportedException();
    }

    [Fact]
    public void Lease_renewal_keeps_a_long_chunk_from_being_claimed_and_it_completes_once()
    {
        Rig? r = null;
        var polls = new List<bool>();
        r = new Rig(inner => new HookRunStore(inner, n =>
        {
            if (n == 50) r!.Clock.Now += TimeSpan.FromSeconds(50);   // renewal is due next: the lease (60 s) is still live and is extended to T+110
            if (n == 70) { r!.Clock.Now += TimeSpan.FromSeconds(20); polls.Add(r.Worker.ProcessOne("w2")); } // T+70: past the original lease, inside the renewed one
        }));
        var job = r.Go(Rates(120));
        Assert.True(r.Worker.ProcessOne("w1"));
        Assert.Equal(new[] { false }, polls);                 // the second worker found nothing to claim
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal(("succeeded", 120), (p.Status, p.CompletedCases));
        Assert.Equal(120, r.Jobs.GetResults(job.Id, -1, int.MaxValue).Count);
        Assert.Equal(120, r.Ledger.Entries.Count(e => e.Action == LedgerEntry.RunComputed));   // nothing computed twice
        Assert.False(r.Worker.ProcessOne("w1"));
    }

    [Fact]
    public void Renewal_only_extends_a_live_lease_the_caller_still_owns()
    {
        var r = new Rig();
        r.Go(Rate(0.06));
        var c = r.Jobs.TryClaimChunk(r.Clock.Now, SweepWorker.Lease, "a")!;
        Assert.False(r.Jobs.RenewLease(c.ChunkId, "b", r.Clock.Now, SweepWorker.Lease));                       // not the owner
        Assert.True(r.Jobs.RenewLease(c.ChunkId, "a", r.Clock.Now + TimeSpan.FromSeconds(50), SweepWorker.Lease));
        Assert.Null(r.Jobs.TryClaimChunk(r.Clock.Now + TimeSpan.FromSeconds(100), SweepWorker.Lease, "b"));    // renewed to T+110
        Assert.False(r.Jobs.RenewLease(c.ChunkId, "a", r.Clock.Now + TimeSpan.FromSeconds(111), SweepWorker.Lease)); // expired
        Assert.NotNull(r.Jobs.TryClaimChunk(r.Clock.Now + TimeSpan.FromSeconds(111), SweepWorker.Lease, "b"));
        Assert.False(r.Jobs.RenewLease(c.ChunkId, "a", r.Clock.Now + TimeSpan.FromSeconds(112), SweepWorker.Lease)); // reclaimed by b
    }

    [Fact]
    public void A_worker_that_lost_its_lease_stops_without_completing_and_counts_lease_lost()
    {
        using var cap = new Capture();
        var log = new CapturingLog();
        Rig? r = null;
        r = new Rig(inner => new HookRunStore(inner, n =>
        {
            if (n == 50) { r!.Clock.Now += TimeSpan.FromSeconds(61); Assert.NotNull(r.Jobs.TryClaimChunk(r.Clock.Now, SweepWorker.Lease, "thief")); }
        }), log);
        var job = r.Go(Rates(120));
        Assert.True(r.Worker.ProcessOne("w1"));
        Assert.Equal(1, cap.Sum("hue.sweep.chunks", "lease_lost"));
        Assert.Equal(0, cap.Sum("hue.sweep.chunks", "done"));
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal((0, 0, 1), (p.CompletedCases, p.Done, p.Leased));           // nothing stored, chunk still leased by the thief
        Assert.Contains(log.Lines, l => l.Contains("lost its lease"));
        Assert.Equal(50, r.Ledger.Entries.Count(e => e.Action == LedgerEntry.RunComputed)); // it stopped right after the renewal check
    }

    [Fact]
    public void A_jobs_plan_is_built_once_per_process_not_once_per_chunk()
    {
        var plans = new SweepPlanCache();
        var r = new Rig(plans: plans);
        var job = r.Go(Enumerable.Repeat(Rate(0.07), 2 * SweepLimits.ChunkSize + 100).ToArray()); // 3 chunks, one distinct run
        Assert.Equal(3, job.ChunkCount);
        r.Drain();
        Assert.Equal(1, plans.Builds);
        Assert.Same(plans.Get(job), plans.Get(job));
        Assert.Equal(1, plans.Builds);
    }

    [Fact]
    public void The_plan_cache_is_bounded_and_evicts_the_oldest_job()
    {
        var plans = new SweepPlanCache();
        var r = new Rig();
        var jobs = Enumerable.Range(0, SweepPlanCache.Capacity + 1).Select(i => r.Go(Rate(0.05 + i * 0.001))).ToList();
        foreach (var j in jobs) plans.Get(j);
        Assert.Equal(SweepPlanCache.Capacity + 1, plans.Builds);
        plans.Get(jobs[^1]);                    // newest is still cached
        Assert.Equal(SweepPlanCache.Capacity + 1, plans.Builds);
        plans.Get(jobs[0]);                     // oldest was evicted: built again
        Assert.Equal(SweepPlanCache.Capacity + 2, plans.Builds);
    }

    [Fact]
    public void Cancelling_mid_chunk_releases_it_for_retry_within_seconds_without_counting_an_attempt() // and a cancelled worker claims nothing
    {
        using var cap = new Capture();
        using var cts = new CancellationTokenSource();
        var r = new Rig(inner => new HookRunStore(inner, n => { if (n == 10) cts.Cancel(); }));
        var job = r.Go(Rates(SweepLimits.ChunkSize));
        var clock = Stopwatch.StartNew();
        Assert.True(r.Worker.ProcessOne("w1", cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        var p = r.Jobs.GetProgress(job.Id)!;
        Assert.Equal((1, 0, 0, 0), (p.Queued, p.Leased, p.Done, p.CompletedCases));
        Assert.Equal(0, cap.Sum("hue.sweep.chunks", "failed") + cap.Sum("hue.sweep.chunks", "done"));
        Assert.False(r.Worker.ProcessOne("w1", cts.Token));
        Assert.Equal(1, r.Jobs.TryClaimChunk(r.Clock.Now, SweepWorker.Lease, "w2")!.Attempts); // the released claim was given back
    }

    [Fact]
    public void Slow_cases_renew_the_lease_before_fifty_cases_are_done()
    {
        Rig? r = null;
        var polls = new List<bool>();
        r = new Rig(inner => new HookRunStore(inner, n =>
        {
            r!.Clock.Now += TimeSpan.FromSeconds(2);                  // 50 cases would take 100 s, far past the 60 s lease
            if (n == 45) polls.Add(r.Worker.ProcessOne("w2"));        // T+90
        }));
        var job = r.Go(Rates(60));
        Assert.True(r.Worker.ProcessOne("w1"));
        Assert.Equal(new[] { false }, polls);
        Assert.Equal("succeeded", r.Jobs.GetProgress(job.Id)!.Status);
    }
}
