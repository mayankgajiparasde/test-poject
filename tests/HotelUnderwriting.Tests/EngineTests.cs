using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

public class EngineTests
{
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(WorkbookPath);
    private static ScenarioInputs Scenario(string name) => A.Scenarios.First(s => s.Name == name);
    private static ScenarioResult Run(string name, Func<ScenarioInputs, ScenarioInputs>? tweak = null, WorkbookQuirks q = WorkbookQuirks.None)
        => Underwriter.Run(A, tweak == null ? Scenario(name) : tweak(Scenario(name)), q);

    // 1. Known result: values in the workbook that the three workbook errors do not touch.
    [Fact]
    public void Base_known_results_match_workbook()
    {
        var r = Run("Base");
        Assert.Equal(44_791_500, r.Summary.LoanCommitment, 2);
        Assert.Equal(39_751_500, r.Summary.InitialFundedLoan, 2);
        Assert.Equal(24_966_415, r.Summary.EquityAtClose, 2);
        Assert.Equal(16_298_777.88, r.Proforma.RoomsRevenue[r.Proforma.IndexOfYear(2028)], 2);   // leap year 2028
        Assert.Equal(6_147_003.93856772, r.Proforma.Noi[r.Proforma.IndexOfYear(2029)], 1);
        Assert.Equal(236_852.6875, r.Summary.Month1Interest, 2);                                  // 39,751,500 x 7.15% x 30/360
        Assert.Equal(305_344.002904508, r.Summary.FirstAmortizingPayment, 1);
        Assert.Equal(87_377_101.6419208, r.Summary.GrossSalePrice, 1);
        Assert.Equal(0.12307800160204, r.Summary.UnleveredIrr!.Value, 4);
    }

    // 2. The reconciliation has >= 15 values per scenario and every difference is explained by a named workbook error.
    [Fact]
    public void Reconciliation_every_difference_is_explained()
    {
        var rows = Reconciler.Compare(A, WorkbookReader.ReadCheckpoints(WorkbookPath, A.Scenarios.Select(s => s.Name)));
        foreach (var s in A.Scenarios) Assert.True(rows.Count(r => r.Scenario == s.Name) >= 15);
        Assert.DoesNotContain(rows, r => !r.Pass && r.Explanation.StartsWith("UNEXPLAINED"));
        Assert.Contains(rows, r => r.Pass);
    }

    // 2b. Switching the three workbook errors on reproduces the workbook's levered IRR in every scenario.
    [Theory]
    [InlineData("Base", WorkbookQuirks.PayoffOnOpeningBalance | WorkbookQuirks.SimplePrefAccrual, 0.183676510068513)]
    [InlineData("Upside", WorkbookQuirks.PayoffOnOpeningBalance | WorkbookQuirks.SimplePrefAccrual, 0.322786347158401)]
    [InlineData("Downside", WorkbookQuirks.PayoffOnOpeningBalance | WorkbookQuirks.FbExpenseOnRoomsRevenue, -0.0805642693260383)]
    public void Replicating_workbook_errors_reproduces_workbook_irr(string name, WorkbookQuirks q, double workbookIrr)
        => Assert.Equal(workbookIrr, Run(name, null, q).Summary.LeveredIrr!.Value, 4);

    // 3. Changed assumption: 36-month hold.
    [Fact]
    public void Hold_36_months_exits_in_month_36_and_loan_is_repaid()
    {
        var r = Run("Base", s => s with { HoldMonths = 36 });
        var m = r.Monthly;
        Assert.Equal(1, m.ExitFlag[36]);
        Assert.Equal(1, m.ExitFlag.Sum());
        Assert.Equal(m.ForwardNoi[36] / 0.08, m.GrossSalePrice[36], 2);
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
        Assert.All(Enumerable.Range(37, 60), i => Assert.Equal(0, m.LeveredCf[i]));              // nothing after exit
        Assert.Equal(m.PreExitBalance[36], m.Payoff[36], 2);                                      // payoff = END-of-month balance
    }

