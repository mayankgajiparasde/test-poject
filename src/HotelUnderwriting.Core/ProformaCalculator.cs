namespace HotelUnderwriting.Core;

/// <summary>Annual operating forecast. Index 0 = base year, 1..n = forecast years.</summary>
public sealed class Proforma
{
    public required int[] Years { get; init; }
    public required double[] Days { get; init; }
    public required double[] InflationIndex { get; init; }
    public required double[] RoomsAvailable { get; init; }
    public required double[] Occupancy { get; init; }
    public required double[] OccupiedRooms { get; init; }
    public required double[] AdrGrowth { get; init; }
    public required double[] Adr { get; init; }
    public required double[] RoomsRevenue { get; init; }
    public required IReadOnlyList<(OperatingLine Line, double[] Amounts)> Lines { get; init; }
    public required double[] TotalRevenue { get; init; }
    public required double[] DepartmentalExpenses { get; init; }
    public required double[] DepartmentalProfit { get; init; }
    public required double[] FranchiseFees { get; init; }
    public required double[] UndistributedExpenses { get; init; }
    public required double[] Gop { get; init; }
    public required double[] ManagementFee { get; init; }
    public required double[] FfeReserve { get; init; }
    public required double[] FixedCharges { get; init; }
    public required double[] Noi { get; init; }

    public int IndexOfYear(int year) => Array.IndexOf(Years, year);
    public double[] Line(string code) => Lines.First(l => l.Line.Code == code).Amounts;
}

public static class ProformaCalculator
{
    private static readonly string[] Sections = { "REV", "DEPT", "UNDIST", "FIXED" };

    public static Proforma Compute(Assumptions a, ScenarioInputs s, WorkbookQuirks quirks = WorkbookQuirks.None)
    {
        var p = a.Property;
        int len = s.Years.Count + 1;
        if (s.Occupancy.Count != s.Years.Count || s.AdrGrowth.Count != s.Years.Count)
            throw new InvalidOperationException($"{s.Name}: occupancy / ADR growth vectors must match the year list.");

        var years = new int[len];
        years[0] = p.BaseYear;
        for (int i = 1; i < len; i++) years[i] = s.Years[i - 1];

        var days = years.Select(y => DateTime.IsLeapYear(y) ? 366.0 : 365.0).ToArray();
        var infl = years.Select(y => Math.Pow(1 + s.ExpenseInflation, y - p.BaseYear)).ToArray();
        var avail = days.Select(d => p.Keys * d).ToArray();
        var occ = new double[len];
        var growth = new double[len];
        var adr = new double[len];
        occ[0] = p.BaseOccupancy;
        adr[0] = p.BaseAdr;
        for (int i = 1; i < len; i++)
        {
            occ[i] = s.Occupancy[i - 1];
            growth[i] = s.AdrGrowth[i - 1];
            adr[i] = adr[i - 1] * (1 + growth[i]);
        }
        var occRooms = Enumerable.Range(0, len).Select(i => avail[i] * occ[i]).ToArray();
        var roomsRev = Enumerable.Range(0, len).Select(i => occRooms[i] * adr[i]).ToArray();

        // Driver series by code. Revenue lines are added as they are computed; TOTAL_REV once all REV lines exist.
        var drivers = new Dictionary<string, double[]> { ["OCC_ROOMS"] = occRooms, ["ROOMS_REV"] = roomsRev };
        var amounts = new List<(OperatingLine, double[])>();

        double[] Amount(OperatingLine l)
        {
            var r = new double[len];
            for (int i = 0; i < len; i++)
            {
                if (i == 0) { r[i] = l.BaseAmount; continue; }
                var code = l.Driver;
                if (l.Code == "FB_EXP" && years[i] >= 2029 && quirks.HasFlag(WorkbookQuirks.FbExpenseOnRoomsRevenue))
                    code = "ROOMS_REV";
                if (!drivers.TryGetValue(code, out var d))
                    throw new InvalidOperationException($"Line {l.Code}: unknown or not-yet-computed driver '{code}'.");
                r[i] = l.BaseAmount * (l.FixedPct * infl[i] * days[i] / 365 + (1 - l.FixedPct) * d[i] / d[0]);
            }
            return r;
        }

        foreach (var section in Sections)
        {
            foreach (var l in a.Lines.Where(x => x.Section == section))
            {
                var arr = Amount(l);
                amounts.Add((l, arr));
                if (section == "REV") drivers[l.Code] = arr;
            }
            if (section == "REV")
                drivers["TOTAL_REV"] = Enumerable.Range(0, len)
                    .Select(i => roomsRev[i] + amounts.Where(x => x.Item1.Section == "REV").Sum(x => x.Item2[i])).ToArray();
        }
        var total = drivers["TOTAL_REV"];

        double[] Sum(string section) => Enumerable.Range(0, len)
            .Select(i => amounts.Where(x => x.Item1.Section == section).Sum(x => x.Item2[i])).ToArray();
        double[] Pct(string code)
        {
            var item = a.PercentItems.FirstOrDefault(x => x.Code == code)
                ?? throw new InvalidOperationException($"Percent-of-revenue item '{code}' missing.");
            var basis = drivers[item.Base];
            return basis.Select(b => item.Pct * b).ToArray();
        }

        var dept = Sum("DEPT");
        var franchise = Pct("FRAN");
        var undist = Sum("UNDIST").Select((v, i) => v + franchise[i]).ToArray();
        var mgmt = Pct("MGMT");
        var ffe = Pct("FFE");
        var fixedCharges = Sum("FIXED").Select((v, i) => v + ffe[i]).ToArray();
        var gop = Enumerable.Range(0, len).Select(i => total[i] - dept[i] - undist[i]).ToArray();
        var noi = Enumerable.Range(0, len).Select(i => gop[i] - mgmt[i] - fixedCharges[i]).ToArray();

        return new Proforma
        {
            Years = years, Days = days, InflationIndex = infl, RoomsAvailable = avail, Occupancy = occ,
            OccupiedRooms = occRooms, AdrGrowth = growth, Adr = adr, RoomsRevenue = roomsRev, Lines = amounts,
            TotalRevenue = total, DepartmentalExpenses = dept,
            DepartmentalProfit = Enumerable.Range(0, len).Select(i => total[i] - dept[i]).ToArray(),
            FranchiseFees = franchise, UndistributedExpenses = undist, Gop = gop, ManagementFee = mgmt,
            FfeReserve = ffe, FixedCharges = fixedCharges, Noi = noi,
        };
    }
}
