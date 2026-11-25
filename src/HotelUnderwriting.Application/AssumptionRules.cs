using System.Globalization;
using System.Text;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>
/// The one place for the business limits of an assumption set and its scenarios, shared by publish (JSON and Excel) and every run (so also every sweep case).
/// Every violation is an <see cref="InvalidAssumptionsException"/> with a fixed text that names the field and never quotes a value.
/// </summary>
public static class AssumptionRules
{
    /// <summary>Length and character rule for every name, code, section, driver and sweep case name.</summary>
    public static void CheckText(string? v)
    {
        if (v is not null && (v.Length > PublishAssumptionSet.MaxTextLength || HasForbidden(v)))
            throw new InvalidAssumptionsException($"Names, codes, sections and drivers may have at most {PublishAssumptionSet.MaxTextLength} characters and no control characters, format or invisible characters (such as bidi controls and zero-width characters), lone surrogates, private-use or unassigned characters, or line and paragraph separators.");
    }

    /// <summary>Refused by Unicode category: Cc, Cf, Cs (a lone surrogate), Co, Cn, Zl, Zp; also U+034F, the combining grapheme joiner. Letters of any script, hyphens and inner spaces pass.</summary>
    private static bool HasForbidden(string v)
    {
        for (var i = 0; i < v.Length; i++)
        {
            if (char.IsHighSurrogate(v[i]) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1])) { i++; if (Bad(CharUnicodeInfo.GetUnicodeCategory(v, i - 1))) return true; continue; }
            if (char.IsSurrogate(v[i]) || v[i] == '\u034F' || Bad(CharUnicodeInfo.GetUnicodeCategory(v[i]))) return true;
        }
        return false;
    }

    private static bool Bad(UnicodeCategory c) =>
        c is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>A sweep case name (when there is one) follows the text rule and is not empty or padded. New submissions only.</summary>
    public static void CheckCaseName(string? name)
    {
        CheckText(name);
        if (name is not null) CheckKey(name, "sweep case name");
    }

    private static void CheckKey(string? v, string what)
    {
        if (string.IsNullOrEmpty(v) || char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1]))
            throw new InvalidAssumptionsException($"A {what} must not be empty or start or end with whitespace.");
    }

    private static void CheckUnique(IEnumerable<string> keys, string what)
    {
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count()) throw new InvalidAssumptionsException($"Each {what} must be unique.");
    }

    /// <summary>The rules of everything that is not one scenario.</summary>
    public static void CheckAssumptions(Assumptions a)
    {
        var p = a.Property;
        if (!(p.BaseOccupancy >= 0 && p.BaseOccupancy <= 1)) throw new InvalidAssumptionsException("Base occupancy must be from 0 to 1.");
        if (!(p.BaseAdr > 0)) throw new InvalidAssumptionsException("Base ADR must be greater than 0.");
        var d = p.AcquisitionDate;
        if (d.Day != 1 || d.Ticks % TimeSpan.TicksPerDay != 0) throw new InvalidAssumptionsException("The acquisition date must be the 1st of a month, at midnight.");
        if (d > DateTime.MaxValue.AddMonths(-(Underwriter.Horizon + 1))) throw new InvalidAssumptionsException("The acquisition date is too far in the future.");
        if (a.Lines.Count == 0) throw new InvalidAssumptionsException("Assumptions must contain at least one operating line.");
        foreach (var l in a.Lines)
        {
            CheckKey(l.Code, "line code");
            if (!(l.FixedPct >= 0 && l.FixedPct <= 1)) throw new InvalidAssumptionsException("A line's fixed share must be from 0 to 1.");
            if (!(l.BaseAmount >= 0)) throw new InvalidAssumptionsException("A line's base amount must not be negative.");
        }
        CheckUnique(a.Lines.Select(l => l.Code), "line code");
        foreach (var i in a.PercentItems)
        {
            CheckKey(i.Code, "percent-of-revenue item code");
            if (!(i.Pct >= 0 && i.Pct <= 1)) throw new InvalidAssumptionsException("A percent-of-revenue rate must be from 0 to 1.");
        }
        CheckUnique(a.PercentItems.Select(i => i.Code), "percent-of-revenue item code");
        if (!(a.PercentItems.Sum(i => i.Pct) <= 1)) throw new InvalidAssumptionsException("The percent-of-revenue rates must add up to at most 1.");
        foreach (var s in a.Scenarios) CheckKey(s.Name, "scenario name");
    }

    /// <summary>The rules of one scenario, the (overridden) one that is about to run.</summary>
    public static void CheckScenario(ScenarioInputs s)
    {
        void Need(bool ok, string rule) { if (!ok) throw new InvalidAssumptionsException($"{s.Name}: {rule}"); }
        static bool Unit(double v) => v >= 0 && v <= 1; // false for NaN
        Need(s.PurchasePrice > 0, "purchase price must be greater than 0.");
        Need(s.ClosingCosts >= 0, "closing costs must not be negative.");
        Need(s.PlacementCosts >= 0, "placement costs must not be negative.");
        Need(s.PipPerKey >= 0, "PIP per key must not be negative.");
        Need(Unit(s.AcquisitionFeePct), "acquisition fee must be from 0 to 1.");
        Need(Unit(s.OriginationFeePct), "origination fee must be from 0 to 1.");
        Need(Unit(s.DispositionFeePct), "disposition fee must be from 0 to 1.");
        Need(Unit(s.CostOfSalePct), "cost of sale must be from 0 to 1.");
        Need(s.CostOfSalePct + s.DispositionFeePct <= 1, "cost of sale and disposition fee together must be at most 1.");
        Need(Unit(s.AssetMgmtFeePct), "asset management fee must be from 0 to 1.");
        Need(Unit(s.InterestRate), "interest rate must be from 0 to 1.");
        Need(s.ExitCapRate <= 1, "exit cap rate must be at most 1.");
        Need(Unit(s.LoanToCost), "loan-to-cost must be from 0 to 1.");
        Need(Unit(s.GpShare), "GP share must be from 0 to 1.");
        Need(Unit(s.Promote), "promote must be from 0 to 1.");
        Need(Unit(s.PrefReturn), "preferred return must be from 0 to 1.");
        Need(s.ExpenseInflation > -1, "expense inflation must be greater than -1.");
        Need(s.InterestOnlyMonths >= 0, "interest-only months must not be negative.");
        Need(s.Occupancy.All(Unit), "occupancy must be from 0 to 1.");
        Need(s.AdrGrowth.All(g => g > -1), "ADR growth must be greater than -1.");
        Need(s.Years.Zip(s.Years.Skip(1)).All(y => y.First < y.Second), "years must be strictly ascending.");
        // the lower bounds of refinance rate and fee, and of the hurdle against the pref, are the engine's own rules
        foreach (var e in s.DebtEvents.OfType<Refinance>())
        {
            Need(!(e.Rate > 1), "a refinance rate must be at most 1.");
            Need(!(e.FeePct > 1), "a refinance fee must be at most 1.");
        }
        foreach (var h in s.HurdleTiers) Need(!(h.LpIrrHurdle > 1), "a hurdle must be at most 1.");
        if (s.PipPerKey > 0)
        {
            Need(s.PipDurationMonths >= 1, "PIP duration must be at least 1 month.");
            Need(s.PipStartMonth >= 1, "PIP start month must be at least 1.");
            if (s.HoldMonths is >= Underwriter.MinHold and <= Underwriter.MaxHold) // the hold period itself is the engine's rule, with its own text
                Need((long)s.PipStartMonth + s.PipDurationMonths - 1 <= s.HoldMonths, "the PIP must end within the hold period.");
        }
    }

    /// <summary>Every number of a result must be finite: summary, monthly (also the pref series) and proforma. An absent IRR (null) is fine.</summary>
    public static void CheckFinite(ScenarioResult r, Assumptions? a = null)
    {
        static bool Finite(object o) => o.GetType().GetProperties().All(p => p.GetValue(o) switch
        {
            double d => double.IsFinite(d),
            double[] series => series.All(double.IsFinite),
            _ => true,
        });
        if (!(Finite(r.Summary) && Finite(r.Monthly) && Finite(r.Proforma) && r.Proforma.Lines.All(l => l.Amounts.All(double.IsFinite))))
            throw new InvalidAssumptionsException(a is null ? InvalidAssumptionsException.NotFinite : ZeroBaseDriver(a) ?? InvalidAssumptionsException.NotFinite);
    }

    /// <summary>
    /// A line scales with its driver as driver(year) / driver(base year), so a driver whose base year is 0 gives 0/0. Names the first such line (its code and a driver that is a built-in series or another line's code passed the character rules);
    /// null when no driver explains the failure. Only used to explain a refusal that already happens.
    /// </summary>
    private static string? ZeroBaseDriver(Assumptions a)
    {
        var revenueBase = a.Lines.Where(l => l.Section == "REV").Sum(l => l.BaseAmount);
        foreach (var l in a.Lines)
        {
            var d = l.Driver;
            if (d is "OCC_ROOMS" or "ROOMS_REV" && a.Property.BaseOccupancy == 0)
                return $"Line {l.Code}: its driver {d} has a zero base year because the base occupancy is 0.";
            if (a.Lines.FirstOrDefault(x => x.Section == "REV" && x.Code == d) is { BaseAmount: 0 })
                return $"Line {l.Code}: its driver line {d} has a zero base amount.";
            if (d == "TOTAL_REV" && a.Property.BaseOccupancy == 0 && revenueBase == 0)
                return $"Line {l.Code}: its driver TOTAL_REV has a zero base year (base occupancy 0 and no revenue base amount).";
        }
        return null;
    }
}