    // 4. Edge cases.
    [Fact]
    public void Interest_only_longer_than_hold_never_amortizes()
    {
        var r = Run("Base", s => s with { InterestOnlyMonths = 120, HoldMonths = 36 });
        Assert.All(r.Monthly.Principal, p => Assert.Equal(0, p, 6));
        Assert.Equal(0, r.Summary.FirstAmortizingPayment, 6);
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
    }

    [Fact]
    public void Zero_interest_rate_uses_straight_line_debt_constant()
    {
        var r = Run("Base", s => s with { InterestRate = 0, InterestOnlyMonths = 0 });
        Assert.All(r.Monthly.Interest, i => Assert.Equal(0, i, 6));
        Assert.Equal(1.0 / 30, r.Summary.DebtConstant, 12);
        Assert.True(r.Monthly.Principal[1] > 0);
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
    }

    [Fact]
    public void Pip_running_past_exit_is_cut_off_at_the_hold_end()
    {
        var r = Run("Base", s => s with { HoldMonths = 12, PipStartMonth = 8, PipDurationMonths = 12 });
        double spent = -r.Monthly.PipSpend.Sum();
        Assert.Equal(r.Summary.PipTotal / 12 * 5, spent, 2);                 // months 8..12 only
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
        Assert.True(r.Monthly.Draw.Skip(1).Sum() <= spent + 1e-6);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(85)]
    public void Hold_outside_12_to_84_is_rejected(int hold)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Run("Base", s => s with { HoldMonths = hold }));

    // 5. IRR solver.
    [Fact]
    public void Xirr_finds_a_negative_irr()
    {
        var d0 = new DateTime(2025, 1, 1);
        var irr = Returns.Xirr(new[] { -100.0, 90.0 }, new[] { d0, d0.AddDays(365) });
        Assert.Equal(-0.10, irr!.Value, 9);
    }

    [Fact]
    public void Xirr_matches_actual_over_365_convention()
    {
        var d0 = new DateTime(2020, 1, 1);                                   // 2020 is a leap year: 366 days
        var irr = Returns.Xirr(new[] { -1000.0, 1100.0 }, new[] { d0, d0.AddDays(366) });
        Assert.Equal(Math.Pow(1.1, 365.0 / 366) - 1, irr!.Value, 9);
    }

    [Fact]
    public void Xirr_returns_null_without_a_sign_change()
    {
        var d0 = new DateTime(2025, 1, 1);
        Assert.Null(Returns.Xirr(new[] { 10.0, 20.0, 30.0 }, new[] { d0, d0.AddDays(30), d0.AddDays(60) }));
        Assert.Null(Returns.Xirr(new[] { -10.0, -20.0 }, new[] { d0, d0.AddDays(30) }));
        Assert.Null(Returns.Multiple(new[] { 10.0, 20.0 }));
    }

    // 6. Waterfall invariant: LP + GP = levered CF in every month, every scenario, including changed assumptions.
    [Theory]
    [InlineData("Base", 60)]
    [InlineData("Upside", 48)]
    [InlineData("Downside", 84)]
    [InlineData("Base", 36)]
    [InlineData("Downside", 12)]
    public void Waterfall_lp_plus_gp_equals_levered_cash_flow(string name, int hold)
    {
        var r = Run(name, s => s with { HoldMonths = hold });
        for (int m = 0; m < r.Monthly.LeveredCf.Length; m++)
            Assert.Equal(r.Monthly.LeveredCf[m], r.Monthly.LpCf[m] + r.Monthly.GpCf[m], 6);
        Assert.Equal(0, r.Summary.WaterfallMaxAbsError, 6);
        Assert.Equal(0, r.Summary.SourcesLessUses, 6);
    }

    // 7. Spec behaviour passes the workbook's own control checks, which the workbook itself fails.
    [Theory]
    [InlineData("Base")]
    [InlineData("Upside")]
    [InlineData("Downside")]
    public void Spec_engine_loan_is_fully_repaid(string name)
        => Assert.Equal(0, Run(name).Summary.LoanBalanceAfterExit, 2);

    [Fact]
    public void Workbook_payoff_error_leaves_a_residual_balance_like_the_workbook_check()
        => Assert.Equal(-38_549.63, Run("Base", null, WorkbookQuirks.PayoffOnOpeningBalance).Summary.LoanBalanceAfterExit, 2);
}
