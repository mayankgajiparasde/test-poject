using System.Reflection;
using System.Text.Json;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Api;

/// <summary>
/// Readiness proof that the engine still computes correctly: recomputes an embedded Base scenario and compares the headline outputs to constants.
/// The verdict is cached for <see cref="CacheFor"/>. A failure never reveals a figure.
/// </summary>
public sealed class GoldenSelfTest(IClock clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    public const double GoldenLeveredIrr = 0.18385962063924477;
    public const double GoldenEquityAtClose = 24966415;

    private readonly object _gate = new();
    private (bool Ok, DateTimeOffset At)? _last;
    private double _irr = GoldenLeveredIrr, _equity = GoldenEquityAtClose;

    /// <summary>Test hook: the expected levered IRR; setting it drops the cached verdict.</summary>
    public double ExpectedLeveredIrr { get => _irr; set { lock (_gate) { _irr = value; _last = null; } } }
    /// <summary>Test hook: the expected equity at close; setting it drops the cached verdict.</summary>
    public double ExpectedEquityAtClose { get => _equity; set { lock (_gate) { _equity = value; _last = null; } } }

    /// <summary>How many times the engine was actually recomputed (a test seam).</summary>
    public int Evaluations { get; private set; }

    public bool Check()
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            if (_last is { } l && now - l.At < CacheFor) return l.Ok;
            Evaluations++;
            var ok = Evaluate();
            _last = (ok, now);
            return ok;
        }
    }

    private bool Evaluate()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("golden-base.json")!;
            var a = JsonSerializer.Deserialize<Assumptions>(s, ApiHost.Json)!;
            var sum = Underwriter.Run(a, a.Scenarios.First(x => x.Name == "Base")).Summary;
            return sum.LeveredIrr is { } irr && Math.Abs(irr - _irr) <= 1e-9 && Math.Abs(sum.EquityAtClose - _equity) <= 0.01;
        }
        catch { return false; }
    }
}

/// <summary>Writes worker and gauge log lines to ILogger.</summary>
public sealed class LoggerWorkLog(ILogger log) : IWorkLog
{
    // A logging provider that throws must not fail a chunk.
    public void Info(string template, params object?[] args) { try { log.LogInformation(template, args); } catch { } }
    public void Warn(string template, params object?[] args) { try { log.LogWarning(template, args); } catch { } }

    public IDisposable? Scope(string? correlationId, string? jobId, string? runKey)
    {
        var d = new Dictionary<string, object>();
        if (correlationId is not null) d["correlation_id"] = correlationId;
        if (jobId is not null) d["job_id"] = jobId;
        if (runKey is not null) d["run_key"] = runKey;
        return log.BeginScope(d);
    }
}
