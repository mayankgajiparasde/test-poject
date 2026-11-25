using System.Globalization;
using ClosedXML.Excel;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Excel;

/// <summary>
/// Reads assumptions (constants only) from 'Inputs' and 'Operating Inputs', and reference checkpoints from
/// 'Reconciliation'. Everything is located by its label, not by a fixed cell address. The workbook is opened
/// read-only in memory and never saved. Formulas are never evaluated: a cell that holds one is read from the value
/// cached in the file. Anything missing or of the wrong kind is a <see cref="WorkbookException"/>.
/// </summary>
public static class WorkbookReader
{
    private static readonly string[] Sections = ["REV", "DEPT", "UNDIST", "FIXED"]; // the ones the engine adds up

    public static XLWorkbook Open(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new XLWorkbook(fs);
    }

    public static Assumptions ReadAssumptions(string path)
    {
        using var wb = Open(path);
        return Read(wb);
    }

    public static Assumptions ReadAssumptions(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        return Read(wb);
    }

    private static Assumptions Read(XLWorkbook wb)
    {
        var inputs = Sheet(wb, "Inputs");
        var ops = Sheet(wb, "Operating Inputs");

        IXLCell At(string label) => inputs.Cell(FindRow(inputs, label), 2);

        var property = new PropertyInputs(
            Str(At("Property")), Int(At("Keys")), Date(At("Acquisition date")),
            Int(At("Base year (actuals)")), Num(At("Base-year occupancy")), Num(At("Base-year ADR ($)")));

        // Scenario scalar table: header row has 'INPUT PARAMETER' in column A and scenario names in B..
        int hdr = FindRow(inputs, "INPUT PARAMETER");
        var scenCols = new List<(string Name, int Col)>();
        for (int c = 2; !inputs.Cell(hdr, c).IsEmpty() && Str(inputs.Cell(hdr, c)) != "Unit"; c++)
        {
            var name = Str(inputs.Cell(hdr, c));
            if (scenCols.Any(s => s.Name == name)) throw new WorkbookException($"{Where(inputs.Cell(hdr, c))}: duplicate scenario.");
            scenCols.Add((name, c));
        }

        IXLCell ScalarCell(string label, int col)
        {
            for (int r = hdr + 1; !inputs.Cell(r, 1).IsEmpty(); r++)
                if (Str(inputs.Cell(r, 1)).StartsWith(label, StringComparison.OrdinalIgnoreCase)) return inputs.Cell(r, col);
            throw new WorkbookException($"Inputs: scenario parameter '{label}' not found.");
        }
        double Scalar(string label, int col) => Num(ScalarCell(label, col));
        int Whole(string label, int col) => Int(ScalarCell(label, col));

        var (years, occ) = ReadVectors(inputs, "ANNUAL OCCUPANCY");
        var (years2, adr) = ReadVectors(inputs, "ANNUAL ADR GROWTH");
        if (!years.SequenceEqual(years2)) throw new WorkbookException("Occupancy and ADR growth tables use different years.");

        double[] Vector(Dictionary<string, double[]> table, string title, (string Name, int Col) sc) => table.TryGetValue(sc.Name, out var v)
            ? v : throw new WorkbookException($"'Inputs' table '{title}': no row for the scenario in {Where(inputs.Cell(hdr, sc.Col))}.");

        var scenarios = scenCols.Select(sc => new ScenarioInputs
        {
            Name = sc.Name,
            PurchasePrice = Scalar("Purchase price", sc.Col),
            ClosingCosts = Scalar("Acquisition closing costs", sc.Col),
            AcquisitionFeePct = Scalar("Acquisition fee", sc.Col),
            PipPerKey = Scalar("PIP budget per key", sc.Col),
            PipStartMonth = Whole("PIP start month", sc.Col),
            PipDurationMonths = Whole("PIP duration", sc.Col),
            ExpenseInflation = Scalar("Annual expense inflation", sc.Col),
            LoanToCost = Scalar("Loan-to-cost", sc.Col),
            InterestRate = Scalar("Loan interest rate", sc.Col),
            InterestOnlyMonths = Whole("Interest-only period", sc.Col),
            AmortizationYears = Scalar("Amortization", sc.Col),
            OriginationFeePct = Scalar("Loan origination fee", sc.Col),
            PlacementCosts = Scalar("Debt placement", sc.Col),
            AssetMgmtFeePct = Scalar("Asset management fee", sc.Col),
            HoldMonths = Whole("Hold period", sc.Col),
            ExitCapRate = Scalar("Exit cap rate", sc.Col),
            CostOfSalePct = Scalar("Cost of sale", sc.Col),
            DispositionFeePct = Scalar("Disposition fee", sc.Col),
            GpShare = Scalar("GP share of equity", sc.Col),
            PrefReturn = Scalar("Preferred return", sc.Col),
            Promote = Scalar("GP promote", sc.Col),
            Years = years,
            Occupancy = Vector(occ, "ANNUAL OCCUPANCY", sc),
            AdrGrowth = Vector(adr, "ANNUAL ADR GROWTH", sc),
        }).ToList();

        // Operating line table: header row 'Code' / 'Line item'; ends at first blank code.
        int lineHdr = FindRow(ops, "Line item", 2);
        var lines = new List<OperatingLine>();
        for (int r = lineHdr + 1; !ops.Cell(r, 1).IsEmpty(); r++)
        {
            var section = Str(ops.Cell(r, 3));
            if (!Sections.Contains(section)) throw new WorkbookException($"{Where(ops.Cell(r, 3))}: unknown section; expected {string.Join(", ", Sections)}.");
            lines.Add(new OperatingLine(Str(ops.Cell(r, 1)), Str(ops.Cell(r, 2)), section,
                Num(ops.Cell(r, 4)), Num(ops.Cell(r, 5)), Str(ops.Cell(r, 6))));
        }

        int pctHdr = FindRow(ops, "Percent-of-revenue item", 2);
        var pcts = new List<PercentItem>();
        for (int r = pctHdr + 1; !ops.Cell(r, 1).IsEmpty(); r++)
            pcts.Add(new PercentItem(Str(ops.Cell(r, 1)), Str(ops.Cell(r, 2)), Num(ops.Cell(r, 4)), Str(ops.Cell(r, 6))));

        int seasHdr = FindRow(ops, "Seasonality weight", 4);
        var seas = new double[12];
        var months = new HashSet<double>();
        for (int r = seasHdr + 1; !ops.Cell(r, 1).IsEmpty(); r++)
        {
            var month = Num(ops.Cell(r, 1));
            if (month is not (>= 1 and <= 12) || month != Math.Floor(month))
                throw new WorkbookException($"{Where(ops.Cell(r, 1))}: seasonality month must be a whole number from 1 to 12.");
            if (!months.Add(month)) throw new WorkbookException($"{Where(ops.Cell(r, 1))}: seasonality month is repeated.");
            seas[(int)month - 1] = Num(ops.Cell(r, 4));
        }

        return new Assumptions(property, lines, pcts, seas, scenarios);
    }

