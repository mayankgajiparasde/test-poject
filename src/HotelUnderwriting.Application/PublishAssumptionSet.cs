using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>Publishes an immutable assumption set (AD-5). Every scenario is run once so an invalid set is rejected with an <see cref="InvalidAssumptionsException"/> and nothing is stored.</summary>
public sealed class PublishAssumptionSet(IAssumptionSetStore store, IClock clock, ILedger? ledger = null)
{
    // Size limits of a published set: they bound the work of every later publish, run and sweep.
    public const int MaxScenarios = 20, MaxLines = 500, MaxPercentItems = 50, MaxYears = 50, MaxTextLength = 100;

    private readonly ILedger _ledger = ledger ?? new NullLedger();

    /// <summary><c>Created</c> is false when an identical set was already published.</summary>
    public (AssumptionSetRecord Record, bool Created) Publish(Assumptions a, string actor = "anonymous", string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Property is null || a.Lines is null || a.PercentItems is null || a.Seasonality is null || a.Scenarios is null || a.Scenarios.Any(s => s is null))
            throw new InvalidAssumptionsException("Assumptions must contain property, lines, percentItems, seasonality and scenarios.");
        if (a.Scenarios.Count == 0) throw new InvalidAssumptionsException("Assumptions must contain at least one scenario.");
        CheckSize(a); // before anything whose cost grows with the model
        if (a.Scenarios.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.Scenarios.Count)
            throw new InvalidAssumptionsException("Scenario names must be unique, ignoring case.");
        // every rule that needs no engine run comes before the store: a broken set is a 422 also while the database is down
        AssumptionRules.CheckAssumptions(a);
        foreach (var s in a.Scenarios) AssumptionRules.CheckScenario(s);

        var id = AssumptionSetId.Compute(a);
        correlationId ??= Guid.NewGuid().ToString();
        LedgerEntry Entry() => new(clock.UtcNow, actor, LedgerEntry.AssumptionSetPublished, id, null, id, id, correlationId);
        if (store.TryGet(id, out var existing))
        {
            _ledger.Append(Entry()); // republish: only a ledger row, its own transaction
            return (existing, false);
        }

        foreach (var s in a.Scenarios)
        {
            ScenarioResult result;
            try { result = Underwriter.Run(a, s); }
            catch (Exception e) when (InvalidAssumptionsException.FromEngine(e) is not null) { throw InvalidAssumptionsException.FromEngine(e)!; }
            AssumptionRules.CheckFinite(result, a);
        }

        var record = new AssumptionSetRecord(id, a, clock.UtcNow);
        AssumptionSetRecord stored;
        if (store is IAtomicAssumptionSetStore atomic) stored = atomic.SaveIfAbsent(record, Entry());
        else // ponytail: a store without atomic support (test doubles) gets state then ledger, not one unit
        {
            stored = store.SaveIfAbsent(record);
            _ledger.Append(Entry());
        }
        return (stored, ReferenceEquals(stored, record));
    }

    private static void CheckSize(Assumptions a)
    {
        static void AtMost(int count, int max, string what)
        {
            if (count > max) throw new InvalidAssumptionsException($"Assumptions may contain at most {max} {what}.");
        }
        static void Text(params string?[] values)
        {
            foreach (var v in values) AssumptionRules.CheckText(v);
        }
        AtMost(a.Scenarios.Count, MaxScenarios, "scenarios");
        AtMost(a.Lines.Count, MaxLines, "operating lines");
        AtMost(a.PercentItems.Count, MaxPercentItems, "percent-of-revenue items");
        if (a.Lines.Any(l => l is null) || a.PercentItems.Any(p => p is null))
            throw new InvalidAssumptionsException("Assumptions contain a missing or invalid value.");
        Text(a.Property.Name);
        foreach (var l in a.Lines) Text(l.Code, l.Name, l.Section, l.Driver);
        foreach (var p in a.PercentItems) Text(p.Code, p.Name, p.Base);
        foreach (var s in a.Scenarios)
        {
            Text(s.Name);
            AtMost(Math.Max(s.Years?.Count ?? 0, Math.Max(s.Occupancy?.Count ?? 0, s.AdrGrowth?.Count ?? 0)), MaxYears, "years per scenario");
        }
    }
}
