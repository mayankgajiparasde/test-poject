namespace HotelUnderwriting.Core;

/// <summary>A reference value per scenario taken from the workbook's Reconciliation sheet.</summary>
public sealed record Checkpoint(string Label, IReadOnlyDictionary<string, double> ByScenario);

public sealed record ReconRow(string Scenario, string Label, double Workbook, double Engine, double Difference, double Tolerance, bool Pass, string Explanation);

public sealed record IssueImpact(string Scenario, WorkbookQuirks Quirk, string Description, ReturnsSummary Spec, ReturnsSummary WithError)
{
    public double SpecLeveredIrr => Spec.LeveredIrr ?? double.NaN;
    public double WithErrorLeveredIrr => WithError.LeveredIrr ?? double.NaN;
    public double LeveredImpactPp => (WithErrorLeveredIrr - SpecLeveredIrr) * 100;
    public double LpImpactPp => ((WithError.LpIrr ?? double.NaN) - (Spec.LpIrr ?? double.NaN)) * 100;
    public double GpImpactPp => ((WithError.GpIrr ?? double.NaN) - (Spec.GpIrr ?? double.NaN)) * 100;
}

public static class Reconciler
{
    public const double CurrencyTol = 1.0, RateTol = 0.0001, MultipleTol = 0.001;

    public static readonly IReadOnlyDictionary<WorkbookQuirks, string> IssueNames = new Dictionary<WorkbookQuirks, string>
    {
        [WorkbookQuirks.PayoffOnOpeningBalance] = "#1 Payoff uses the exit month's opening balance (Monthly CF row 73, D73:CU73)",
        [WorkbookQuirks.SimplePrefAccrual] = "#2 Pref accrues at 1 + pref/12 (Monthly CF B37, used by row 87)",
        [WorkbookQuirks.FbExpenseOnRoomsRevenue] = "#3 F&B expense driven by rooms revenue 2029+ (Proforma - Downside E26:L26)",
    };

    private static readonly WorkbookQuirks[] AllQuirks =
        { WorkbookQuirks.PayoffOnOpeningBalance, WorkbookQuirks.SimplePrefAccrual, WorkbookQuirks.FbExpenseOnRoomsRevenue };

    // Formula audit: #3 exists only on the Downside proforma sheet (Base and Upside use the correct driver row).
    private static bool Applies(string scenario, WorkbookQuirks q) => q != WorkbookQuirks.FbExpenseOnRoomsRevenue || scenario == "Downside";

    private delegate double? Selector(ScenarioResult r);

    // Label on the workbook's Reconciliation sheet -> (engine value, tolerance)
    private static readonly Dictionary<string, (Selector Get, double Tol)> Map = new()
    {
        ["Loan commitment"] = (r => r.Summary.LoanCommitment, CurrencyTol),
        ["Initial funded loan (after PIP holdback)"] = (r => r.Summary.InitialFundedLoan, CurrencyTol),
        ["Equity required at close"] = (r => r.Summary.EquityAtClose, CurrencyTol),
        ["2028 rooms revenue"] = (r => r.Proforma.RoomsRevenue[r.Proforma.IndexOfYear(2028)], CurrencyTol),
        ["2028 total revenue"] = (r => r.Proforma.TotalRevenue[r.Proforma.IndexOfYear(2028)], CurrencyTol),
        ["2029 total departmental expenses"] = (r => r.Proforma.DepartmentalExpenses[r.Proforma.IndexOfYear(2029)], CurrencyTol),
        ["2029 total undistributed expenses"] = (r => r.Proforma.UndistributedExpenses[r.Proforma.IndexOfYear(2029)], CurrencyTol),
        ["2029 NOI"] = (r => r.Proforma.Noi[r.Proforma.IndexOfYear(2029)], CurrencyTol),
        ["Month 1 interest"] = (r => r.Summary.Month1Interest, CurrencyTol),
        ["First amortizing payment"] = (r => r.Summary.FirstAmortizingPayment, CurrencyTol),
        ["Peak loan balance"] = (r => r.Summary.PeakLoanBalance, CurrencyTol),
        ["Forward 12-month NOI at exit"] = (r => r.Summary.ForwardNoiAtExit, CurrencyTol),
        ["Gross sale price"] = (r => r.Summary.GrossSalePrice, CurrencyTol),
        ["Loan payoff at exit"] = (r => r.Summary.LoanPayoff, CurrencyTol),
        ["Net sale proceeds to equity"] = (r => r.Summary.NetSaleProceeds, CurrencyTol),
        ["Unlevered IRR"] = (r => r.Summary.UnleveredIrr, RateTol),
        ["Levered IRR"] = (r => r.Summary.LeveredIrr, RateTol),
        ["Levered equity multiple"] = (r => r.Summary.LeveredMultiple, MultipleTol),
        ["LP IRR"] = (r => r.Summary.LpIrr, RateTol),
        ["GP IRR"] = (r => r.Summary.GpIrr, RateTol),
        ["GP multiple"] = (r => r.Summary.GpMultiple, MultipleTol),
        ["Check: loan balance after exit (must be 0)"] = (r => r.Summary.LoanBalanceAfterExit, CurrencyTol),
    };

