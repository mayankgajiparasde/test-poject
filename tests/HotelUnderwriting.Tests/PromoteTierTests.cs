using HotelUnderwriting.Core;
using Xunit;

namespace HotelUnderwriting.Tests;

public class PromoteTierTests
{
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private static readonly Assumptions A = HotelUnderwriting.Excel.WorkbookReader.ReadAssumptions(WorkbookPath);
    private static ScenarioInputs Scenario(string name) => A.Scenarios.First(s => s.Name == name);

    private static HurdleTier Tier(double hurdle, double promote) => new(hurdle, promote);

    private static ScenarioResult Run(string name, params HurdleTier[] tiers)
        => Underwriter.Run(A, Scenario(name) with { HurdleTiers = tiers });

    private static void AssertConserved(ScenarioResult r)
    {
        var m = r.Monthly;
        Assert.Equal(0, r.Summary.WaterfallMaxAbsError, 6);
        for (int i = 0; i < m.LeveredCf.Length; i++)
        {
            Assert.Equal(m.LeveredCf[i], m.LpCf[i] + m.GpCf[i], 6);
            Assert.Equal(m.Distributable[i], m.LpCf[i] + m.GpCf[i] + m.Contributions[i], 6);   // distributions to LP + GP equal dist
        }
    }

    [Fact]
    public void No_tiers_leave_cash_flows_identical_and_pin_the_spec_irrs()
    {
        Assert.All(A.Scenarios, s => Assert.Empty(s.HurdleTiers));
        var expected = new Dictionary<string, double[]>
        {
            ["Base"] = new[] { 0.183860, 0.166089, 0.307736 },
            ["Upside"] = new[] { 0.323000, 0.285376, 0.565242 },
            ["Downside"] = new[] { -0.038510, -0.038510, -0.038510 },
        };
        foreach (var s in A.Scenarios)
        {
            var plain = Underwriter.Run(A, s);
            var empty = Underwriter.Run(A, s with { HurdleTiers = new List<HurdleTier>() });
            Assert.Equal(plain.Monthly.LpCf, empty.Monthly.LpCf);
            Assert.Equal(plain.Monthly.GpCf, empty.Monthly.GpCf);
            Assert.Equal(expected[s.Name][0], plain.Summary.LeveredIrr!.Value, 0.000006);
            Assert.Equal(expected[s.Name][1], plain.Summary.LpIrr!.Value, 0.000006);
            Assert.Equal(expected[s.Name][2], plain.Summary.GpIrr!.Value, 0.000006);
        }
    }

    [Fact]
    public void Reached_hurdle_lifts_gp_irr_and_lowers_lp_irr_but_lp_stays_near_the_hurdle()
    {
        var plain = Underwriter.Run(A, Scenario("Base")).Summary;
        var r = Run("Base", Tier(0.12, 0.30));
        Assert.True(r.Summary.GpIrr > plain.GpIrr);
        Assert.True(r.Summary.LpIrr < plain.LpIrr);
        Assert.True(r.Summary.LpIrr >= 0.12 - 0.002);
        Assert.Equal(plain.LeveredIrr!.Value, r.Summary.LeveredIrr!.Value, 12);
        AssertConserved(r);
    }

    [Fact]
    public void Hurdle_never_reached_changes_nothing()
    {
        var plain = Underwriter.Run(A, Scenario("Base")).Monthly;
        var r = Run("Base", Tier(0.40, 0.50));
        Assert.Equal(plain.LpCf, r.Monthly.LpCf);
        Assert.Equal(plain.GpCf, r.Monthly.GpCf);
    }

    [Fact]
    public void Two_hurdles_stack()
    {
        var first = Run("Base", Tier(0.10, 0.25));
        var both = Run("Base", Tier(0.10, 0.25), Tier(0.14, 0.35));
        Assert.True(both.Summary.GpIrr > first.Summary.GpIrr);
        Assert.True(both.Summary.LpIrr < first.Summary.LpIrr);
        Assert.True(both.Summary.LpIrr >= 0.14 - 0.0005);
        AssertConserved(both);
    }

