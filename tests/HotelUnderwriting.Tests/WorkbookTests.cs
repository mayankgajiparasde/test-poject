using System.Security.Cryptography;
using ClosedXML.Excel;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

/// <summary>CAP-1 (assumption import) and CAP-7 (output workbook, reference workbook untouched). Writes only to a per-test temp folder.</summary>
public class WorkbookTests : IDisposable
{
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private readonly string _tmp = Directory.CreateTempSubdirectory("hue-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // cleanup must not mask a real assertion failure
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Import_reads_the_shipped_workbook_values()
    {
        var a = WorkbookReader.ReadAssumptions(WorkbookPath);

        Assert.Equal(new[] { "Base", "Upside", "Downside" }, a.Scenarios.Select(s => s.Name));
        var b = a.Scenarios.First(s => s.Name == "Base");
        var d = a.Scenarios.First(s => s.Name == "Downside");
        Assert.Equal(62_000_000, b.PurchasePrice);
        Assert.Equal(60, b.HoldMonths);
        Assert.Equal(12, d.PipDurationMonths);
        Assert.Equal(Enumerable.Range(2027, 10), d.Years);
        Assert.Equal(0.58, d.Occupancy[0], 9);                       // 2027
        Assert.All(a.Scenarios, s => { Assert.Equal(10, s.Occupancy.Count); Assert.Equal(10, s.AdrGrowth.Count); });

        Assert.Equal(12, a.Lines.Count);
        Assert.Contains(a.Lines, l => l.Code == "FB_EXP");
        Assert.Equal(3, a.PercentItems.Count);
        var fran = a.PercentItems.Single(p => p.Code == "FRAN");
        Assert.Equal(0.09, fran.Pct, 9);
        Assert.Equal("ROOMS_REV", fran.Base);
        var mgmt = a.PercentItems.Single(p => p.Code == "MGMT");
        Assert.Equal(0.03, mgmt.Pct, 9);
        Assert.Equal("TOTAL_REV", mgmt.Base);
        var ffe = a.PercentItems.Single(p => p.Code == "FFE");
        Assert.Equal(0.04, ffe.Pct, 9);
        Assert.Equal("TOTAL_REV", ffe.Base);

        var u = a.Scenarios.First(s => s.Name == "Upside");
        Assert.Equal(48, u.HoldMonths);
        Assert.Equal(0.0775, u.ExitCapRate, 9);
        Assert.Equal(0.65, a.Property.BaseOccupancy, 9);
        Assert.Equal(150, a.Property.BaseAdr, 9);

        Assert.Equal(420, a.Property.Keys);
        Assert.Equal(new DateTime(2027, 4, 1), a.Property.AcquisitionDate);
        Assert.Equal(2026, a.Property.BaseYear);
        Assert.Equal(12, a.Seasonality.Count);
        Assert.Equal(1.0, a.Seasonality.Sum(), 9);
    }

    [Fact]
    public void Changed_input_in_a_copy_changes_the_engine_with_no_code_change()
    {
        var copy = Path.Combine(_tmp, "copy.xlsx");
        File.Copy(WorkbookPath, copy);
        using (var wb = new XLWorkbook(copy))
        {
            var ws = wb.Worksheet("Inputs");
            var hdr = ws.RowsUsed().First(r => r.Cell(1).GetString().Trim() == "INPUT PARAMETER");
            int baseCol = hdr.CellsUsed().First(c => c.GetString() == "Base").Address.ColumnNumber;
            var row = ws.RowsUsed().First(r => r.RowNumber() > hdr.RowNumber()
                && r.Cell(1).GetString().StartsWith("Hold period", StringComparison.OrdinalIgnoreCase));
            row.Cell(baseCol).Value = 36;
            wb.Save();
        }

        var a = WorkbookReader.ReadAssumptions(copy);
        var s = a.Scenarios.First(x => x.Name == "Base");
        Assert.Equal(36, s.HoldMonths);
        Assert.Equal(48, a.Scenarios.First(x => x.Name == "Upside").HoldMonths);   // other scenarios untouched
        var r = Underwriter.Run(a, s);
        Assert.Equal(1, r.Monthly.ExitFlag[36]);
        Assert.Equal(1, r.Monthly.ExitFlag.Sum());

        Assert.InRange(Math.Abs(r.Summary.GrossSalePrice - r.Monthly.ForwardNoi[36] / s.ExitCapRate), 0, 0.005);
    }

    [Fact]
    public void Output_workbook_has_every_required_sheet_and_97_monthly_rows()
    {
        var a = WorkbookReader.ReadAssumptions(WorkbookPath);
        var results = a.Scenarios.Select(s => Underwriter.Run(a, s)).ToList();
        var recon = Reconciler.Compare(a, WorkbookReader.ReadCheckpoints(WorkbookPath, a.Scenarios.Select(s => s.Name)));
        var impacts = Reconciler.IrrImpacts(a);
        var xlsx = Path.Combine(_tmp, "out", "HotelUnderwriting_Output.xlsx");

        OutputWriter.Write(xlsx, results, recon, impacts);

        using var wb = new XLWorkbook(xlsx);
        var expected = new[] { "Scenario Summary", "Reconciliation", "Workbook Issues" }
            .Concat(new[] { "Base", "Upside", "Downside" }.SelectMany(n => new[] { $"Proforma - {n}", $"Monthly - {n}", $"Returns - {n}" }));
        foreach (var name in expected) Assert.True(wb.Worksheets.Contains(name), $"missing sheet '{name}'");

        foreach (var n in new[] { "Base", "Upside", "Downside" })
        {
            var monthly = wb.Worksheet($"Monthly - {n}");
            Assert.Equal(97, monthly.LastRowUsed()!.RowNumber() - 3);    // header on row 3, data from row 4
            Assert.Equal(0, monthly.Cell(4, 1).GetDouble());
            Assert.Equal(96, monthly.Cell(100, 1).GetDouble());
        }

        Assert.Equal(67, wb.Worksheet("Reconciliation").RowsUsed().Count());   // header + 22 checkpoints x 3 scenarios
        Assert.True(wb.Worksheet("Workbook Issues").RowsUsed().Count() > 1);

        Assert.Equal(12, wb.Worksheets.Count);
        Assert.Equal(1 + impacts.Count, wb.Worksheet("Workbook Issues").RowsUsed().Count());

        // Written values read back against the in-memory results.
        foreach (var n in new[] { "Base", "Downside" })
        {
            var res = results.First(r => r.Inputs.Name == n);
            var m = res.Monthly;
            var ws = wb.Worksheet($"Monthly - {n}");
            int Col(string h) => ws.Row(3).CellsUsed().First(c => c.GetString() == h).Address.ColumnNumber;
            int exit = Array.IndexOf(m.ExitFlag, 1);
            foreach (var (h, arr) in new[] { ("Levered CF", m.LeveredCf), ("LP CF", m.LpCf), ("GP CF", m.GpCf), ("Tier 1", m.Tier1), ("Tier 2", m.Tier2) })
                foreach (var i in new[] { 0, 1, exit })
                    Assert.InRange(Math.Abs(arr[i] - ws.Cell(4 + i, Col(h)).GetDouble()), 0, 0.005);
        }

        var summary = wb.Worksheet("Scenario Summary");
        var sumRow = summary.RowsUsed().First(r => r.Cell(1).GetString() == "Levered IRR");
        for (int i = 0; i < results.Count; i++)
        {
            Assert.Equal(results[i].Inputs.Name, summary.Cell(3, i + 2).GetString());
            var irr = results[i].Summary.LeveredIrr;
            Assert.NotNull(irr);
            Assert.InRange(Math.Abs(irr.Value - sumRow.Cell(i + 2).GetDouble()), 0, 0.0001);

            var ret = wb.Worksheet($"Returns - {results[i].Inputs.Name}");
            var cell = ret.RowsUsed().First(r => r.Cell(1).GetString() == "Levered IRR").Cell(2);
            Assert.InRange(Math.Abs(irr.Value - cell.GetDouble()), 0, 0.0001);
        }

        var rec = wb.Worksheet("Reconciliation");
        Assert.Equal(recon[0].Workbook, rec.Cell(2, 3).GetDouble(), 6);
        Assert.Equal(recon[0].Engine, rec.Cell(2, 4).GetDouble(), 6);
        Assert.Equal(recon[0].Pass ? "PASS" : "FAIL", rec.Cell(2, 7).GetString());

        var baseRes = results.First(r => r.Inputs.Name == "Base");
        int yi = baseRes.Proforma.IndexOfYear(2029);
        var pf = wb.Worksheet("Proforma - Base");
        Assert.Equal("2029", pf.Cell(3, yi + 2).GetString());
        var noiRow = pf.RowsUsed().First(r => r.Cell(1).GetString() == "Net operating income (NOI)");
        Assert.InRange(Math.Abs(baseRes.Proforma.Noi[yi] - noiRow.Cell(yi + 2).GetDouble()), 0, 0.005);
    }

    [Fact]
    public void Changed_operating_line_in_a_copy_lowers_noi()
    {
        var copy = Path.Combine(_tmp, "ops.xlsx");
        File.Copy(WorkbookPath, copy);
        using (var wb = new XLWorkbook(copy))
        {
            var ws = wb.Worksheet("Operating Inputs");
            var hdr = ws.RowsUsed().First(r => r.Cell(2).GetString().Trim() == "Line item");
            int col = hdr.CellsUsed().First(c => c.GetString() == "Base year ($)").Address.ColumnNumber;
            var ptax = ws.RowsUsed().First(r => r.RowNumber() > hdr.RowNumber() && r.Cell(1).GetString() == "PTAX").Cell(col);
            ptax.Value = ptax.GetDouble() + 1_000_000;
            wb.Save();
        }

        double Noi2029(string path)
        {
            var a = WorkbookReader.ReadAssumptions(path);
            var r = Underwriter.Run(a, a.Scenarios.First(s => s.Name == "Base"));
            return r.Proforma.Noi[r.Proforma.IndexOfYear(2029)];
        }
        Assert.True(Noi2029(copy) < Noi2029(WorkbookPath));
    }

    [Fact]
    public void Reference_workbook_is_byte_identical_after_read_and_full_run()
    {
        var before = Sha256(WorkbookPath);
        var stamp = File.GetLastWriteTimeUtc(WorkbookPath);

        var a = WorkbookReader.ReadAssumptions(WorkbookPath);
        var results = a.Scenarios.Select(s => Underwriter.Run(a, s)).ToList();
        var recon = Reconciler.Compare(a, WorkbookReader.ReadCheckpoints(WorkbookPath, a.Scenarios.Select(s => s.Name)));
        OutputWriter.Write(Path.Combine(_tmp, "HotelUnderwriting_Output.xlsx"), results, recon, Reconciler.IrrImpacts(a));

        Assert.Equal(before, Sha256(WorkbookPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(WorkbookPath));
    }
}
