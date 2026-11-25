using System.Diagnostics;
using System.Diagnostics.Metrics;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>
/// The service's metrics and spans (in-box System.Diagnostics, no package). Instrument and span names are a contract, see the README.
/// Only ids, hashes, counts, durations and outcomes are ever tagged: never a deal figure. No call here throws.
/// </summary>
public static class Telemetry
{
    public const string Name = "HotelUnderwriting";

    public static readonly Meter Meter = new(Name, EngineInfo.Version);
    public static readonly ActivitySource Source = new(Name, EngineInfo.Version);

    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("hue.run.duration", "ms", "Time to submit one run");
    public static readonly Counter<long> Runs = Meter.CreateCounter<long>("hue.runs", description: "Runs submitted by outcome");
    public static readonly Counter<long> SweepChunks = Meter.CreateCounter<long>("hue.sweep.chunks", description: "Sweep chunks by outcome");
    public static readonly Counter<long> SweepCases = Meter.CreateCounter<long>("hue.sweep.cases", description: "Sweep cases by outcome");

    public static void RecordRun(string outcome, double ms)
    {
        try
        {
            var tag = new KeyValuePair<string, object?>("outcome", outcome);
            RunDuration.Record(ms, tag);
            Runs.Add(1, tag);
        }
        catch { /* telemetry never breaks the caller */ }
    }

    public static void Count(Counter<long> counter, string outcome, long n = 1)
    {
        if (n == 0) return;
        try { counter.Add(n, new KeyValuePair<string, object?>("outcome", outcome)); }
        catch { }
    }

    public static Activity? Start(string name)
    {
        try { return Source.StartActivity(name); }
        catch { return null; }
    }

    public static void Tag(this Activity? a, string key, object? value)
    {
        try { a?.SetTag(key, value); }
        catch { }
    }
}

/// <summary>Where the engine and worker write log lines (the API adapts this to ILogger). Templates use named placeholders; arguments are ids and counts only.</summary>
public interface IWorkLog
{
    void Info(string template, params object?[] args);
    void Warn(string template, params object?[] args);
    IDisposable? Scope(string? correlationId, string? jobId, string? runKey);
}

public sealed class NullWorkLog : IWorkLog
{
    public void Info(string template, params object?[] args) { }
    public void Warn(string template, params object?[] args) { }
    public IDisposable? Scope(string? correlationId, string? jobId, string? runKey) => null;
}

/// <summary>
/// The three queue gauges (<c>hue.queue.depth</c>, <c>hue.queue.oldest_age</c>, <c>hue.queue.dead</c>) read from <see cref="IJobStore.GetQueueStats"/>.
/// Each instance owns its own meter (same name), so disposing it removes exactly its gauges.
/// A failing read is swallowed and logged once; the gauge then reports nothing.
/// </summary>
public sealed class QueueGauges : IDisposable
{
    public Meter Meter { get; } = new(Telemetry.Name, EngineInfo.Version);
    private int _warned;

    public QueueGauges(IJobStore jobs, IClock clock, IWorkLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(clock);
        IEnumerable<Measurement<double>> Read(Func<(int Queued, double OldestQueuedAgeSeconds, int Dead), double> pick)
        {
            try
            {
                var value = pick(jobs.GetQueueStats(clock.UtcNow));
                Interlocked.Exchange(ref _warned, 0); // the next outage is logged again
                return [new Measurement<double>(value)];
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _warned, 1) == 0) try { log?.Warn("Queue gauge read failed: {ErrorType}", e.GetType().Name); } catch { }
                return [];
            }
        }
        Meter.CreateObservableGauge("hue.queue.depth", () => Read(s => s.Queued), description: "Queued plus expired-lease chunks");
        Meter.CreateObservableGauge("hue.queue.oldest_age", () => Read(s => s.OldestQueuedAgeSeconds), "s", "Age of the oldest waiting chunk");
        Meter.CreateObservableGauge("hue.queue.dead", () => Read(s => s.Dead), description: "Chunks that used all attempts");
    }

    public void Dispose() => Meter.Dispose();
}

/// <summary>A small bounded cache of validated sweep plans by job id (oldest evicted first), so one job is validated once per process.</summary>
public sealed class SweepPlanCache(Func<SweepRequest, SweepPlan>? factory = null)
{
    public const int Capacity = 64;
    private readonly Func<SweepRequest, SweepPlan> _factory = factory ?? SweepPlan.Create;
    private readonly object _gate = new();
    private readonly Dictionary<string, SweepPlan> _plans = new();
    private readonly Queue<string> _order = new();
    private long _builds;

    /// <summary>How many times a plan was actually built (a test seam).</summary>
    public long Builds => Interlocked.Read(ref _builds);

    public SweepPlan Get(JobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (_plans.TryGetValue(job.Id, out var hit)) return hit;
            var plan = _factory(job.Request); // throws for an invalid request: nothing is cached
            _builds++;
            if (_plans.Count >= Capacity) _plans.Remove(_order.Dequeue());
            _plans[job.Id] = plan;
            _order.Enqueue(job.Id);
            return plan;
        }
    }
}
