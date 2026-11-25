using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;

namespace HotelUnderwriting.PropertyTests;

public class InvariantProperties
{
    private const int Cases = 600;
    private const double RATE_BOUND = 0.4;
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private static readonly Assumptions A = Load();

    private static Assumptions Load()
    {
        if (!File.Exists(WorkbookPath)) throw new FileNotFoundException($"Reference workbook not found at {WorkbookPath}; check the data link in the csproj.");
        return WorkbookReader.ReadAssumptions(WorkbookPath);
    }

    private sealed record Case(int Index, string Description, ScenarioInputs Inputs, ScenarioResult? Result, Exception? Error);

    // Every case is generated and run once; each property test reads the same results.
    private static readonly Lazy<Case[]> All = new(() => Enumerable.Range(0, Cases).Select(i =>
    {
        var s = ScenarioGenerator.Generate(A, i);
        string d = ScenarioGenerator.Describe(i, s);
        try { return new Case(i, d, s, Underwriter.Run(A, s), null); }
        catch (Exception e) { return new Case(i, d, s, null, e); }
    }).ToArray());

    private static void ForEachCase(Func<ScenarioResult, string?> check)
    {
        var bad = All.Value
            .Select(c => (c, msg: c.Result == null ? "case threw (generator or engine bug): " + c.Error?.Message : check(c.Result)))
            .Where(x => x.msg != null)
            .Select(x => $"{x.msg} | {x.c.Description}").ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} of {Cases} cases failed:\n" + string.Join("\n", bad.Take(5)));
    }

    private static bool Finite(IEnumerable<double> xs) => xs.All(double.IsFinite);

    [Fact]
    public void Generator_never_produces_an_invalid_case()
    {
        var bad = All.Value.Where(c => c.Error != null).Select(c => $"case threw (generator or engine bug) {c.Error!.GetType().Name}: {c.Error.Message} | {c.Description}").ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} cases threw (generator or engine bug):\n" + string.Join("\n", bad.Take(5)));
    }

    // Independent recomputation of the close from the inputs (price, costs, loan sizing).
    private static (double Uses, double Funded, double FinCosts) Close(ScenarioInputs s)
    {
        double acqFee = s.AcquisitionFeePct * s.PurchasePrice;
        double pipTotal = s.PipPerKey * A.Property.Keys;
        double commitment = s.LoanToCost * (s.PurchasePrice + s.ClosingCosts + acqFee + pipTotal);
        double funded = commitment - Math.Min(pipTotal, commitment);
        double fin = s.OriginationFeePct * commitment + s.PlacementCosts;
        return (s.PurchasePrice + s.ClosingCosts + acqFee + fin, funded, fin);
    }

    [Fact]
    public void Loan_balance_after_exit_is_zero()
        => ForEachCase(r =>
        {
            var m = r.Monthly;
            for (int i = 1; i < m.ClosingBalance.Length; i++)
            {
                if (Math.Abs(m.ClosingBalance[i] - (m.OpeningBalance[i] + m.Draw[i] - m.Principal[i] - m.Payoff[i])) > 0.005) return $"roll-forward month {i}";
                if (Math.Abs(m.OpeningBalance[i] - m.ClosingBalance[i - 1]) > 0.005) return $"opening != prior closing, month {i}";
            }
            double fundedIn = Close(r.Inputs).Funded + m.Draw.Skip(1).Sum(), out_ = m.Principal.Sum() + m.Payoff.Sum();
            if (Math.Abs(fundedIn - out_) > 0.01) return $"loan in {fundedIn} vs out {out_}";
            int h = r.Inputs.HoldMonths;
            double worst = r.Monthly.ClosingBalance.Skip(h).Select(Math.Abs).Max();
            return Math.Abs(r.Summary.LoanBalanceAfterExit) <= 0.01 && worst <= 0.01 ? null : $"loan after exit {r.Summary.LoanBalanceAfterExit}, worst later closing balance {worst}";
        });

    [Fact]
    public void Lp_plus_gp_equals_levered_cash_flow_every_month()
        => ForEachCase(r =>
        {
            var m = r.Monthly;
            for (int i = 0; i < m.LeveredCf.Length; i++)
            {
                double e = Math.Abs(m.LpCf[i] + m.GpCf[i] - m.LeveredCf[i]);
                if (!(e < 1e-6)) return $"month {i}: |LP+GP-levered| = {e}";
            }
            return null;
        });

    [Fact]
    public void Lp_plus_gp_plus_contributions_equals_distributable_every_month()
        => ForEachCase(r =>
        {
            var m = r.Monthly;
            for (int i = 0; i < m.LeveredCf.Length; i++)
            {
                double e = Math.Abs(m.LpCf[i] + m.GpCf[i] + m.Contributions[i] - m.Distributable[i]);
                if (!(e < 1e-6)) return $"month {i}: |lp+gp+contrib-distributable| = {e}";
            }
            return null;
        });

    [Fact]
    public void Sources_equal_uses_at_close()
        => ForEachCase(r =>
        {
            var (uses, funded, _) = Close(r.Inputs);
            double equity = -r.Monthly.LeveredCf[0];
            if (Math.Abs(equity - (uses - funded)) > 1e-4) return $"equity at close {equity} vs uses - funded {uses - funded}";
            if (Math.Abs(r.Monthly.ClosingBalance[0] - funded) > 1e-4) return $"month 0 balance {r.Monthly.ClosingBalance[0]} vs funded {funded}";
            return Math.Abs(r.Summary.SourcesLessUses) < 1e-6 ? null : $"reported sources less uses = {r.Summary.SourcesLessUses}";
        });

    [Fact]
    public void Monthly_arrays_and_summary_numbers_are_finite()
        => ForEachCase(r =>
        {
            var arrays = typeof(MonthlySchedule).GetProperties().Where(p => p.PropertyType == typeof(double[])).ToList();
            var scalars = typeof(ReturnsSummary).GetProperties().Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(double?)).ToList();
            if (arrays.Count == 0 || scalars.Count == 0) return "reflection found no double[] / double members to check";
            foreach (var p in arrays)
                if (!Finite((double[])p.GetValue(r.Monthly)!)) return $"non-finite value in Monthly.{p.Name}";
            foreach (var p in scalars)
                if (p.GetValue(r.Summary) is double d && !double.IsFinite(d)) return $"non-finite Summary.{p.Name}";   // a null double? is skipped
            return null;
        });

    [Fact]
    public void Closing_balance_is_never_negative()
        => ForEachCase(r =>
        {
            double min = r.Monthly.ClosingBalance.Min();
            return min >= -0.01 ? null : $"closing balance {min}";
        });

    [Fact]
    public void Levered_irr_when_present_is_finite()
        => ForEachCase(r => r.Summary.LeveredIrr is null || double.IsFinite(r.Summary.LeveredIrr.Value) ? null : $"levered IRR {r.Summary.LeveredIrr}");

    [Fact]
    public void Levered_irr_is_present_in_most_cases()
    {
        var ok = All.Value.Where(c => c.Result != null).ToList();
        double rate = ok.Count(c => c.Result!.Summary.LeveredIrr.HasValue) / (double)Cases;
        Assert.True(rate >= RATE_BOUND, $"levered IRR present in {rate:P1} of cases");
    }

    [Fact]
    public void Generator_depends_on_its_seed()
    {
        var d = Enumerable.Range(0, 20).Select(i => ScenarioGenerator.Describe(i, ScenarioGenerator.Generate(A, i))).Select(x => x[x.IndexOf("base=", StringComparison.Ordinal)..]).Distinct().Count();
        Assert.True(d >= 15, $"only {d} distinct scenarios from 20 seeds");
    }

    [Fact]
    public void Same_seed_gives_identical_scenario_and_result()
    {
        foreach (int i in new[] { 0, 7, 123, Cases - 1 })
        {
            var s1 = ScenarioGenerator.Generate(A, i);
            var s2 = ScenarioGenerator.Generate(A, i);
            Assert.Equal(ScenarioGenerator.Describe(i, s1), ScenarioGenerator.Describe(i, s2));
            var r1 = Underwriter.Run(A, s1);
            var r2 = Underwriter.Run(A, s2);
            Assert.Equal(r1.Monthly.LeveredCf, r2.Monthly.LeveredCf);
            Assert.Equal(r1.Monthly.LpCf, r2.Monthly.LpCf);
            Assert.Equal(r1.Summary.LeveredIrr, r2.Summary.LeveredIrr);
            Assert.Equal(r1.Summary.LpIrr, r2.Summary.LpIrr);
        }
    }

    [Fact]
    public void Extremes_each_appear_at_least_once()
    {
        var s = All.Value.Select(c => c.Inputs).ToList();
        Assert.Contains(s, x => x.InterestRate == 0 && x.InterestOnlyMonths == 0);
        Assert.Contains(s, x => x.LoanToCost == 0);
        Assert.Contains(s, x => x.PipStartMonth + x.PipDurationMonths - 1 > x.HoldMonths);   // PIP spend would continue past exit
        Assert.Contains(s, x => x.DebtEvents.Any(e => e.Month == x.HoldMonths - 1));
        Assert.Contains(s, x => x.HoldMonths == 12);
        Assert.Contains(s, x => x.HoldMonths == 84);
        Assert.Contains(s, x => x.DebtEvents.Count > 0);
        Assert.Contains(s, x => x.HurdleTiers.Count > 0);
        Assert.Contains(s, x => x.DebtEvents.Any(e => e.Month == 1));
        Assert.Contains(s, x => x.InterestOnlyMonths > x.HoldMonths);
        Assert.Contains(s, x => x.GpShare == 0);
        Assert.Contains(s, x => x.Promote == 0);
    }
}
