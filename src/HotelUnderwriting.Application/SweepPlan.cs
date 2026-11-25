using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public static class SweepLimits
{
    public const int MaxCases = 100_000;
    public const int MaxGridAxes = 20;
    public const int MaxValuesPerAxis = 1_000;
    public const int MaxOverridesPerCase = 20;
    public const int MaxCaseNameLength = 100;
    public const int ChunkSize = 500;
    public const int MaxAttempts = 5;
    public const int MaxIdempotencyKeyLength = 128;

    public static bool IsValidIdempotencyKey(string key) =>
        key.Length is >= 1 and <= MaxIdempotencyKeyLength && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

/// <summary>The allowed override paths (camelCase numeric <see cref="ScenarioInputs"/> fields) and how to apply them. Vectors, events and tiers are never touched.</summary>
public static class SweepOverrides
{
    private static readonly Dictionary<string, (bool IsInt, Func<ScenarioInputs, double, ScenarioInputs> Set)> Paths = new()
    {
        ["purchasePrice"] = (false, (s, v) => s with { PurchasePrice = v }),
        ["closingCosts"] = (false, (s, v) => s with { ClosingCosts = v }),
        ["acquisitionFeePct"] = (false, (s, v) => s with { AcquisitionFeePct = v }),
        ["pipPerKey"] = (false, (s, v) => s with { PipPerKey = v }),
        ["pipStartMonth"] = (true, (s, v) => s with { PipStartMonth = (int)v }),
        ["pipDurationMonths"] = (true, (s, v) => s with { PipDurationMonths = (int)v }),
        ["expenseInflation"] = (false, (s, v) => s with { ExpenseInflation = v }),
        ["loanToCost"] = (false, (s, v) => s with { LoanToCost = v }),
        ["interestRate"] = (false, (s, v) => s with { InterestRate = v }),
        ["interestOnlyMonths"] = (true, (s, v) => s with { InterestOnlyMonths = (int)v }),
        ["amortizationYears"] = (false, (s, v) => s with { AmortizationYears = v }),
        ["originationFeePct"] = (false, (s, v) => s with { OriginationFeePct = v }),
        ["placementCosts"] = (false, (s, v) => s with { PlacementCosts = v }),
        ["assetMgmtFeePct"] = (false, (s, v) => s with { AssetMgmtFeePct = v }),
        ["holdMonths"] = (true, (s, v) => s with { HoldMonths = (int)v }),
        ["exitCapRate"] = (false, (s, v) => s with { ExitCapRate = v }),
        ["costOfSalePct"] = (false, (s, v) => s with { CostOfSalePct = v }),
        ["dispositionFeePct"] = (false, (s, v) => s with { DispositionFeePct = v }),
        ["gpShare"] = (false, (s, v) => s with { GpShare = v }),
        ["prefReturn"] = (false, (s, v) => s with { PrefReturn = v }),
        ["promote"] = (false, (s, v) => s with { Promote = v }),
    };

    public static void Validate(string path, double value)
    {
        if (path is null || !Paths.TryGetValue(path, out var p)) throw new InvalidAssumptionsException("Unknown override path.");
        if (!double.IsFinite(value)) throw new InvalidAssumptionsException($"Override '{path}' must be a finite number.");
        if (p.IsInt && (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue))
            throw new InvalidAssumptionsException($"Override '{path}' must be a whole number.");
    }

    /// <summary>Applies validated overrides in list order, last write wins.</summary>
    public static ScenarioInputs Apply(ScenarioInputs s, IEnumerable<ScenarioOverride> overrides)
    {
        foreach (var o in overrides) s = Paths[o.Path].Set(s, o.Value);
        return s;
    }
}

/// <summary>A validated sweep: the cases are computed by index (the grid is never expanded into a list).</summary>
public sealed class SweepPlan
{
    private readonly SweepRequest _request;
    private readonly int[] _stride;

    public int CaseCount { get; }
    public int ChunkCount => (CaseCount + SweepLimits.ChunkSize - 1) / SweepLimits.ChunkSize;

    private SweepPlan(SweepRequest request, int caseCount, int[] stride) { _request = request; CaseCount = caseCount; _stride = stride; }

    /// <exception cref="InvalidAssumptionsException">The request breaks a rule or a limit.</exception>
    public static SweepPlan Create(SweepRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if ((r.Cases is null) == (r.Grid is null)) throw new InvalidAssumptionsException("Exactly one of cases and grid is required.");
        if (r.Cases is { } cases)
        {
            if (cases.Count is 0 or > SweepLimits.MaxCases) throw new InvalidAssumptionsException($"cases must contain 1 to {SweepLimits.MaxCases} items.");
            foreach (var c in cases)
            {
                if (c?.Overrides is null) throw new InvalidAssumptionsException("Every case needs an overrides list.");
                if (c.Name is { Length: > SweepLimits.MaxCaseNameLength }) throw new InvalidAssumptionsException($"A case name may have at most {SweepLimits.MaxCaseNameLength} characters.");
                if (c.Overrides.Count > SweepLimits.MaxOverridesPerCase) throw new InvalidAssumptionsException($"A case may have at most {SweepLimits.MaxOverridesPerCase} overrides.");
                foreach (var o in c.Overrides)
                {
                    if (o is null) throw new InvalidAssumptionsException("Overrides must not contain null.");
                    SweepOverrides.Validate(o.Path, o.Value);
                }
            }
            return new SweepPlan(r, cases.Count, []);
        }

        var grid = r.Grid!;
        if (grid.Count is 0 or > SweepLimits.MaxGridAxes) throw new InvalidAssumptionsException($"grid must contain 1 to {SweepLimits.MaxGridAxes} axes.");
        long count = 1;
        foreach (var axis in grid)
        {
            if (axis?.Values is null) throw new InvalidAssumptionsException("Every grid axis needs a path and values.");
            if (axis.Values.Count is 0 or > SweepLimits.MaxValuesPerAxis) throw new InvalidAssumptionsException($"A grid axis must have 1 to {SweepLimits.MaxValuesPerAxis} values.");
            if (grid.Count(g => g?.Path == axis.Path) > 1) throw new InvalidAssumptionsException("A grid axis path may appear only once.");
            foreach (var v in axis.Values) SweepOverrides.Validate(axis.Path, v);
            count *= axis.Values.Count;
            if (count > SweepLimits.MaxCases) throw new InvalidAssumptionsException($"The grid expands to more than {SweepLimits.MaxCases} cases.");
        }
        var stride = new int[grid.Count];
        var s = 1;
        for (var k = grid.Count - 1; k >= 0; k--) { stride[k] = s; s *= grid[k].Values.Count; }
        return new SweepPlan(r, (int)count, stride);
    }

    /// <summary>Case <paramref name="index"/> (0-based); for a grid the first axis varies slowest. Grid cases have no name.</summary>
    public (string? Name, IReadOnlyList<ScenarioOverride> Overrides) CaseAt(int index)
    {
        if ((uint)index >= (uint)CaseCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (_request.Cases is { } cases) return (cases[index].Name, cases[index].Overrides);
        var grid = _request.Grid!;
        var list = new ScenarioOverride[grid.Count];
        for (var k = 0; k < grid.Count; k++) list[k] = new ScenarioOverride(grid[k].Path, grid[k].Values[index / _stride[k] % grid[k].Values.Count]);
        return (null, list);
    }
}