    /// <summary>Reads the workbook's own checkpoint values (cached results) for reconciliation only.</summary>
    public static IReadOnlyList<Checkpoint> ReadCheckpoints(string path, IEnumerable<string> scenarioNames)
    {
        using var wb = Open(path);
        var ws = Sheet(wb, "Reconciliation");
        int hdr = FindRow(ws, "CHECKPOINT");
        var names = scenarioNames.ToList();
        var cols = new Dictionary<string, int>();
        for (int c = 2; !ws.Cell(hdr, c).IsEmpty(); c++) cols[Str(ws.Cell(hdr, c))] = c;

        var list = new List<Checkpoint>();
        for (int r = hdr + 1; !ws.Cell(r, 1).IsEmpty() && !Str(ws.Cell(r, 1)).StartsWith("Tolerance"); r++)
        {
            var by = new Dictionary<string, double>();
            foreach (var n in names)
                if (cols.TryGetValue(n, out var c) && TryNum(ws.Cell(r, c), out var v)) by[n] = v;
            list.Add(new Checkpoint(Str(ws.Cell(r, 1)), by));
        }
        return list;
    }

    private static (int[] Years, Dictionary<string, double[]> Values) ReadVectors(IXLWorksheet ws, string title)
    {
        int t = FindRow(ws, title);
        int h = t + 1;                                    // 'Scenario' | 2027 | 2028 ...
        var years = new List<int>();
        for (int c = 2; !ws.Cell(h, c).IsEmpty(); c++) years.Add(Int(ws.Cell(h, c)));
        var vals = new Dictionary<string, double[]>();
        for (int r = h + 1; !ws.Cell(r, 1).IsEmpty(); r++)
            if (!vals.TryAdd(Str(ws.Cell(r, 1)), Enumerable.Range(0, years.Count).Select(i => Num(ws.Cell(r, 2 + i))).ToArray()))
                throw new WorkbookException($"{Where(ws.Cell(r, 1))}: duplicate scenario.");
        return (years.ToArray(), vals);
    }

    private static int FindRow(IXLWorksheet ws, string text, int col = 1)
    {
        IXLCell? uncached = null;
        foreach (var row in ws.RowsUsed())
        {
            var cell = row.Cell(col);
            if (Uncached(cell)) uncached ??= cell; // only a problem when the label is not found elsewhere
            else if (!Value(cell).IsError && Str(cell).Trim().Equals(text, StringComparison.OrdinalIgnoreCase)) return row.RowNumber();
        }
        if (uncached is not null) Value(uncached); // throws, naming the cell
        throw new WorkbookException($"'{ws.Name}': label '{text}' not found in column {col}.");
    }

    private static IXLWorksheet Sheet(XLWorkbook wb, string name) =>
        wb.TryGetWorksheet(name, out var ws) ? ws : throw new WorkbookException($"Sheet '{name}' is missing.");

    private static string Where(IXLCell c) => $"'{c.Worksheet.Name}' cell {c.Address.ColumnLetter}{c.Address.RowNumber}";

    private static bool Uncached(IXLCell c) => c.HasFormula && (c.NeedsRecalculation || c.CachedValue.IsBlank);

    /// <summary>The value of a cell the reader needs. Never <see cref="IXLCell.Value"/> on a formula: that would evaluate it.</summary>
    private static XLCellValue Value(IXLCell c) =>
        !c.HasFormula ? c.Value
        : !Uncached(c) ? c.CachedValue
        : throw new WorkbookException($"{Where(c)}: the formula has no cached value.");

    private static string Str(IXLCell c) =>
        Value(c) is { IsError: false } v ? v.ToString(CultureInfo.CurrentCulture) : throw new WorkbookException($"{Where(c)}: the cell holds an error value.");

    private static int Int(IXLCell c) =>
        Num(c) is var n && n == Math.Floor(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : throw new WorkbookException($"{Where(c)}: expected a whole number.");

    private static double Num(IXLCell c) =>
        Value(c) is { IsNumber: true } v ? v.GetNumber() : throw new WorkbookException($"{Where(c)}: expected a number.");

    private static DateTime Date(IXLCell c) =>
        Value(c) is { IsDateTime: true } v ? v.GetDateTime() : throw new WorkbookException($"{Where(c)}: expected a date.");

    private static bool TryNum(IXLCell c, out double number) =>
        c.HasFormula ? Value(c).TryConvert(out number, CultureInfo.CurrentCulture) : c.TryGetValue(out number);
}