    public static IReadOnlyList<ReconRow> Compare(Assumptions a, IReadOnlyList<Checkpoint> checkpoints)
    {
        var rows = new List<ReconRow>();
        foreach (var s in a.Scenarios)
        {
            var spec = Underwriter.Run(a, s);
            var replicas = new Dictionary<WorkbookQuirks, ScenarioResult>();
            ScenarioResult Replica(WorkbookQuirks q) =>
                replicas.TryGetValue(q, out var r) ? r : replicas[q] = Underwriter.Run(a, s, q);

            foreach (var cp in checkpoints)
            {
                if (!Map.TryGetValue(cp.Label, out var m)) throw new InvalidOperationException($"No engine mapping for checkpoint '{cp.Label}'.");
                if (!cp.ByScenario.TryGetValue(s.Name, out var wb)) continue;
                var eng = m.Get(spec) ?? double.NaN;
                bool pass = Math.Abs(eng - wb) <= m.Tol;
                string why = pass ? "Within tolerance." : Attribute(s.Name, Replica, wb, m);
                rows.Add(new ReconRow(s.Name, cp.Label, wb, eng, eng - wb, m.Tol, pass, why));
            }
        }
        return rows;
    }

    // Smallest set of workbook errors that, when switched on, makes the engine hit the workbook's number.
    private static string Attribute(string scenario, Func<WorkbookQuirks, ScenarioResult> replica, double wb, (Selector Get, double Tol) m)
    {
        var applicable = AllQuirks.Where(q => Applies(scenario, q)).ToArray();
        var subsets = Enumerable.Range(1, (1 << applicable.Length) - 1)
            .Select(mask => applicable.Where((_, i) => (mask & (1 << i)) != 0).ToArray())
            .OrderBy(x => x.Length);
        foreach (var set in subsets)
        {
            var q = set.Aggregate(WorkbookQuirks.None, (acc, x) => acc | x);
            var v = m.Get(replica(q));
            if (v.HasValue && Math.Abs(v.Value - wb) <= m.Tol)
                return "Workbook error: reproduced only by switching on " + string.Join("; ", set.Select(x => IssueNames[x]));
        }
        return "UNEXPLAINED: not reproduced by any known workbook error. Investigate.";
    }

    /// <summary>Return effect of each workbook error on its own, plus all applicable ones together.</summary>
    public static IReadOnlyList<IssueImpact> IrrImpacts(Assumptions a)
    {
        var list = new List<IssueImpact>();
        foreach (var s in a.Scenarios)
        {
            var spec = Underwriter.Run(a, s).Summary;
            var applicable = AllQuirks.Where(q => Applies(s.Name, q)).ToArray();
            foreach (var q in applicable)
                list.Add(new IssueImpact(s.Name, q, IssueNames[q], spec, Underwriter.Run(a, s, q).Summary));
            var all = applicable.Aggregate(WorkbookQuirks.None, (acc, x) => acc | x);
            list.Add(new IssueImpact(s.Name, all, "All errors together (replicates the workbook)", spec, Underwriter.Run(a, s, all).Summary));
        }
        return list;
    }
}
