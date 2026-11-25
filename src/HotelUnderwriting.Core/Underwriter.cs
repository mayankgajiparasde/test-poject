namespace HotelUnderwriting.Core;

/// <summary>Monthly schedule for one scenario. Index 0 = acquisition (month 0), 1..Horizon = months.</summary>
public sealed class MonthlySchedule
{
    public required DateTime[] Date { get; init; }
    public required int[] Month { get; init; }
    public required int[] DaysInMonth { get; init; }
    public required int[] InHold { get; init; }
    public required int[] ExitFlag { get; init; }
    public required int[] HoldYear { get; init; }
    public required double[] Seasonality { get; init; }
    public required double[] Revenue { get; init; }
    public required double[] NoiBeforeFee { get; init; }
    public required double[] ForwardNoi { get; init; }
    // Unlevered
    public required double[] PurchasePrice { get; init; }
    public required double[] ClosingCostsAndFee { get; init; }
    public required double[] Noi { get; init; }
    public required double[] AssetMgmtFee { get; init; }
    public required double[] PipSpend { get; init; }
    public required double[] GrossSalePrice { get; init; }
    public required double[] CostOfSale { get; init; }
    public required double[] DispositionFee { get; init; }
    public required double[] UnleveredCf { get; init; }
    // Debt
    public required double[] OpeningBalance { get; init; }
    public required double[] Draw { get; init; }
    public required double[] Interest { get; init; }
    public required double[] Payment { get; init; }
    public required double[] Principal { get; init; }
    public required double[] Payoff { get; init; }
    public required double[] ClosingBalance { get; init; }
    public required double[] PreExitBalance { get; init; }
    // Levered + waterfall
    public required double[] FinancingCosts { get; init; }
    public required double[] LeveredCf { get; init; }
    public required double[] Contributions { get; init; }
    public required double[] Distributable { get; init; }
    public required double[] PrefBop { get; init; }
    public required double[] Tier1 { get; init; }
    public required double[] Tier2 { get; init; }
    public required double[] PrefEop { get; init; }
    public required double[] LpCf { get; init; }
    public required double[] GpCf { get; init; }
}

public sealed class ReturnsSummary
{
    public double PipTotal { get; init; }
    public double AcquisitionFee { get; init; }
    public double LoanCommitment { get; init; }
    public double PipHoldback { get; init; }
    public double InitialFundedLoan { get; init; }
    /// <summary>At-close costs plus refinance fees (= -sum of the monthly FinancingCosts row).</summary>
    public double FinancingCostsTotal { get; init; }
    /// <summary>Describes the original loan; refinances not reflected.</summary>
    public double DebtConstant { get; init; }
    public double EquityAtClose { get; init; }
    public double ForwardNoiAtExit { get; init; }
    public double GrossSalePrice { get; init; }
    public double LoanPayoff { get; init; }
    public double NetSaleProceeds { get; init; }
    public double PeakLoanBalance { get; init; }
    /// <summary>Describes the original loan; refinances not reflected.</summary>
    public double Month1Interest { get; init; }
    /// <summary>First payment that includes principal (original or refinanced loan); 0 if no month has principal.</summary>
    public double FirstAmortizingPayment { get; init; }
    public double? UnleveredIrr { get; init; }
    public double? UnleveredMultiple { get; init; }
    public double? LeveredIrr { get; init; }
    public double? LeveredMultiple { get; init; }
    public double LeveredNetProfit { get; init; }
    public double? LpIrr { get; init; }
    public double? LpMultiple { get; init; }
    public double? GpIrr { get; init; }
    public double? GpMultiple { get; init; }
    // Controls (all should be 0)
    public double LoanBalanceAfterExit { get; init; }
    public double WaterfallMaxAbsError { get; init; }
    public double SourcesLessUses { get; init; }
}