    [Fact]
    public void Unreached_third_hurdle_changes_nothing()
    {
        var two = Run("Base", Tier(0.10, 0.25), Tier(0.13, 0.35));
        var three = Run("Base", Tier(0.10, 0.25), Tier(0.13, 0.35), Tier(0.16, 0.45));
        Assert.True(two.Summary.LpIrr < 0.16);
        Assert.Equal(two.Summary.GpIrr, three.Summary.GpIrr);
        AssertConserved(three);
    }

    // Plain-loop reference waterfall rebuilt from the schedule's Contributions, Tier1 and Tier2 (hurdle balances as in the conventions doc).
    // firstAbove = first month in which cash is left over after the last hurdle slice is paid (paid at the final tier's promote).
    private static (double[] Lp, double[] Gp, int FirstAbove) Reference(ScenarioResult r, params HurdleTier[] tiers)
    {
        var s = r.Inputs; var m = r.Monthly; double g = s.GpShare;
        int n = m.Contributions.Length;
        var lp = new double[n]; var gp = new double[n]; var eop = new double[tiers.Length]; int firstAbove = -1;
        for (int i = 0; i < n; i++)
        {
            double remaining = m.Tier2[i], p = s.Promote, lpPaid = 0, promoteCash = 0, pieces = 0;
            var bal = new double[tiers.Length];
            for (int j = 0; j < tiers.Length; j++)
            {
                bal[j] = (i == 0 ? 0 : eop[j] * Math.Pow(1 + tiers[j].LpIrrHurdle, 1.0 / 12)) + (1 - g) * m.Contributions[i] - (1 - g) * m.Tier1[i];
                double need = Math.Max(0, bal[j] - lpPaid);
                double amt = Math.Min(remaining, need / ((1 - g) * (1 - p)));
                promoteCash += p * amt; pieces += (1 - p) * amt; lpPaid += (1 - g) * (1 - p) * amt; remaining -= amt;
                p = tiers[j].Promote;
            }
            if (remaining > 0 && firstAbove < 0) firstAbove = i;
            promoteCash += p * remaining; pieces += (1 - p) * remaining;
            lp[i] = -(1 - g) * m.Contributions[i] + (1 - g) * (m.Tier1[i] + pieces);
            gp[i] = -g * m.Contributions[i] + g * (m.Tier1[i] + pieces) + promoteCash;
            for (int j = 0; j < tiers.Length; j++) eop[j] = Math.Max(0, bal[j] - (1 - g) * pieces);
        }
        return (lp, gp, firstAbove);
    }

    [Fact]
    public void Two_tier_cash_flows_match_an_independent_reference_waterfall()
    {
        var tiers = new[] { Tier(0.12, 0.30), Tier(0.15, 0.40) };
        var r = Run("Base", tiers);
        var (lp, gp, firstAbove) = Reference(r, tiers);
        Assert.True(firstAbove > 0);                                   // the last hurdle is actually passed on Base
        for (int i = 0; i < lp.Length; i++)
        {
            Assert.Equal(lp[i], r.Monthly.LpCf[i], 1e-6);
            Assert.Equal(gp[i], r.Monthly.GpCf[i], 1e-6);
        }
    }

    [Fact]
    public void The_promote_above_the_hurdle_only_matters_from_the_month_cash_passes_it()
    {
        var low = Run("Base", Tier(0.12, 0.30));
        var high = Run("Base", Tier(0.12, 0.99));
        int x = Reference(low, Tier(0.12, 0.30)).FirstAbove;
        Assert.True(x > 0);
        for (int i = 0; i < x; i++)                                    // everything paid so far is at the base promote
        {
            Assert.Equal(low.Monthly.LpCf[i], high.Monthly.LpCf[i]);
            Assert.Equal(low.Monthly.GpCf[i], high.Monthly.GpCf[i]);
        }
        Assert.NotEqual(low.Monthly.LpCf[x], high.Monthly.LpCf[x]);    // the slice above the hurdle is at the tier's promote
        Assert.NotEqual(low.Monthly.GpCf[x], high.Monthly.GpCf[x]);
        Assert.NotEqual(low.Monthly.LpCf, high.Monthly.LpCf);
    }

