using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

public class DebtEventTests
{
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(WorkbookPath);
    private static ScenarioInputs Scenario(string name) => A.Scenarios.First(s => s.Name == name);

    private static Refinance Refi(int month = 24, double rate = 0.05, int io = 12, double years = 30, double fee = 0.01)
        => new() { Month = month, Rate = rate, InterestOnlyMonths = io, AmortizationYears = years, FeePct = fee };

    private static ScenarioResult Run(string name, params DebtEvent[] events)
        => Underwriter.Run(A, Scenario(name) with { DebtEvents = events });

    private sealed record UnknownEvent : DebtEvent;

    private static double Constant(double rate, double years) => rate / (1 - Math.Pow(1 + rate, -years));

    private static void AssertRollForward(ScenarioResult r)
    {
        var m = r.Monthly;
        for (int i = 1; i < m.ClosingBalance.Length; i++)
            Assert.True(Math.Abs(m.ClosingBalance[i] - (m.OpeningBalance[i] + m.Draw[i] - m.Principal[i] - m.Payoff[i])) <= 0.005, $"roll-forward month {i}");
        double lhs = r.Summary.InitialFundedLoan + m.Draw.Skip(1).Sum(), rhs = m.Principal.Sum() + m.Payoff.Sum();
        Assert.True(Math.Abs(lhs - rhs) <= 0.01, $"loan in {lhs} vs out {rhs}");
    }

    private static void AssertConserved(ScenarioResult r)
    {
        AssertRollForward(r);
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
        Assert.Equal(0, r.Summary.WaterfallMaxAbsError, 6);
        Assert.Equal(0, r.Summary.SourcesLessUses, 6);
        for (int m = 0; m < r.Monthly.LeveredCf.Length; m++)
            Assert.Equal(r.Monthly.LeveredCf[m], r.Monthly.LpCf[m] + r.Monthly.GpCf[m], 6);
    }

    [Fact]
    public void No_events_reproduces_a_run_without_the_property()
    {
        foreach (var s in A.Scenarios)
        {
            var plain = Underwriter.Run(A, s);
            var empty = Underwriter.Run(A, s with { DebtEvents = new List<DebtEvent>() });
            Assert.Equal(plain.Summary.LeveredIrr, empty.Summary.LeveredIrr);
            Assert.Equal(plain.Summary.GrossSalePrice, empty.Summary.GrossSalePrice);
        }
        Assert.Equal(0.183860, Underwriter.Run(A, Scenario("Base")).Summary.LeveredIrr!.Value, 6);
    }

    [Fact]
    public void No_events_pin_todays_spec_values()
    {
        Assert.All(A.Scenarios, s => Assert.Empty(s.DebtEvents));
        Assert.Equal(0.183860, Underwriter.Run(A, Scenario("Base")).Summary.LeveredIrr!.Value, 0.00005);
        Assert.Equal(0.323000, Underwriter.Run(A, Scenario("Upside")).Summary.LeveredIrr!.Value, 0.00005);
        Assert.Equal(-0.038510, Underwriter.Run(A, Scenario("Downside")).Summary.LeveredIrr!.Value, 0.00005);
        var rows = Reconciler.Compare(A, WorkbookReader.ReadCheckpoints(WorkbookPath, A.Scenarios.Select(s => s.Name)));
        Assert.Equal(66, rows.Count);
        Assert.Equal(37, rows.Count(r => r.Pass));
        Assert.DoesNotContain(rows, r => r.Explanation.StartsWith("UNEXPLAINED"));
    }

    [Fact]
    public void Loan_roll_forward_and_financing_total_hold_with_zero_one_and_two_refinances()
    {
        foreach (var events in new[] { new DebtEvent[0], new DebtEvent[] { Refi() }, new DebtEvent[] { Refi(month: 18, rate: 0.06, io: 6), Refi(month: 36, io: 12, years: 25) } })
        {
            var r = Run("Base", events);
            AssertRollForward(r);
            Assert.Equal(-r.Monthly.FinancingCosts.Sum(), r.Summary.FinancingCostsTotal, 6);
        }
    }