public sealed class ScenarioResult
{
    public required ScenarioInputs Inputs { get; init; }
    public required Proforma Proforma { get; init; }
    public required MonthlySchedule Monthly { get; init; }
    public required ReturnsSummary Summary { get; init; }
}

public static class Underwriter
{
    public const int Horizon = 96;
    public const int MinHold = 12, MaxHold = 84;

    public static ScenarioResult Run(Assumptions a, ScenarioInputs s, WorkbookQuirks quirks = WorkbookQuirks.None)
    {
        Validate(a, s);
        var pf = ProformaCalculator.Compute(a, s, quirks);
        var p = a.Property;
        int n = Horizon, H = s.HoldMonths;
        T[] Arr<T>() => new T[n + 1];

        // ---- timeline ----
        var first = new DateTime(p.AcquisitionDate.Year, p.AcquisitionDate.Month, 1);
        var date = Arr<DateTime>(); var cal = Arr<int>(); var mon = Arr<int>(); var dim = Arr<int>();
        var inHold = Arr<int>(); var exit = Arr<int>(); var holdYear = Arr<int>(); var w = Arr<double>();
        var rev = Arr<double>(); var noiRaw = Arr<double>();
        date[0] = p.AcquisitionDate;
        for (int m = 1; m <= n; m++)
        {
            date[m] = first.AddMonths(m).AddDays(-1);            // EOMONTH(acq, m-1)
            cal[m] = date[m].Year; mon[m] = date[m].Month; dim[m] = date[m].Day;
            inHold[m] = m <= H ? 1 : 0; exit[m] = m == H ? 1 : 0; holdYear[m] = (m + 11) / 12;
            w[m] = a.Seasonality[mon[m] - 1];
            int yi = pf.IndexOfYear(cal[m]);
            if (yi < 1) throw new InvalidOperationException($"{s.Name}: calendar year {cal[m]} is outside the annual forecast.");
            rev[m] = pf.TotalRevenue[yi] * w[m];
            noiRaw[m] = pf.Noi[yi] * w[m];
        }
        // Forward 12-month NOI: months m+1..m+12 (truncated at the horizon; irrelevant for hold <= 84).
        var fwd = Arr<double>();
        for (int m = 0; m <= n; m++)
            for (int k = m + 1; k <= Math.Min(m + 12, n); k++) fwd[m] += noiRaw[k];

        // ---- capital ----
        double pipTotal = s.PipPerKey * p.Keys;
        double acqFee = s.AcquisitionFeePct * s.PurchasePrice;
        double commitment = s.LoanToCost * (s.PurchasePrice + s.ClosingCosts + acqFee + pipTotal);
        double holdback = Math.Min(pipTotal, commitment);
        double funded = commitment - holdback;
        double finCosts = s.OriginationFeePct * commitment + s.PlacementCosts;
        double debtConstant = AmortConstant(s.InterestRate, s.AmortizationYears);

        // ---- unlevered cash flow ----
        var price = Arr<double>(); var closing = Arr<double>(); var noi = Arr<double>(); var am = Arr<double>();
        var pip = Arr<double>(); var sale = Arr<double>(); var cos = Arr<double>(); var disp = Arr<double>(); var ucf = Arr<double>();
        price[0] = -s.PurchasePrice; closing[0] = -(s.ClosingCosts + acqFee);
        for (int m = 1; m <= n; m++)
        {
            noi[m] = noiRaw[m] * inHold[m];
            am[m] = -s.AssetMgmtFeePct * rev[m] * inHold[m];
            bool pipMonth = m >= s.PipStartMonth && m < s.PipStartMonth + s.PipDurationMonths;
            pip[m] = pipMonth && s.PipDurationMonths > 0 ? -pipTotal / s.PipDurationMonths * inHold[m] : 0;
            sale[m] = exit[m] * fwd[m] / s.ExitCapRate;
            cos[m] = -sale[m] * s.CostOfSalePct;
            disp[m] = -sale[m] * s.DispositionFeePct;
        }
        for (int m = 0; m <= n; m++) ucf[m] = price[m] + closing[m] + noi[m] + am[m] + pip[m] + sale[m] + cos[m] + disp[m];

        // ---- debt ----
        var open = Arr<double>(); var draw = Arr<double>(); var intr = Arr<double>(); var pay = Arr<double>();
        var prin = Arr<double>(); var payoff = Arr<double>(); var close = Arr<double>(); var pre = Arr<double>();
        var fin = Arr<double>();
        draw[0] = funded; close[0] = funded; pre[0] = funded;
        double drawn = funded;
        double rate = s.InterestRate, constant = debtConstant; int ioEnd = s.InterestOnlyMonths;   // current loan terms
        for (int m = 1; m <= n; m++)
        {
            open[m] = close[m - 1];
            draw[m] = Math.Min(-pip[m], commitment - drawn);
            drawn += draw[m];
            intr[m] = open[m] * rate * dim[m] / 360 * inHold[m];
            pay[m] = (m <= ioEnd ? intr[m] : constant * open[m] * dim[m] / 360) * inHold[m];
            prin[m] = pay[m] - intr[m];
            pre[m] = open[m] + draw[m] - prin[m];                      // balance at END of the month, before payoff
            payoff[m] = exit[m] * (quirks.HasFlag(WorkbookQuirks.PayoffOnOpeningBalance) ? open[m] : pre[m]);
            close[m] = pre[m] - payoff[m];
            foreach (var e in s.DebtEvents)
                if (e.Month == m && e is Refinance r)                  // new terms from month m+1; month m used the old ones
                {
                    fin[m] -= r.FeePct * pre[m];
                    rate = r.Rate;
                    ioEnd = (int)Math.Min((long)m + r.InterestOnlyMonths, int.MaxValue);   // saturates instead of overflowing int
                    constant = AmortConstant(r.Rate, r.AmortizationYears);
                }
        }

        // ---- levered cash flow & waterfall ----
        var lcf = Arr<double>(); var contrib = Arr<double>(); var dist = Arr<double>();
        var bop = Arr<double>(); var t1 = Arr<double>(); var t2 = Arr<double>(); var eop = Arr<double>();
        var lp = Arr<double>(); var gp = Arr<double>();
        fin[0] = -finCosts;
        double accrual = quirks.HasFlag(WorkbookQuirks.SimplePrefAccrual) ? 1 + s.PrefReturn / 12 : Math.Pow(1 + s.PrefReturn, 1.0 / 12);
        int hk = s.HurdleTiers.Count;
        var hAccrual = new double[hk]; var hEop = new double[hk]; var hBal = new double[hk];   // per hurdle: LP balance at its IRR
        for (int j = 0; j < hk; j++) hAccrual[j] = Math.Pow(1 + s.HurdleTiers[j].LpIrrHurdle, 1.0 / 12);
        for (int m = 0; m <= n; m++)
        {
            lcf[m] = ucf[m] + draw[m] + fin[m] - pay[m] - payoff[m];
            contrib[m] = Math.Max(-lcf[m], 0);
            dist[m] = Math.Max(lcf[m], 0);
            bop[m] = m == 0 ? 0 : eop[m - 1] * accrual;
            t1[m] = Math.Min(dist[m], bop[m] + contrib[m]);
            t2[m] = dist[m] - t1[m];
            eop[m] = bop[m] + contrib[m] - t1[m];
            // Split tier 2 across the hurdles: each slice is paid at the promote in force until the LP balance at that hurdle is cleared.
            double remaining = t2[m], promote = s.Promote, lpPaid = 0, promoteCash = 0, piece = 0;
            for (int j = 0; j < hk; j++)
            {
                hBal[j] = (m == 0 ? 0 : hEop[j] * hAccrual[j]) + (1 - s.GpShare) * contrib[m] - (1 - s.GpShare) * t1[m];
                double need = Math.Max(0, hBal[j] - lpPaid);
                double amt = Math.Min(remaining, need / ((1 - s.GpShare) * (1 - promote)));
                promoteCash += promote * amt;
                piece += (1 - promote) * amt;
                lpPaid += (1 - s.GpShare) * (1 - promote) * amt;
                remaining -= amt;
                promote = s.HurdleTiers[j].Promote;
            }
            promoteCash += promote * remaining;
            piece += (1 - promote) * remaining;
            double toEquity = t1[m] + piece;
            lp[m] = -(1 - s.GpShare) * contrib[m] + (1 - s.GpShare) * toEquity;
            gp[m] = -s.GpShare * contrib[m] + s.GpShare * toEquity + promoteCash;
            for (int j = 0; j < hk; j++) hEop[j] = Math.Max(0, hBal[j] - (1 - s.GpShare) * piece);   // hBal already net of the LP's tier-1 share
        }

        var monthly = new MonthlySchedule
        {
            Date = date, Month = Enumerable.Range(0, n + 1).ToArray(), DaysInMonth = dim, InHold = inHold, ExitFlag = exit,
            HoldYear = holdYear, Seasonality = w, Revenue = rev, NoiBeforeFee = noiRaw, ForwardNoi = fwd,
            PurchasePrice = price, ClosingCostsAndFee = closing, Noi = noi, AssetMgmtFee = am, PipSpend = pip,
            GrossSalePrice = sale, CostOfSale = cos, DispositionFee = disp, UnleveredCf = ucf,
            OpeningBalance = open, Draw = draw, Interest = intr, Payment = pay, Principal = prin, Payoff = payoff,
            ClosingBalance = close, PreExitBalance = pre, FinancingCosts = fin, LeveredCf = lcf, Contributions = contrib,
            Distributable = dist, PrefBop = bop, Tier1 = t1, Tier2 = t2, PrefEop = eop, LpCf = lp, GpCf = gp,
        };

        double equity = s.PurchasePrice + s.ClosingCosts + acqFee + finCosts - funded;
        int amortIdx = Array.FindIndex(prin, 1, x => x > 0);
        var summary = new ReturnsSummary
        {
            PipTotal = pipTotal, AcquisitionFee = acqFee, LoanCommitment = commitment, PipHoldback = holdback,
            InitialFundedLoan = funded, FinancingCostsTotal = -fin.Sum(), DebtConstant = debtConstant, EquityAtClose = equity,
            ForwardNoiAtExit = fwd[H], GrossSalePrice = sale.Sum(),
            LoanPayoff = payoff.Sum(), NetSaleProceeds = sale.Sum() + cos.Sum() + disp.Sum() - payoff.Sum(),
            PeakLoanBalance = Math.Max(open.Max(), pre.Max()),
            Month1Interest = intr[1], FirstAmortizingPayment = amortIdx >= 0 ? pay[amortIdx] : 0,
            UnleveredIrr = Returns.Xirr(ucf, date), UnleveredMultiple = Returns.Multiple(ucf),
            LeveredIrr = Returns.Xirr(lcf, date), LeveredMultiple = Returns.Multiple(lcf), LeveredNetProfit = lcf.Sum(),
            LpIrr = Returns.Xirr(lp, date), LpMultiple = Returns.Multiple(lp),
            GpIrr = Returns.Xirr(gp, date), GpMultiple = Returns.Multiple(gp),
            LoanBalanceAfterExit = close[H],
            WaterfallMaxAbsError = Enumerable.Range(0, n + 1).Max(m => Math.Abs(lp[m] + gp[m] - lcf[m])),
            SourcesLessUses = funded + equity - (s.PurchasePrice + s.ClosingCosts + acqFee + finCosts),
        };
        return new ScenarioResult { Inputs = s, Proforma = pf, Monthly = monthly, Summary = summary };
    }

