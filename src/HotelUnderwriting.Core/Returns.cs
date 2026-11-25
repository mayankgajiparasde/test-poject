namespace HotelUnderwriting.Core;

public static class Returns
{
    /// <summary>
    /// XIRR convention: NPV = sum(cf / (1+r)^((date - date0)/365)). Scans for the first sign change
    /// from r = -99.99% upward, then bisects. Returns null when no root exists (e.g. no sign change).
    /// </summary>
    public static double? Xirr(IReadOnlyList<double> flows, IReadOnlyList<DateTime> dates)
    {
        if (flows.Count != dates.Count) throw new ArgumentException("flows and dates must have the same length.");
        if (!flows.Any(f => f > 0) || !flows.Any(f => f < 0)) return null;

        double Npv(double r)
        {
            double sum = 0;
            for (int i = 0; i < flows.Count; i++)
                sum += flows[i] / Math.Pow(1 + r, (dates[i] - dates[0]).TotalDays / 365.0);
            return sum;
        }

        double lo = -0.9999, flo = Npv(lo);
        // ponytail: geometric grid up to 1e6 (i.e. 100,000,000%), bisection inside the first bracket; add Newton polish if speed matters
        double step = 0.01;
        for (double hi = lo + step; hi < 1e6; hi += step, step *= 1.05)
        {
            double fhi = Npv(hi);
            if (flo == 0) return lo;
            if (Math.Sign(fhi) != Math.Sign(flo))
            {
                double a = lo, b = hi, fa = flo;
                for (int k = 0; k < 200; k++)
                {
                    double mid = (a + b) / 2, fm = Npv(mid);
                    if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; } else b = mid;
                }
                return (a + b) / 2;
            }
            lo = hi; flo = fhi;
        }
        return null;
    }

    /// <summary>Sum of positive flows / abs(sum of negative flows); null if there are no negative flows.</summary>
    public static double? Multiple(IEnumerable<double> flows)
    {
        double pos = 0, neg = 0;
        foreach (var f in flows) { if (f > 0) pos += f; else neg += f; }
        return neg == 0 ? null : pos / -neg;
    }
}
