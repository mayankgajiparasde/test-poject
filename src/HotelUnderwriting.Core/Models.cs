namespace HotelUnderwriting.Core;

public sealed record PropertyInputs(string Name, int Keys, DateTime AcquisitionDate, int BaseYear, double BaseOccupancy, double BaseAdr);

/// <summary>One row of the Operating Inputs line table. Section is REV, DEPT, UNDIST or FIXED.</summary>
public sealed record OperatingLine(string Code, string Name, string Section, double BaseAmount, double FixedPct, string Driver);

/// <summary>Percent-of-revenue item (FRAN, MGMT, FFE): Pct x the Base driver series.</summary>
public sealed record PercentItem(string Code, string Name, double Pct, string Base);

public sealed record ScenarioInputs
{
    public required string Name { get; init; }
    public required double PurchasePrice { get; init; }
    public required double ClosingCosts { get; init; }
    public required double AcquisitionFeePct { get; init; }
    public required double PipPerKey { get; init; }
    public required int PipStartMonth { get; init; }
    public required int PipDurationMonths { get; init; }
    public required double ExpenseInflation { get; init; }
    public required double LoanToCost { get; init; }
    public required double InterestRate { get; init; }
    public required int InterestOnlyMonths { get; init; }
    public required double AmortizationYears { get; init; }
    public required double OriginationFeePct { get; init; }
    public required double PlacementCosts { get; init; }
    public required double AssetMgmtFeePct { get; init; }
    public required int HoldMonths { get; init; }
    public required double ExitCapRate { get; init; }
    public required double CostOfSalePct { get; init; }
    public required double DispositionFeePct { get; init; }
    public required double GpShare { get; init; }
    public required double PrefReturn { get; init; }
    public required double Promote { get; init; }
    public required IReadOnlyList<int> Years { get; init; }
    public required IReadOnlyList<double> Occupancy { get; init; }
    public required IReadOnlyList<double> AdrGrowth { get; init; }
    /// <summary>Optional debt events applied in list order (strictly increasing months). Empty = single loan for the whole hold.</summary>
    public IReadOnlyList<DebtEvent> DebtEvents { get; init; } = Array.Empty<DebtEvent>();
    /// <summary>Optional promote tiers above the pref, strictly increasing LP IRR hurdles. Empty = one flat <see cref="Promote"/> on all residual cash.</summary>
    public IReadOnlyList<HurdleTier> HurdleTiers { get; init; } = Array.Empty<HurdleTier>();
}

/// <summary>Once the LP has earned <paramref name="LpIrrHurdle"/>, the GP promote on residual cash becomes <paramref name="Promote"/>. The hurdle is an effective annual LP IRR, accrued as (1+h)^(1/12) per month; the LP balance is clipped at 0.</summary>
public sealed record HurdleTier(double LpIrrHurdle, double Promote);

/// <summary>Base type for debt events; each applies at the end of <see cref="Month"/>.</summary>
public abstract record DebtEvent
{
    public required int Month { get; init; }
}

/// <summary>
/// Refinance at the end of month <see cref="DebtEvent.Month"/>: the balance (after that month's draw and scheduled principal)
/// is repaid by a new loan of the same amount (no cash out), fee = FeePct x that balance charged in the same month.
/// New terms apply from the next month. The fee is on the funded balance (not the commitment). Undrawn PIP holdback keeps
/// drawing under the original commitment cap. <see cref="InterestOnlyMonths"/> counts from month R+1; amortization restarts at R+1.
/// </summary>
public sealed record Refinance : DebtEvent
{
    public required double Rate { get; init; }
    public required int InterestOnlyMonths { get; init; }
    public required double AmortizationYears { get; init; }
    public required double FeePct { get; init; }
}

public sealed record Assumptions(
    PropertyInputs Property,
    IReadOnlyList<OperatingLine> Lines,
    IReadOnlyList<PercentItem> PercentItems,
    IReadOnlyList<double> Seasonality,
    IReadOnlyList<ScenarioInputs> Scenarios);

/// <summary>
/// Workbook errors that can be switched on to replicate the reference workbook (used only to
/// attribute reconciliation differences and to quantify their IRR impact). Default = READ ME spec.
/// </summary>
[Flags]
public enum WorkbookQuirks
{
    None = 0,
    /// <summary>#1 Monthly CF row 73: payoff = OPENING balance of the exit month.</summary>
    PayoffOnOpeningBalance = 1,
    /// <summary>#2 Monthly CF B37: pref accrues at 1 + pref/12 instead of (1 + pref)^(1/12).</summary>
    SimplePrefAccrual = 2,
    /// <summary>#3 Proforma - Downside E26:L26: F&amp;B expense driven by rooms revenue from 2029.</summary>
    FbExpenseOnRoomsRevenue = 4,
}
