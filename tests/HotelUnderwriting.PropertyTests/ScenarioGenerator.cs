using System.Globalization;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.PropertyTests;

/// <summary>Seeded random valid scenarios: case i uses seed BaseSeed + i, so any failure can be regenerated from its seed.</summary>
public static class ScenarioGenerator
{
    public const int BaseSeed = 20261124;

    public static int SeedFor(int caseIndex) => BaseSeed + caseIndex;

    public static ScenarioInputs Generate(Assumptions a, int caseIndex)
    {
        var rng = new Random(SeedFor(caseIndex));
        double U(double lo, double hi) => lo + rng.NextDouble() * (hi - lo);
        bool Chance(double p) => rng.NextDouble() < p;

        var s = a.Scenarios[rng.Next(a.Scenarios.Count)];
        int hold = Chance(0.1) ? 12 : Chance(0.1) ? 84 : rng.Next(12, 85);

        var events = new List<DebtEvent>();
        if (Chance(0.5))
        {
            var months = new SortedSet<int>();
            if (Chance(0.25)) months.Add(hold - 1);
            if (Chance(0.15)) months.Add(1);
            int want = rng.Next(1, 3);
            while (months.Count < want) months.Add(rng.Next(1, hold));
            foreach (var m in months)
                events.Add(new Refinance
                {
                    Month = m,
                    Rate = Chance(0.1) ? 0 : U(0, 0.15),
                    InterestOnlyMonths = rng.Next(0, 25),
                    AmortizationYears = rng.Next(1, 41),
                    FeePct = U(0, 0.03),
                });
        }

        double pref = U(0, 0.12);
        var tiers = new List<HurdleTier>();
        if (Chance(0.5))
        {
            double h = pref;
            for (int k = rng.Next(0, 3); k > 0; k--)
            {
                h += 0.005 + U(0, 0.1);
                if (h >= 0.5) break;
                tiers.Add(new HurdleTier(h, U(0, 0.6)));
            }
        }

        return s with
        {
            HoldMonths = hold,
            InterestRate = Chance(0.15) ? 0 : U(0, 0.15),
            InterestOnlyMonths = Chance(0.15) ? 0 : Chance(0.15) ? rng.Next(hold + 1, 121) : rng.Next(0, 121),   // the middle branch makes IO longer than the hold
            AmortizationYears = rng.Next(1, 41),
            LoanToCost = Chance(0.1) ? 0 : U(0, 0.8),
            PipPerKey = U(0, 20000),
            PipStartMonth = rng.Next(1, hold + 13),
            PipDurationMonths = rng.Next(1, 25),
            ExitCapRate = U(0.05, 0.12),
            PrefReturn = pref,
            GpShare = Chance(0.1) ? 0 : U(0, 0.5),
            Promote = Chance(0.1) ? 0 : U(0, 0.5),
            PurchasePrice = U(20_000_000, 200_000_000),
            ClosingCosts = U(0, 5_000_000),
            AcquisitionFeePct = U(0, 0.03),
            ExpenseInflation = U(0, 0.06),
            OriginationFeePct = U(0, 0.02),
            PlacementCosts = U(0, 1_000_000),
            DebtEvents = events,
            HurdleTiers = tiers,
        };
    }

    /// <summary>One-line description with round-trip precision, enough to rebuild the case by hand and to compare two generations.</summary>
    public static string Describe(int caseIndex, ScenarioInputs s)
    {
        string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        string refis = string.Join(";", s.DebtEvents.OfType<Refinance>().Select(r =>
            $"m{r.Month}/rate{F(r.Rate)}/io{r.InterestOnlyMonths}/yrs{F(r.AmortizationYears)}/fee{F(r.FeePct)}"));
        string tiers = string.Join(";", s.HurdleTiers.Select(t => $"{F(t.LpIrrHurdle)}->{F(t.Promote)}"));
        return $"case {caseIndex} seed {SeedFor(caseIndex)} base={s.Name} price={F(s.PurchasePrice)} closing={F(s.ClosingCosts)} acqFee={F(s.AcquisitionFeePct)} infl={F(s.ExpenseInflation)} origFee={F(s.OriginationFeePct)} placement={F(s.PlacementCosts)} hold={s.HoldMonths} rate={F(s.InterestRate)} io={s.InterestOnlyMonths} " +
               $"amort={F(s.AmortizationYears)} ltc={F(s.LoanToCost)} pip/key={F(s.PipPerKey)} pipStart={s.PipStartMonth} pipDur={s.PipDurationMonths} " +
               $"cap={F(s.ExitCapRate)} pref={F(s.PrefReturn)} gp={F(s.GpShare)} promote={F(s.Promote)} refis=[{refis}] tiers=[{tiers}]";
    }
}