    [Fact]
    public void Refinance_mid_hold_follows_the_convention()
    {
        var s = Scenario("Base");
        var r = Run("Base", Refi());
        var m = r.Monthly;
        var plain = Underwriter.Run(A, s).Monthly;

        Assert.Equal(plain.Interest[24], m.Interest[24], 6);                                // month R uses the old terms
        Assert.Equal(-0.01 * m.PreExitBalance[24], m.FinancingCosts[24], 6);                // fee on balance after draw and principal
        Assert.Equal(m.PreExitBalance[24], m.ClosingBalance[24], 6);                        // no cash out, balance unchanged
        Assert.Equal(m.OpeningBalance[25] * 0.05 * m.DaysInMonth[25] / 360, m.Interest[25], 6);
        for (int i = 25; i <= 36; i++) Assert.Equal(0, m.Principal[i], 6);                 // interest-only 25..36
        double constant = 0.05 / (1 - Math.Pow(1.05, -30));
        Assert.Equal(constant * m.OpeningBalance[37] * m.DaysInMonth[37] / 360, m.Payment[37], 6);
        Assert.True(m.Principal[37] > 0);
        Assert.Equal(m.PreExitBalance[s.HoldMonths], m.Payoff[s.HoldMonths], 6);
        AssertConserved(r);
    }

    [Fact]
    public void Refinance_to_a_lower_rate_raises_levered_irr()
    {
        Assert.True(Scenario("Base").InterestRate > 0.05);
        Assert.True(Run("Base", Refi()).Summary.LeveredIrr > Underwriter.Run(A, Scenario("Base")).Summary.LeveredIrr);
    }

    [Fact]
    public void Refinance_to_a_higher_rate_with_a_fee_lowers_levered_irr()
        => Assert.True(Run("Base", Refi(rate: 0.09, fee: 0.02)).Summary.LeveredIrr < Underwriter.Run(A, Scenario("Base")).Summary.LeveredIrr);

    [Fact]
    public void Downside_refinance_keeps_the_invariants()
    {
        var r = Run("Downside", Refi(month: 24, rate: 0.06, io: 12, years: 30, fee: 0.01));
        Assert.True(r.Summary.LeveredIrr < 0);
        Assert.Equal(-r.Monthly.FinancingCosts.Sum(), r.Summary.FinancingCostsTotal, 6);
        AssertConserved(r);
    }

    [Fact]
    public void Overlapping_interest_only_windows_use_the_latest_refinance()
    {
        var m = Run("Base", Refi(month: 18, io: 12), Refi(month: 24, rate: 0.06, io: 6, years: 30)).Monthly;
        for (int i = 19; i <= 30; i++) Assert.Equal(0, m.Principal[i], 6);
        Assert.Equal(Constant(0.06, 30) * m.OpeningBalance[31] * m.DaysInMonth[31] / 360, m.Payment[31], 6);
    }

    [Fact]
    public void First_amortizing_payment_is_the_first_payment_with_principal()
    {
        foreach (var s in A.Scenarios)
        {
            var plain = Underwriter.Run(A, s);
            Assert.Equal(plain.Monthly.Payment[s.InterestOnlyMonths + 1], plain.Summary.FirstAmortizingPayment);
        }
        var r = Run("Base", Refi());                                                               // refinance at 24, IO 12: first principal in month 37
        Assert.Equal(r.Monthly.Payment[37], r.Summary.FirstAmortizingPayment);
        var r12 = Run("Base", Refi(month: 12, io: 0));
        Assert.Equal(r12.Monthly.Payment[13], r12.Summary.FirstAmortizingPayment);
        Assert.Equal(0, Underwriter.Run(A, Scenario("Base") with { InterestOnlyMonths = 120, HoldMonths = 36 }).Summary.FirstAmortizingPayment, 6);
    }

    [Fact]
    public void Tiny_positive_refinance_rate_gives_finite_payments()
    {
        var r = Run("Base", Refi(rate: 1e-17, io: 0));
        Assert.All(r.Monthly.Payment, p => Assert.True(double.IsFinite(p)));
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
    }

    [Fact]
    public void Refinance_at_rate_zero_has_no_interest_and_straight_line_constant()
    {
        var m = Run("Base", Refi(rate: 0, io: 0)).Monthly;
        for (int i = 25; i <= 60; i++) Assert.Equal(0, m.Interest[i], 6);
        Assert.Equal(m.OpeningBalance[25] / 30 * m.DaysInMonth[25] / 360, m.Principal[25], 6);
        AssertConserved(Run("Base", Refi(rate: 0, io: 0)));
    }

