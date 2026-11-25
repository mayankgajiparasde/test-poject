using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using HotelUnderwriting.Excel;

namespace HotelUnderwriting.Api.Tests;

/// <summary>Hostile and broken workbooks, made in memory from the reference workbook.</summary>
internal static class HostileWorkbooks
{
    public static readonly byte[] Reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));

    public const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Dtd = "<!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><x>&b;</x>";

    public static byte[] Package(Action<ZipArchive> edit, byte[]? from = null)
    {
        var ms = new MemoryStream();
        ms.Write(from ?? Reference);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, true)) edit(zip);
        return ms.ToArray();
    }

    public static void Add(ZipArchive zip, string name, string content = "x") => Add(zip, name, Encoding.UTF8.GetBytes(content));

    public static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        s.Write(content);
    }

    public static void Change(ZipArchive zip, string name, Func<string, string> change)
    {
        var entry = zip.GetEntry(name)!;
        string text;
        using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
        var changed = change(text);
        Assert.NotEqual(text, changed);
        entry.Delete();
        Add(zip, name, changed);
    }

    private static byte[] With(string name, string content = "x") => Package(z => Add(z, name, content));
    private static byte[] With(string name, byte[] content) => Package(z => Add(z, name, content));

    private static byte[] Relationship(string kind, string target, bool external = false) => Package(z => Change(z, "xl/_rels/workbook.xml.rels", s => s.Replace("</Relationships>",
        $"<Relationship Type=\"{Rel}{kind}\" Target=\"{target}\" Id=\"rId99\"{(external ? " TargetMode=\"External\"" : "")}/></Relationships>")));

    private static byte[] Override(string part, string type) => Package(z => Change(z, "[Content_Types].xml", s => s.Replace("</Types>", $"<Override PartName=\"{part}\" ContentType=\"{type}\"/></Types>")));

    private static byte[] ContentType(string type) => Override("/xl/some.bin", type);

    private static byte[] MainType(string type) => Package(z => Change(z, "[Content_Types].xml", s => s.Replace("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", type)));

    public static byte[] Rows(int rows) => With("xl/worksheets/extra.xml", $"<worksheet xmlns=\"{Ns}\"><sheetData>{string.Concat(Enumerable.Repeat("<row/>", rows))}</sheetData></worksheet>");

    /// <summary>The reference workbook plus a part holding <paramref name="cells"/> more cells.</summary>
    public static byte[] Cells(int cells) => With("xl/worksheets/extra.xml", $"<worksheet xmlns=\"{Ns}\"><sheetData><row>{string.Concat(Enumerable.Repeat("<c/>", cells))}</row></sheetData></worksheet>");

    /// <summary>The reference workbook with <paramref name="count"/> more sheet elements (it has 11).</summary>
    public static byte[] ListedSheets(int count) => Package(z => Change(z, "xl/workbook.xml", s => s.Replace("</sheets>",
        string.Concat(Enumerable.Range(100, count).Select(i => $"<sheet name=\"S{i}\" sheetId=\"{i}\"/>")) + "</sheets>")));

    /// <summary>Rewrites the uncompressed size that the central directory declares for one entry.</summary>
    private static byte[] Declared(string name, uint size)
    {
        var b = (byte[])Reference.Clone();
        var wanted = Encoding.ASCII.GetBytes(name);
        for (var i = 0; i + 46 + wanted.Length <= b.Length; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i)) == 0x02014b50 && b.AsSpan(i + 46, wanted.Length).SequenceEqual(wanted))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i + 24), size);
                return b;
            }
        throw new InvalidOperationException("no such entry");
    }

    /// <summary>The reference workbook edited through ClosedXML.</summary>
    public static byte[] Edited(Action<XLWorkbook> edit)
    {
        using var wb = new XLWorkbook(new MemoryStream(Reference));
        edit(wb);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static IXLCell Labelled(XLWorkbook wb, string sheet, string label, int column = 2)
    {
        var ws = wb.Worksheet(sheet);
        return ws.Cell(ws.RowsUsed().First(r => r.Cell(1).GetString().Trim() == label).RowNumber(), column);
    }

    public static string Address(string sheet, string label, int column = 2)
    {
        using var wb = new XLWorkbook(new MemoryStream(Reference));
        return Labelled(wb, sheet, label, column).Address.ToString()!;
    }

    /// <summary>Replaces one cell element of a sheet part.</summary>
    public static byte[] RawCell(string part, string address, string xml) => Package(z => Change(z, part,
        s => new Regex($"<c r=\"{address}\"(?:[^>]*/>|[^>]*>.*?</c>)", RegexOptions.Singleline).Replace(s, xml, 1)));

    private static byte[] NotZip(int which) => which switch
    {
        0 => [],
        1 => Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 + 7)).ToArray(),
        2 => Reference[..(Reference.Length / 2)],
        _ => Plain(),
    };

    private static byte[] Plain()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) Add(z, "hello.txt", "hello");
        return ms.ToArray();
    }

    private const string SheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    public const string CalcChainType = "application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml";
    private const string SameSheet = "same sheet more than once";
    private const string BrokenVml = "<xml><br></xml>";

    private static byte[] Typed(string name, string type, string content) => Package(z => Add(z, name, content), Override("/" + name, type));
    private static byte[] Related(string name, string content, byte[]? from = null) => Package(z => Add(z, name, content),
        Package(z => Change(z, "xl/_rels/workbook.xml.rels", s => s.Replace("</Relationships>", $"<Relationship Type=\"{Rel}worksheet\" Target=\"/{name}\" Id=\"rId98\"/></Relationships>")), from));

    /// <summary>A part that calls itself a calculation chain and holds more cells than a workbook may.</summary>
    public static string ManyChained => $"<calcChain xmlns=\"{Ns}\">{string.Concat(Enumerable.Repeat("<c/>", PackageInspector.MaxCells + 1))}</calcChain>";

    private static byte[] TargetMode(string mode) => Package(z => Change(z, "xl/_rels/workbook.xml.rels", s => s.Replace("</Relationships>",
        $"<Relationship Type=\"{Rel}image\" Target=\"x.png\" Id=\"rId97\" TargetMode=\"{mode}\"/></Relationships>")));

    private const string Zip = "not a readable zip", NotXlsx = "not an Excel workbook", Macro = "contains macros", External = "external links or data connections",
        Embedded = "embedded objects or ActiveX", BadXml = "DTD or is not well-formed", Name = "unsafe entry name";

    /// <summary>One file per rule: name, the text the 422 detail must hold, and the file.</summary>
    public static readonly Dictionary<string, (string Detail, Func<byte[]> Make)> All = new()
    {
        ["empty"] = (Zip, () => NotZip(0)),
        ["random bytes"] = (Zip, () => NotZip(1)),
        ["truncated zip"] = (Zip, () => NotZip(2)),
        ["plain zip"] = (NotXlsx, () => NotZip(3)),
        ["docx main type"] = (NotXlsx, () => MainType("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
        ["xltx template"] = (NotXlsx, () => MainType("application/vnd.openxmlformats-officedocument.spreadsheetml.template.main+xml")),
        ["no package relationships"] = (NotXlsx, () => Package(z => z.GetEntry("_rels/.rels")!.Delete())),

        ["too many entries"] = ("more than 500 entries", () => Package(z => { for (var i = 0; i < 500; i++) Add(z, $"junk/{i}.bin"); })),
        ["huge entry"] = ("larger than 50 MB", () => With("xl/media/big.bin", new byte[PackageInspector.MaxEntryBytes + 1])),
        ["huge total"] = ("larger than 100 MB", () => Package(z => { for (var i = 0; i < 3; i++) Add(z, $"xl/media/{i}.bin", new byte[40 << 20]); })),
        ["size understated"] = (BadXml, () => Declared("xl/worksheets/sheet2.xml", 200)),
        ["size overstated"] = (Zip, () => Declared("xl/worksheets/sheet2.xml", 40_000_000)),
        ["too many listed sheets"] = ("more than 50 sheets", () => ListedSheets(40)),
        ["too many sheet parts"] = ("more than 50 sheets", () => Package(z => { for (var i = 0; i < 40; i++) Add(z, $"xl/x/{i}.xml", $"<worksheet xmlns=\"{Ns}\"/>"); })),
        ["too many rows"] = ("more than 20000 rows", () => Rows(PackageInspector.MaxRowsPerSheet + 1)),
        ["too many cells"] = ("more than 200000 cells", () => Cells(PackageInspector.MaxCells)),

        ["name with dots"] = (Name, () => With("xl/../evil.xml", "<x/>")),
        ["name with leading slash"] = (Name, () => With("/abs.xml", "<x/>")),
        ["name with backslash"] = (Name, () => With("xl\\evil.xml", "<x/>")),
        ["duplicate name"] = ("duplicate entry names", () => With("XL/Workbook.xml", "<x/>")),

        ["vba part"] = (Macro, () => With("xl/vbaProject.bin")),
        ["macro sheet part"] = (Macro, () => With("xl/macrosheets/sheet1.xml", "<x/>")),
        ["xlsm type"] = (Macro, () => MainType("application/vnd.ms-excel.sheet.macroEnabled.main+xml")),
        ["xltm type"] = (Macro, () => MainType("application/vnd.ms-excel.template.macroEnabled.main+xml")),
        ["xlsb type"] = (Macro, () => MainType("application/vnd.ms-excel.sheet.binary.macroEnabled.main")),
        ["vba relationship"] = (Macro, () => Relationship("vbaProject", "data.bin")),

        ["external link part"] = (External, () => With("xl/externalLinks/externalLink1.xml", "<x/>")),
        ["connections part"] = (External, () => With("xl/connections.xml", "<x/>")),
        ["connections type"] = (External, () => Override("/xl/c.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.connections+xml")),
        ["external link relationship"] = (External, () => Relationship("externalLink", "link.xml")),
        ["external image"] = (External, () => Relationship("image", "http://127.0.0.1:1/x.png", external: true)),

        ["ole part"] = (Embedded, () => With("xl/embeddings/oleObject1.bin")),
        ["activex part"] = (Embedded, () => With("xl/activeX/activeX1.xml", "<x/>")),
        ["ole relationship"] = (Embedded, () => Relationship("oleObject", "o.bin")),
        ["package relationship"] = (Embedded, () => Relationship("package", "p.docx")),
        ["activex type"] = (Embedded, () => Override("/xl/a.bin", "application/vnd.ms-office.activeX+xml")),

        ["dtd in xml"] = (BadXml, () => With("xl/extra.xml", Dtd)),
        ["dtd in relationships"] = (BadXml, () => With("xl/worksheets/_rels/sheet1.xml.rels", Dtd)),
        ["dtd in content types"] = (BadXml, () => Package(z => Change(z, "[Content_Types].xml", s => s.Replace("<Types", "<!DOCTYPE Types><Types")))),
        ["dtd in vml"] = (BadXml, () => With("xl/drawings/vmlDrawing1.vml", Dtd)),
        ["dtd under a binary name"] = (BadXml, () => With("xl/media/image1.png", Dtd)),
        ["dtd in utf-16 with a mark"] = (BadXml, () => With("xl/media/a.bin", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Dtd)])),
        ["dtd in utf-16 without a mark"] = (BadXml, () => With("xl/media/b.bin", Encoding.BigEndianUnicode.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16BE\"?>" + Dtd))),
        ["broken xml"] = (BadXml, () => With("xl/extra.xml", "<x><y></x>")),
        ["xml type that is not xml"] = (BadXml, () => Typed("xl/data.bin", "application/vnd.something+xml", "not xml at all")),

        ["query table part"] = (External, () => With("xl/queryTables/queryTable1.xml", "<x/>")),
        ["macro sheet type"] = (Macro, () => ContentType("application/vnd.ms-excel.macrosheet+xml")),
        ["vba project type"] = (Macro, () => ContentType("application/vnd.ms-office.vbaProject")),
        ["vba data type"] = (Macro, () => ContentType("application/vnd.ms-word.vbaData+xml")),
        ["external link type"] = (External, () => ContentType("application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml")),
        ["query table type"] = (External, () => ContentType("application/vnd.openxmlformats-officedocument.spreadsheetml.queryTable+xml")),
        ["ole type"] = (Embedded, () => ContentType("application/vnd.openxmlformats-officedocument.oleObject")),
        ["excel 4 macro relationship"] = (Macro, () => Relationship("xlMacrosheet", "m.bin")),
        ["international macro relationship"] = (Macro, () => Relationship("xlIntlMacrosheet", "m.bin")),
        ["external link path relationship"] = (External, () => Relationship("externalLinkPath", "p.bin")),
        ["connections relationship"] = (External, () => Relationship("connections", "c.bin")),
        ["query table relationship"] = (External, () => Relationship("queryTable", "q.bin")),
        ["control relationship"] = (Embedded, () => Relationship("control", "c.bin")),
        ["activex binary relationship"] = (Embedded, () => Relationship("activeXControlBinary", "a.bin")),

        ["target mode 1"] = (External, () => TargetMode("1")),
        ["target mode with spaces"] = (External, () => TargetMode(" External ")),
        ["target mode in lower case"] = (External, () => TargetMode("external")),
        ["two worksheet relationships to one part"] = (SameSheet, () => Relationship("worksheet", "worksheets/sheet1.xml")),
        ["the same part spelled with dots"] = (SameSheet, () => Relationship("worksheet", "../xl/./worksheets/SHEET1.xml")),
        ["two sheets with one relationship id"] = (SameSheet, () => Package(z => Change(z, "xl/workbook.xml", s => s.Replace("</sheets>",
            "<sheet xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" name=\"Z\" sheetId=\"99\" r:id=\"rId1\"/></sheets>")))),
        ["calc chain root typed as a worksheet"] = ("more than 200000 cells", () => Typed("xl/c.xml", SheetType, ManyChained)),
        ["calc chain related as a worksheet"] = ("more than 200000 cells", () => Related("xl/c.xml", ManyChained, Override("/xl/c.xml", CalcChainType))),
        ["calc chain root without the type"] = ("more than 200000 cells", () => With("xl/c.xml", ManyChained)),
        ["loose vml typed as a worksheet"] = (BadXml, () => Typed("xl/v.vml", SheetType, BrokenVml)),
        ["loose vml related as a worksheet"] = (BadXml, () => Related("xl/v.vml", BrokenVml)),
        ["loose vml declared as something else"] = (BadXml, () => Typed("xl/v.vml", "application/xml", BrokenVml)),
    };
}