    [Fact]
    public void No_tiers_pin_todays_monthly_cash_flow_totals_and_keep_the_invariants()
    {
        // sumLP, sumGP, sumAbsLP, sumAbsGP over months 0..96, captured from the output workbook before the tier work
        var pins = new Dictionary<string, double[]>
        {
            ["Base"] = new[] { 21697559.74119427, 6034559.290800176, 66637106.74119427, 11027842.290800177 },
            ["Upside"] = new[] { 31973776.022941902, 10649026.412760068, 76913323.0229419, 15642309.412760068 },
            ["Downside"] = new[] { -5558170.290774063, -617574.4767526696, 46135129.70922594, 5126125.52324733 },
        };
        foreach (var sc in A.Scenarios)
        {
            var r = Underwriter.Run(A, sc);
            var e = pins[sc.Name];
            Assert.Equal(e[0], r.Monthly.LpCf.Sum(), 0.01);
            Assert.Equal(e[1], r.Monthly.GpCf.Sum(), 0.01);
            Assert.Equal(e[2], r.Monthly.LpCf.Sum(Math.Abs), 0.01);
            Assert.Equal(e[3], r.Monthly.GpCf.Sum(Math.Abs), 0.01);
            AssertConserved(r);
        }
        AssertConserved(Run("Upside", Tier(0.12, 0.30)));
    }

    [Fact]
    public void Zero_promote_tier_is_accepted_and_is_pro_rata_above_the_hurdle()
    {
        Assert.Equal(0.20, Scenario("Base").Promote);
        var plain = Underwriter.Run(A, Scenario("Base")).Summary;
        var zero = Run("Base", Tier(0.12, 0.0));
        var thirty = Run("Base", Tier(0.12, 0.30));
        Assert.True(zero.Summary.GpIrr < plain.GpIrr);
        Assert.True(zero.Summary.LpIrr > plain.LpIrr);
        Assert.True(zero.Summary.GpIrr < thirty.Summary.GpIrr);
        AssertConserved(zero);
        // a base Promote of 0 with a tier is valid too
        AssertConserved(Underwriter.Run(A, Scenario("Base") with { Promote = 0, HurdleTiers = new[] { Tier(0.12, 0.30) } }));
    }