    [Fact]
    public void Two_refinances_apply_in_order()
    {
        var r = Run("Base", Refi(month: 18, rate: 0.06, io: 6, years: 30), Refi(month: 36, rate: 0.05, io: 12, years: 25));
        var m = r.Monthly;
        Assert.Equal(m.OpeningBalance[19] * 0.06 * m.DaysInMonth[19] / 360, m.Interest[19], 6);
        Assert.Equal(m.OpeningBalance[36] * 0.06 * m.DaysInMonth[36] / 360, m.Interest[36], 6);   // month 36 still old terms
        Assert.Equal(m.OpeningBalance[37] * 0.05 * m.DaysInMonth[37] / 360, m.Interest[37], 6);
        for (int i = 19; i <= 24; i++) Assert.Equal(0, m.Principal[i], 6);                        // first IO window
        for (int i = 37; i <= 48; i++) Assert.Equal(0, m.Principal[i], 6);                        // second IO window
        Assert.Equal(Constant(0.06, 30) * m.OpeningBalance[25] * m.DaysInMonth[25] / 360, m.Payment[25], 6);
        Assert.Equal(Constant(0.05, 25) * m.OpeningBalance[49] * m.DaysInMonth[49] / 360, m.Payment[49], 6);
        Assert.True(m.FinancingCosts[18] < 0 && m.FinancingCosts[36] < 0);
        Assert.Equal(-r.Monthly.FinancingCosts.Sum(), r.Summary.FinancingCostsTotal, 6);
        AssertConserved(r);
    }

    [Fact]
    public void Financing_total_includes_the_refinance_fee()
    {
        var plain = Underwriter.Run(A, Scenario("Base"));
        Assert.Equal(-plain.Monthly.FinancingCosts.Sum(), plain.Summary.FinancingCostsTotal, 6);
        var r = Run("Base", Refi());
        Assert.Equal(-r.Monthly.FinancingCosts.Sum(), r.Summary.FinancingCostsTotal, 6);
        Assert.Equal(plain.Summary.FinancingCostsTotal - r.Monthly.FinancingCosts[24], r.Summary.FinancingCostsTotal, 6);
    }

    [Fact]
    public void Refinance_while_pip_is_still_drawing_keeps_drawing()
    {
        var r = Run("Base", Refi(month: 6));
        Assert.True(r.Monthly.Draw.Skip(7).Sum() > 0);
        Assert.Equal(r.Summary.PipTotal, r.Monthly.Draw.Skip(1).Sum(), 2);
        AssertConserved(r);
    }

    [Theory]
    [InlineData("Base", 1)]
    [InlineData("Base", 59)]
    [InlineData("Upside", 18)]
    public void Refinance_is_accepted_at_boundaries_and_on_other_scenarios(string name, int month)
        => AssertConserved(Run(name, Refi(month: month, rate: 0.05, io: 6, years: 30, fee: 0.01)));

    [Fact]
    public void Huge_interest_only_months_do_not_overflow_and_never_amortize()
    {
        var r = Run("Base", Refi(io: int.MaxValue));
        for (int i = 25; i < r.Monthly.Principal.Length; i++) Assert.Equal(0, r.Monthly.Principal[i], 6);
        Assert.Equal(0, r.Summary.LoanBalanceAfterExit, 2);
    }

    private static object[] Bad(Type type, string fragment, params DebtEvent[] events) => new object[] { events, type, fragment };
    private static readonly Type OutOfRange = typeof(ArgumentOutOfRangeException), Arg = typeof(ArgumentException);

    public static IEnumerable<object[]> InvalidEvents() => new[]
    {
        Bad(OutOfRange, "must be 1 to", Refi(month: 0)),
        Bad(OutOfRange, "must be 1 to", Refi(month: 60)),                                         // Base hold is 60
        Bad(Arg, "strictly increasing", Refi(month: 36), Refi(month: 36)),
        Bad(Arg, "strictly increasing", Refi(month: 36), Refi(month: 24)),
        Bad(OutOfRange, "rate must not be negative", Refi(rate: -0.01)),
        Bad(OutOfRange, "fee must not be negative", Refi(fee: -0.01)),
        Bad(OutOfRange, "amortization years must be positive", Refi(years: 0)),
        Bad(OutOfRange, "interest-only months must not be negative", Refi(io: -1)),
        Bad(OutOfRange, "must be finite", Refi(rate: double.NaN)),
        Bad(OutOfRange, "must be finite", Refi(fee: double.PositiveInfinity)),
        Bad(OutOfRange, "must be finite", Refi(years: double.NaN)),
        Bad(Arg, "unsupported", new UnknownEvent { Month = 12 }),
    };

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void Invalid_events_are_rejected_naming_the_event(DebtEvent[] events, Type type, string fragment)
    {
        Assert.Equal(60, Scenario("Base").HoldMonths);
        var ex = Assert.Throws(type, () => { Run("Base", events); });
        Assert.Contains("DebtEvents[", ex.Message);
        Assert.Contains(fragment, ex.Message);
    }
}
