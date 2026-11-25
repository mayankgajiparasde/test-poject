using System.Diagnostics;
using System.Text.Json;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>
/// Content-addressed run (AD-6): the same inputs, engine version and conventions share one stored record.
/// Sequential repeats compute once. Concurrent first submits of the same key may each compute; the store keeps the first record and every caller receives it.
/// </summary>
public sealed class SubmitRun
{
    private readonly IRunStore _store;
    private readonly IClock _clock;
    private readonly string _engineVersion;
    private readonly ILedger _ledger;

    public SubmitRun(IRunStore store, IClock clock, string? engineVersion = null, ILedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        engineVersion ??= EngineInfo.Version;
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
        _store = store;
        _clock = clock;
        _engineVersion = engineVersion;
        _ledger = ledger ?? new NullLedger();
    }

    public SubmitResult Submit(Assumptions a, ScenarioInputs s, WorkbookQuirks quirks = WorkbookQuirks.None, string actor = "anonymous", string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString();
        var clock = Stopwatch.StartNew();
        using var span = Telemetry.Start("run.submit");
        var outcome = "error"; // store, ledger or other infrastructure failure
        try
        {
            var result = SubmitCore(a, s, quirks, actor, correlationId);
            outcome = result.WasCached ? "cached" : "computed";
            span.Tag("run_key", result.Record.RunKey);
            return result;
        }
        catch (ArgumentException) { outcome = "rejected"; throw; } // includes InvalidAssumptionsException
        finally
        {
            Telemetry.RecordRun(outcome, clock.Elapsed.TotalMilliseconds);
            span.Tag("engine_version", _engineVersion);
            span.Tag("correlation_id", correlationId);
            span.Tag("outcome", outcome);
        }
    }

    private SubmitResult SubmitCore(Assumptions a, ScenarioInputs s, WorkbookQuirks quirks, string actor, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(s);
        // AD-3: error-replication switches never run through the application layer.
        if (quirks != WorkbookQuirks.None)
            throw new ArgumentException("Workbook quirks are not allowed through the application layer.", nameof(quirks));

        string key;
        try { key = RunKey.Compute(a, s, _engineVersion, quirks); }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new ArgumentException("Inputs contain a non-finite number (NaN or Infinity) or cannot be serialized.", e);
        }
        LedgerEntry Entry(bool computed, RunRecord r) => new(_clock.UtcNow, actor, computed ? LedgerEntry.RunComputed : LedgerEntry.RunServedFromStore,
            r.RunKey, r.EngineVersion, r.RunKey, LedgerEntry.OutputHashOf(r.Result.Summary), correlationId);

        if (_store.TryGet(key, out var existing))
        {
            _ledger.Append(Entry(false, existing)); // cached path: only a ledger row, its own transaction
            return new SubmitResult(existing, true);
        }

        AssumptionRules.CheckAssumptions(a); // not for a stored run: those are never re-checked
        AssumptionRules.CheckScenario(s);
        ScenarioResult result;
        try { result = Underwriter.Run(a, s, quirks); }
        // engine failures only (its ArgumentExceptions pass as they are); store failures stay InvalidOperation
        catch (Exception e) when (e is not ArgumentException && InvalidAssumptionsException.FromEngine(e) is not null) { throw InvalidAssumptionsException.FromEngine(e)!; }
        AssumptionRules.CheckFinite(result, a);
        var record = new RunRecord(key, _engineVersion, _clock.UtcNow, result);
        RunRecord stored;
        if (_store is IAtomicRunStore atomic) stored = atomic.SaveIfAbsent(record, computed => Entry(computed, record));
        else // ponytail: a store without atomic support (test doubles) gets state then ledger, not one unit
        {
            stored = _store.SaveIfAbsent(record);
            _ledger.Append(Entry(ReferenceEquals(stored, record), record));
        }
        return new SubmitResult(stored, !ReferenceEquals(stored, record));
    }
}
