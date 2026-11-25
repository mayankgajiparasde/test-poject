using ClosedXML.Excel;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Excel;

/// <summary>Writes a NEW workbook with the engine's results. Values only (no formulas), so it opens identically everywhere.</summary>
public static class OutputWriter
{
    private const string Money = "#,##0.00;[Red](#,##0.00)", Pct = "0.0000%", Mult = "0.0000\"x\"";

    public static void Write(string path, IReadOnlyList<ScenarioResult> results, IReadOnlyList<ReconRow> recon, IReadOnlyList<IssueImpact> impacts)
    {
        using var wb = new XLWorkbook();
        WriteSummary(wb.AddWorksheet("Scenario Summary"), results);
        foreach (var r in results)
        {
            WriteProforma(wb.AddWorksheet("Proforma - " + r.Inputs.Name), r);
            WriteMonthly(wb.AddWorksheet("Monthly - " + r.Inputs.Name), r);
            WriteReturns(wb.AddWorksheet("Returns - " + r.Inputs.Name), r);
        }
        WriteRecon(wb.AddWorksheet("Reconciliation"), recon);
        WriteImpacts(wb.AddWorksheet("Workbook Issues"), impacts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    private static void Header(IXLWorksheet ws, int row, params string[] cols)
    {
        for (int i = 0; i < cols.Length; i++) ws.Cell(row, i + 1).Value = cols[i];
        var r = ws.Range(row, 1, row, cols.Length);
        r.Style.Font.Bold = true; r.Style.Fill.BackgroundColor = XLColor.LightGray;
    }

    private static void WriteProforma(IXLWorksheet ws, ScenarioResult r)
    {
        var p = r.Proforma;
        ws.Cell(1, 1).Value = $"{r.Inputs.Name} | Annual operating forecast (engine output)"; ws.Cell(1, 1).Style.Font.Bold = true;
        int row = 3;
        void Line(string label, double[] v, string fmt = Money)
        {
            ws.Cell(row, 1).Value = label;
            for (int i = 0; i < v.Length; i++) { var c = ws.Cell(row, i + 2); c.Value = v[i]; c.Style.NumberFormat.Format = fmt; }
            row++;
        }
        Header(ws, row, new[] { "Calendar year" }.Concat(p.Years.Select(y => y.ToString())).ToArray()); row++;
        Line("Days in year", p.Days, "0"); Line("Fixed-cost inflation index", p.InflationIndex, "0.000000");
        Line("Rooms available", p.RoomsAvailable, "#,##0"); Line("Occupancy", p.Occupancy, "0.00%");
        Line("Occupied rooms", p.OccupiedRooms, "#,##0.00"); Line("ADR growth", p.AdrGrowth, "0.00%"); Line("ADR ($)", p.Adr);
        Line("Rooms revenue", p.RoomsRevenue);
        foreach (var l in p.Lines.Where(x => x.Line.Section == "REV")) Line(l.Line.Name, l.Amounts);
        Line("Total revenue", p.TotalRevenue);
        foreach (var l in p.Lines.Where(x => x.Line.Section == "DEPT")) Line(l.Line.Name, l.Amounts);
        Line("Total departmental expenses", p.DepartmentalExpenses); Line("Departmental profit", p.DepartmentalProfit);
        foreach (var l in p.Lines.Where(x => x.Line.Section == "UNDIST")) Line(l.Line.Name, l.Amounts);
        Line("Franchise fees", p.FranchiseFees); Line("Total undistributed expenses", p.UndistributedExpenses);
        Line("Gross operating profit (GOP)", p.Gop); Line("Base management fee", p.ManagementFee);
        foreach (var l in p.Lines.Where(x => x.Line.Section == "FIXED")) Line(l.Line.Name, l.Amounts);
        Line("FF&E reserve", p.FfeReserve); Line("Total fixed charges & reserve", p.FixedCharges);
        Line("Net operating income (NOI)", p.Noi);
        ws.Columns().AdjustToContents();
    }

    private static void WriteMonthly(IXLWorksheet ws, ScenarioResult r)
    {
        var m = r.Monthly;
        ws.Cell(1, 1).Value = $"{r.Inputs.Name} | Monthly schedule (engine output)"; ws.Cell(1, 1).Style.Font.Bold = true;
        var cols = new (string Name, Func<int, object> Get, string Fmt)[]
        {
            ("Month", i => m.Month[i], "0"), ("Date", i => m.Date[i], "yyyy-mm-dd"), ("Days", i => m.DaysInMonth[i], "0"),
            ("In hold", i => m.InHold[i], "0"), ("Exit", i => m.ExitFlag[i], "0"), ("Hold year", i => m.HoldYear[i], "0"),
            ("Seasonality", i => m.Seasonality[i], "0.0%"), ("Total revenue", i => m.Revenue[i], Money),
            ("NOI before AM fee", i => m.NoiBeforeFee[i], Money), ("Fwd 12m NOI", i => m.ForwardNoi[i], Money),
            ("Purchase price", i => m.PurchasePrice[i], Money), ("Closing costs & acq fee", i => m.ClosingCostsAndFee[i], Money),
            ("NOI", i => m.Noi[i], Money), ("Asset mgmt fee", i => m.AssetMgmtFee[i], Money), ("PIP spend", i => m.PipSpend[i], Money),
            ("Gross sale price", i => m.GrossSalePrice[i], Money), ("Cost of sale", i => m.CostOfSale[i], Money),
            ("Disposition fee", i => m.DispositionFee[i], Money), ("Unlevered CF", i => m.UnleveredCf[i], Money),
            ("Opening balance", i => m.OpeningBalance[i], Money), ("Funding / draws", i => m.Draw[i], Money),
            ("Interest", i => m.Interest[i], Money), ("Scheduled payment", i => m.Payment[i], Money),
            ("Scheduled principal", i => m.Principal[i], Money), ("Loan payoff", i => m.Payoff[i], Money),
            ("Closing balance", i => m.ClosingBalance[i], Money), ("Financing costs", i => m.FinancingCosts[i], Money),
            ("Levered CF", i => m.LeveredCf[i], Money), ("Equity contributions", i => m.Contributions[i], Money),
            ("Distributable cash", i => m.Distributable[i], Money), ("Pref balance BoP", i => m.PrefBop[i], Money),
            ("Tier 1", i => m.Tier1[i], Money), ("Tier 2", i => m.Tier2[i], Money), ("Pref balance EoP", i => m.PrefEop[i], Money),
            ("LP CF", i => m.LpCf[i], Money), ("GP CF", i => m.GpCf[i], Money),
        };
        Header(ws, 3, cols.Select(c => c.Name).ToArray());
        for (int i = 0; i < m.Date.Length; i++)
            for (int c = 0; c < cols.Length; c++)
            {
                var cell = ws.Cell(4 + i, c + 1);
                var v = cols[c].Get(i);
                if (v is DateTime d) cell.Value = d; else cell.Value = Convert.ToDouble(v);
                cell.Style.NumberFormat.Format = cols[c].Fmt;
            }
        ws.SheetView.FreezeRows(3); ws.SheetView.FreezeColumns(2);
        ws.Columns().AdjustToContents();
    }

    private static void WriteReturns(IXLWorksheet ws, ScenarioResult r)
    {
        var s = r.Summary;
        ws.Cell(1, 1).Value = $"{r.Inputs.Name} | Returns summary (engine output)"; ws.Cell(1, 1).Style.Font.Bold = true;
        int row = 3;
        void Item(string label, double? v, string fmt)
        {
            ws.Cell(row, 1).Value = label;
            if (v.HasValue) { ws.Cell(row, 2).Value = v.Value; ws.Cell(row, 2).Style.NumberFormat.Format = fmt; } else ws.Cell(row, 2).Value = "n/a";
            row++;
        }
        Item("PIP total", s.PipTotal, Money); Item("Acquisition fee ($)", s.AcquisitionFee, Money);
        Item("Loan commitment", s.LoanCommitment, Money); Item("PIP holdback", s.PipHoldback, Money);
        Item("Initial funded loan balance", s.InitialFundedLoan, Money); Item("Total financing costs", s.FinancingCostsTotal, Money);
        Item("Annual debt constant", s.DebtConstant, Pct); Item("Equity required at close", s.EquityAtClose, Money);
        Item("Forward 12-month NOI at exit", s.ForwardNoiAtExit, Money); Item("Gross sale price", s.GrossSalePrice, Money);
        Item("Loan payoff at exit", s.LoanPayoff, Money); Item("Net sale proceeds to equity", s.NetSaleProceeds, Money);
        Item("Peak loan balance", s.PeakLoanBalance, Money); Item("Month 1 interest", s.Month1Interest, Money);
        Item("First amortizing payment", s.FirstAmortizingPayment, Money);
        Item("Unlevered IRR", s.UnleveredIrr, Pct); Item("Unlevered multiple", s.UnleveredMultiple, Mult);
        Item("Levered IRR", s.LeveredIrr, Pct); Item("Levered equity multiple", s.LeveredMultiple, Mult);
        Item("Levered net profit", s.LeveredNetProfit, Money);
        Item("LP IRR", s.LpIrr, Pct); Item("LP multiple", s.LpMultiple, Mult);
        Item("GP IRR", s.GpIrr, Pct); Item("GP multiple", s.GpMultiple, Mult);
        row++;
        Item("Check: loan balance after exit (0)", s.LoanBalanceAfterExit, Money);
        Item("Check: max |LP + GP - levered CF| (0)", s.WaterfallMaxAbsError, Money);
        Item("Check: sources - uses at close (0)", s.SourcesLessUses, Money);
        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, IReadOnlyList<ScenarioResult> rs)
    {
        ws.Cell(1, 1).Value = "Scenario comparison (engine output)"; ws.Cell(1, 1).Style.Font.Bold = true;
        Header(ws, 3, new[] { "Metric" }.Concat(rs.Select(r => r.Inputs.Name)).ToArray());
        int row = 4;
        void Item(string label, Func<ScenarioResult, double?> get, string fmt)
        {
            ws.Cell(row, 1).Value = label;
            for (int i = 0; i < rs.Count; i++)
            {
                var v = get(rs[i]); var c = ws.Cell(row, i + 2);
                if (v.HasValue) { c.Value = v.Value; c.Style.NumberFormat.Format = fmt; } else c.Value = "n/a";
            }
            row++;
        }
        Item("Loan commitment", r => r.Summary.LoanCommitment, Money); Item("Equity required at close", r => r.Summary.EquityAtClose, Money);
        Item("Hold period (months)", r => r.Inputs.HoldMonths, "0");
        Item("Gross sale price", r => r.Summary.GrossSalePrice, Money); Item("Loan payoff at exit", r => r.Summary.LoanPayoff, Money);
        Item("Net sale proceeds to equity", r => r.Summary.NetSaleProceeds, Money);
        Item("Unlevered IRR", r => r.Summary.UnleveredIrr, Pct); Item("Levered IRR", r => r.Summary.LeveredIrr, Pct);
        Item("Levered equity multiple", r => r.Summary.LeveredMultiple, Mult);
        Item("LP IRR", r => r.Summary.LpIrr, Pct); Item("GP IRR", r => r.Summary.GpIrr, Pct); Item("GP multiple", r => r.Summary.GpMultiple, Mult);
        ws.Columns().AdjustToContents();
    }

    private static void WriteRecon(IXLWorksheet ws, IReadOnlyList<ReconRow> rows)
    {
        Header(ws, 1, "Scenario", "Value", "Workbook", "Engine", "Difference", "Tolerance", "Result", "Explanation");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; int x = 2 + i;
            ws.Cell(x, 1).Value = r.Scenario; ws.Cell(x, 2).Value = r.Label;
            ws.Cell(x, 3).Value = r.Workbook; ws.Cell(x, 4).Value = r.Engine; ws.Cell(x, 5).Value = r.Difference; ws.Cell(x, 6).Value = r.Tolerance;
            ws.Range(x, 3, x, 6).Style.NumberFormat.Format = "#,##0.000000";
            ws.Cell(x, 7).Value = r.Pass ? "PASS" : "FAIL"; ws.Cell(x, 7).Style.Font.FontColor = r.Pass ? XLColor.Green : XLColor.Red;
            ws.Cell(x, 8).Value = r.Explanation;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteImpacts(IXLWorksheet ws, IReadOnlyList<IssueImpact> impacts)
    {
        Header(ws, 1, "Scenario", "Workbook error", "Levered IRR (spec)", "Levered IRR (with error)", "Levered impact (pp)", "LP IRR impact (pp)", "GP IRR impact (pp)");
        for (int i = 0; i < impacts.Count; i++)
        {
            var m = impacts[i]; int x = 2 + i;
            ws.Cell(x, 1).Value = m.Scenario; ws.Cell(x, 2).Value = m.Description;
            ws.Cell(x, 3).Value = m.SpecLeveredIrr; ws.Cell(x, 4).Value = m.WithErrorLeveredIrr; ws.Cell(x, 5).Value = m.LeveredImpactPp; ws.Cell(x, 6).Value = m.LpImpactPp; ws.Cell(x, 7).Value = m.GpImpactPp;
            ws.Range(x, 3, x, 4).Style.NumberFormat.Format = Pct; ws.Range(x, 5, x, 7).Style.NumberFormat.Format = "0.0000";
        }
        ws.Columns().AdjustToContents();
    }
}