    private static double AmortConstant(double rate, double years)
    {
        double d = 1 - Math.Pow(1 + rate, -years);                     // 0 for a tiny rate such as 1e-17
        return rate == 0 || d == 0 ? 1 / years : rate / d;
    }

    private static void Validate(Assumptions a, ScenarioInputs s)
    {
        if (s.HoldMonths < MinHold || s.HoldMonths > MaxHold)
            throw new ArgumentOutOfRangeException(nameof(s), $"{s.Name}: hold period {s.HoldMonths} is outside {MinHold}-{MaxHold} months.");
        if (a.Property.Keys <= 0) throw new ArgumentException("Keys must be positive.");
        if (s.ExitCapRate <= 0) throw new ArgumentException($"{s.Name}: exit cap rate must be positive.");
        if (s.AmortizationYears <= 0) throw new ArgumentException($"{s.Name}: amortization years must be positive.");
        if (a.Seasonality.Count != 12 || Math.Abs(a.Seasonality.Sum() - 1) > 1e-9)
            throw new ArgumentException("Seasonality needs 12 weights summing to 100%.");
        int prev = 0;
        for (int i = 0; i < s.DebtEvents.Count; i++)
        {
            var e = s.DebtEvents[i];
            string who = $"{s.Name}: DebtEvents[{i}] ({e.GetType().Name})";
            if (e.Month < 1 || e.Month >= s.HoldMonths)
                throw new ArgumentOutOfRangeException(nameof(s), $"{who}: month {e.Month} must be 1 to {s.HoldMonths - 1}.");
            if (e.Month <= prev) throw new ArgumentException($"{who}: months must be strictly increasing.");
            prev = e.Month;
            if (e is not Refinance r) throw new ArgumentException($"{who}: unsupported debt event.");
            if (!double.IsFinite(r.Rate) || !double.IsFinite(r.FeePct) || !double.IsFinite(r.AmortizationYears))
                throw new ArgumentOutOfRangeException(nameof(s), $"{who}: rate, fee and amortization years must be finite.");
            if (r.Rate < 0) throw new ArgumentOutOfRangeException(nameof(s), $"{who}: rate must not be negative.");
            if (r.FeePct < 0) throw new ArgumentOutOfRangeException(nameof(s), $"{who}: fee must not be negative.");
            if (r.AmortizationYears <= 0) throw new ArgumentOutOfRangeException(nameof(s), $"{who}: amortization years must be positive.");
            if (r.InterestOnlyMonths < 0) throw new ArgumentOutOfRangeException(nameof(s), $"{who}: interest-only months must not be negative.");
        }
        if (s.HurdleTiers.Count > 0 && !(s.GpShare >= 0 && s.GpShare < 1 && s.Promote >= 0 && s.Promote < 1))   // also rejects NaN/Infinity
            throw new ArgumentOutOfRangeException(nameof(s), $"{s.Name}: with HurdleTiers, GpShare and Promote must each be 0 to below 1.");
        double prevHurdle = s.PrefReturn;
        for (int i = 0; i < s.HurdleTiers.Count; i++)
        {
            var h = s.HurdleTiers[i];
            string who = $"{s.Name}: HurdleTiers[{i}]";
            if (!double.IsFinite(h.LpIrrHurdle) || !double.IsFinite(h.Promote))
                throw new ArgumentOutOfRangeException(nameof(s), $"{who}: hurdle and promote must be finite.");
            if (h.LpIrrHurdle <= prevHurdle)
                throw new ArgumentException($"{who}: hurdle {h.LpIrrHurdle} must be above {(i == 0 ? "the pref return" : "the previous hurdle")} ({prevHurdle}); hurdles must be strictly increasing.");
            if (h.Promote < 0 || h.Promote >= 1)
                throw new ArgumentOutOfRangeException(nameof(s), $"{who}: promote {h.Promote} must be 0 to below 1.");
            prevHurdle = h.LpIrrHurdle;
        }
    }
}