    private static ScenarioInputs RefiBase() => Scenario("Base") with
    {
        DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.05, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 } },
    };

    [Fact]
    public void Mid_hold_contribution_feeds_the_hurdle_balance_like_the_reference()
    {
        var tier = Tier(0.12, 0.30);
        var r = Underwriter.Run(A, RefiBase() with { HurdleTiers = new[] { tier } });
        Assert.True(r.Monthly.Contributions.Skip(1).Max() > 0);          // the refinance fee is funded by equity in month 24
        var (lp, gp, _) = Reference(r, tier);
        for (int i = 0; i < lp.Length; i++)
        {
            Assert.Equal(lp[i], r.Monthly.LpCf[i], 1e-6);
            Assert.Equal(gp[i], r.Monthly.GpCf[i], 1e-6);
        }
        var near = Underwriter.Run(A, RefiBase() with { HurdleTiers = new[] { Tier(0.12, 0.99) } });
        Assert.InRange(near.Summary.LpIrr!.Value, 0.12 - 0.0005, 0.12 + 0.0035);
    }

    [Fact]
    public void Hurdle_after_a_refinance_keeps_the_invariants()
    {
        var refi = new Refinance { Month = 24, Rate = 0.05, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 };
        var s = Scenario("Base") with { DebtEvents = new DebtEvent[] { refi } };
        var plain = Underwriter.Run(A, s);
        var r = Underwriter.Run(A, s with { HurdleTiers = new[] { Tier(0.12, 0.30) } });
        Assert.True(r.Summary.GpIrr > plain.Summary.GpIrr);
        Assert.True(r.Summary.LpIrr < plain.Summary.LpIrr);
        AssertConserved(r);
    }

    // With a 99% promote above the hurdle the LP gets almost nothing past it, so its IRR sits just above the hurdle:
    // an independent check of the hurdle balance carry-forward.
    [Theory]
    [InlineData("Base", 0.10)]
    [InlineData("Base", 0.12)]
    [InlineData("Base", 0.14)]
    [InlineData("Upside", 0.10)]
    [InlineData("Upside", 0.12)]
    [InlineData("Upside", 0.14)]
    public void Near_total_promote_pins_the_lp_irr_to_the_hurdle(string name, double hurdle)
    {
        var r = Run(name, Tier(hurdle, 0.99));
        Assert.InRange(r.Summary.LpIrr!.Value, hurdle - 0.0005, hurdle + 0.0035);
        Assert.True(r.Summary.GpIrr > Underwriter.Run(A, Scenario(name)).Summary.GpIrr);
        AssertConserved(r);
    }

    [Fact]
    public void Near_total_promote_lp_irr_rises_with_the_hurdle()
    {
        var lp = new[] { 0.10, 0.12, 0.14 }.Select(h => Run("Base", Tier(h, 0.99)).Summary.LpIrr!.Value).ToArray();
        Assert.True(lp[0] < lp[1] && lp[1] < lp[2]);
    }

    [Fact]
    public void Three_reached_hurdles_lift_gp_irr_above_two()
    {
        var two = Run("Base", Tier(0.10, 0.25), Tier(0.12, 0.35));
        var three = Run("Base", Tier(0.10, 0.25), Tier(0.12, 0.35), Tier(0.14, 0.45));
        Assert.True(two.Summary.LpIrr > 0.14);                   // third hurdle is reachable here
        Assert.True(three.Summary.GpIrr > two.Summary.GpIrr);
        AssertConserved(three);
    }

    [Theory]
    [InlineData(1.0, 0.20)]                                      // GpShare of 1
    [InlineData(0.10, 1.0)]                                      // base Promote of 1
    [InlineData(double.NaN, 0.20)]
    [InlineData(-0.1, 0.20)]                                     // negative GpShare
    [InlineData(0.10, -0.1)]                                     // negative base Promote
    [InlineData(0.10, double.NaN)]
    public void Gp_share_or_base_promote_of_one_with_a_tier_is_rejected(double gpShare, double promote)
    {
        var s = Scenario("Base") with { GpShare = gpShare, Promote = promote, HurdleTiers = new[] { Tier(0.12, 0.30) } };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Underwriter.Run(A, s));
        Assert.Contains("with HurdleTiers, GpShare and Promote must each be 0 to below 1", ex.Message);
    }

    [Fact]
    public void Gp_share_of_one_without_tiers_still_runs_finite()
    {
        var r = Underwriter.Run(A, Scenario("Base") with { GpShare = 1 });
        Assert.All(r.Monthly.LpCf.Concat(r.Monthly.GpCf), x => Assert.True(double.IsFinite(x)));
    }

    [Fact]
    public void Downside_with_a_tier_keeps_the_invariants()
        => AssertConserved(Run("Downside", Tier(0.10, 0.30)));

    private static object[] Bad(Type type, string fragment, params HurdleTier[] tiers) => new object[] { tiers, type, fragment };
    private static readonly Type OutOfRange = typeof(ArgumentOutOfRangeException), Arg = typeof(ArgumentException);

    public static IEnumerable<object[]> InvalidTiers() => new[]
    {
        Bad(OutOfRange, "must be finite", Tier(double.NaN, 0.3)),
        Bad(OutOfRange, "must be finite", Tier(0.12, double.NaN)),
        Bad(OutOfRange, "must be finite", Tier(double.PositiveInfinity, 0.3)),
        Bad(Arg, "strictly increasing", Tier(Scenario("Base").PrefReturn, 0.3)),               // not above the pref
        Bad(Arg, "strictly increasing", Tier(Scenario("Base").PrefReturn - 0.01, 0.3)),
        Bad(Arg, "strictly increasing", Tier(0.12, 0.3), Tier(0.12, 0.4)),
        Bad(Arg, "strictly increasing", Tier(0.14, 0.3), Tier(0.12, 0.4)),
        Bad(OutOfRange, "0 to below 1", Tier(0.12, -0.1)),
        Bad(OutOfRange, "0 to below 1", Tier(0.12, 1.0)),
    };

    [Theory]
    [MemberData(nameof(InvalidTiers))]
    public void Invalid_tiers_are_rejected_naming_the_tier(HurdleTier[] tiers, Type type, string fragment)
    {
        var ex = Assert.Throws(type, () => { Run("Base", tiers); });
        Assert.Contains("HurdleTiers[", ex.Message);
        Assert.Contains(fragment, ex.Message);
    }
}
