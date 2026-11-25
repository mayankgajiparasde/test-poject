using System.Globalization;
using System.Text;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;

// Usage: HotelUnderwriting.Cli <reference-workbook.xlsx> [output-folder]
if (args.Length < 1) { Console.Error.WriteLine("Usage: HotelUnderwriting.Cli <reference-workbook.xlsx> [output-folder]"); return 1; }
var input = args[0];
var outDir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "output");

var assumptions = WorkbookReader.ReadAssumptions(input);
var results = assumptions.Scenarios.Select(s => Underwriter.Run(assumptions, s)).ToList();
var checkpoints = WorkbookReader.ReadCheckpoints(input, assumptions.Scenarios.Select(s => s.Name));
var recon = Reconciler.Compare(assumptions, checkpoints);
var impacts = Reconciler.IrrImpacts(assumptions);

Directory.CreateDirectory(outDir);
var xlsx = Path.Combine(outDir, "HotelUnderwriting_Output.xlsx");
OutputWriter.Write(xlsx, results, recon, impacts);

string N(double v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
var md = new StringBuilder("| Scenario | Value | Workbook | Engine | Difference | Result | Explanation |\n|---|---|---:|---:|---:|---|---|\n");
foreach (var r in recon)
{
    bool rate = r.Tolerance == Reconciler.RateTol, mult = r.Tolerance == Reconciler.MultipleTol;
    string F(double v) => rate ? (v * 100).ToString("0.0000", CultureInfo.InvariantCulture) + "%" : mult ? v.ToString("0.0000", CultureInfo.InvariantCulture) + "x" : N(v);
    string D(double v) => rate ? (v * 100).ToString("0.0000", CultureInfo.InvariantCulture) + " pp" : mult ? v.ToString("0.0000", CultureInfo.InvariantCulture) : N(v);
    md.AppendLine($"| {r.Scenario} | {r.Label} | {F(r.Workbook)} | {F(r.Engine)} | {D(r.Difference)} | {(r.Pass ? "PASS" : "FAIL")} | {r.Explanation} |");
}
md.AppendLine().AppendLine("| Scenario | Workbook error | Levered IRR (spec) | Levered IRR (with error) | Levered impact (pp) | LP IRR impact (pp) | GP IRR impact (pp) |\n|---|---|---:|---:|---:|---:|---:|");
foreach (var m in impacts)
    md.AppendLine($"| {m.Scenario} | {m.Description} | {m.SpecLeveredIrr * 100:0.0000}% | {m.WithErrorLeveredIrr * 100:0.0000}% | {m.LeveredImpactPp:+0.0000;-0.0000;0.0000} | {m.LpImpactPp:+0.0000;-0.0000;0.0000} | {m.GpImpactPp:+0.0000;-0.0000;0.0000} |");
File.WriteAllText(Path.Combine(outDir, "reconciliation.md"), md.ToString());

int fail = recon.Count(r => !r.Pass);
Console.WriteLine($"Scenarios: {string.Join(", ", results.Select(r => r.Inputs.Name))}");
foreach (var r in results)
    Console.WriteLine($"{r.Inputs.Name,-9} levered IRR {r.Summary.LeveredIrr * 100:0.0000}%  multiple {r.Summary.LeveredMultiple:0.0000}x  LP {r.Summary.LpIrr * 100:0.0000}%  GP {r.Summary.GpIrr * 100:0.0000}%");
Console.WriteLine($"Reconciliation: {recon.Count - fail}/{recon.Count} pass, {fail} outside tolerance ({recon.Count(r => r.Explanation.StartsWith("UNEXPLAINED"))} unexplained)");
Console.WriteLine($"Wrote {xlsx}");
return 0;
