using System.IO.Compression;
using System.Xml;

namespace HotelUnderwriting.Excel;

/// <summary>The workbook is not acceptable. The message is safe to show: a fixed reason, or the sheet, label or cell. It never holds a cell value.</summary>
public sealed class WorkbookException(string message) : Exception(message);

/// <summary>
/// Checks an untrusted .xlsx package before any workbook library opens it. Sizes are counted on the bytes actually decompressed.
/// Every XML part is read with DTDs prohibited and no resolver. Nothing is written to disk or fetched.
/// </summary>
public static class PackageInspector
{
    public const int MaxEntries = 500, MaxSheets = 50, MaxRowsPerSheet = 20_000, MaxCells = 200_000;
    public const long MaxEntryBytes = 50L << 20, MaxTotalBytes = 100L << 20;

    private const string WorkbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string ContentTypes = "[Content_Types].xml";
    private const string VmlType = "application/vnd.openxmlformats-officedocument.vmlDrawing";
    private const string CalcChainType = "application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml";

    private const string NotZip = "The file is not a readable zip package.";
    private const string NotWorkbook = "The package is not an Excel workbook (.xlsx).";
    private const string Macro = "The workbook contains macros.";
    private const string External = "The workbook contains external links or data connections.";
    private const string Embedded = "The workbook contains embedded objects or ActiveX controls.";
    private const string SameSheet = "The workbook refers to the same sheet more than once.";

    public static void Inspect(byte[] file)
    {
        try { InspectZip(file); }
        catch (InvalidDataException) { throw new WorkbookException(NotZip); }
    }

    private static void InspectZip(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file, false), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries) throw new WorkbookException($"The package has more than {MaxEntries} entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var n = e.FullName;
            if (n.Contains("..") || n.StartsWith('/') || n.Contains('\\')) throw new WorkbookException("The package has an unsafe entry name.");
            if (!names.Add(n)) throw new WorkbookException("The package has duplicate entry names.");
            var l = n.ToLowerInvariant();
            if (l.Contains("vbaproject") || l.StartsWith("xl/macrosheets/")) throw new WorkbookException(Macro);
            if (l.StartsWith("xl/externallinks/") || l.StartsWith("xl/querytables/") || l == "xl/connections.xml") throw new WorkbookException(External);
            if (l.StartsWith("xl/embeddings/") || l.StartsWith("xl/activex/")) throw new WorkbookException(Embedded);
        }

        long total = 0;
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "/part/name" or "extension" -> content type
        var typesEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ContentTypes, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbookException(NotWorkbook);
        Xml(Read(typesEntry, ref total), false, r =>
        {
            if (r.LocalName is not ("Default" or "Override") || r.GetAttribute("ContentType") is not { } type) return;
            var t = type.ToLowerInvariant();
            if (t.Contains("macroenabled") || t.Contains("macrosheet") || t.Contains("vbaproject") || t.Contains("vbadata")) throw new WorkbookException(Macro);
            if (t.Contains("externallink") || t.Contains("spreadsheetml.connections") || t.Contains("querytable")) throw new WorkbookException(External);
            if (t.Contains("oleobject") || t.Contains("activex")) throw new WorkbookException(Embedded);
            if ((r.GetAttribute("PartName") ?? r.GetAttribute("Extension")) is { } key) types[key] = type;
        });

        int sheets = 0, worksheetParts = 0, cells = 0;
        var mains = new List<string>();
        var worksheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "/part/name" targeted by a worksheet relationship
        var sheetIds = new HashSet<string>(StringComparer.Ordinal);
        // relationship parts first, so that what is a worksheet is known before any other part is read
        foreach (var e in zip.Entries.OrderBy(e => !e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            if (e == typesEntry) continue;
            var data = Read(e, ref total);
            var name = e.FullName;
            var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var type = types.GetValueOrDefault("/" + name) ?? types.GetValueOrDefault(extension) ?? "";
            // Whatever the extension claims: anything that is declared as XML or starts like XML is parsed as XML.
            if (!(extension is "xml" or "rels" or "vml" || type.EndsWith("xml", StringComparison.OrdinalIgnoreCase) || StartsLikeXml(data))) continue;
            string? root = null;
            var rows = 0;
            // Excel's own VML drawings are often not well formed; a DTD can only come before the first element, so that stays rejected.
            var worksheet = worksheets.Contains("/" + name) || type.EndsWith("spreadsheetml.worksheet+xml", StringComparison.OrdinalIgnoreCase);
            var calcChain = !worksheet && type.Equals(CalcChainType, StringComparison.OrdinalIgnoreCase);
            var lenient = extension == "vml" && !worksheet && (type.Length == 0 || type.Equals(VmlType, StringComparison.OrdinalIgnoreCase));
            Xml(data, lenient, r =>
            {
                root ??= r.LocalName;
                if (r.Depth == 0 && root == "worksheet" && ++worksheetParts > MaxSheets) throw TooManySheets();
                switch (r.LocalName)
                {
                    case "sheet" when root == "workbook":
                        if (++sheets > MaxSheets) throw TooManySheets();
                        if (RelationshipId(r) is { } id && !sheetIds.Add(id)) throw new WorkbookException(SameSheet);
                        break;
                    case "row" when !(calcChain && root == "calcChain") && ++rows > MaxRowsPerSheet: throw new WorkbookException($"A sheet has more than {MaxRowsPerSheet} rows.");
                    case "c" when !(calcChain && root == "calcChain") && ++cells > MaxCells: throw new WorkbookException($"The workbook has more than {MaxCells} cells.");
                    case "Relationship":
                        var kind = r.GetAttribute("Type") is { } t ? t[(t.LastIndexOf('/') + 1)..].ToLowerInvariant() : "";
                        if (kind is "vbaproject" or "xlmacrosheet" or "xlintlmacrosheet") throw new WorkbookException(Macro);
                        if (kind is "externallink" or "externallinkpath" or "connections" or "querytable") throw new WorkbookException(External);
                        if (kind is "oleobject" or "package" or "control" or "activexcontrolbinary") throw new WorkbookException(Embedded);
                        // Hyperlinks are the only way out of the package, and nothing here or in the reader follows them.
                        // exactly "Internal" or absent: the library also takes other spellings (such as "1" or " External ") as external
                        if (kind != "hyperlink" && r.GetAttribute("TargetMode") is not (null or "Internal")) throw new WorkbookException(External);
                        if (kind == "worksheet" && !worksheets.Add(Resolve(name, r.GetAttribute("Target") ?? ""))) throw new WorkbookException(SameSheet);
                        if (kind == "officedocument" && name.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)) mains.Add("/" + (r.GetAttribute("Target") ?? "").TrimStart('/'));
                        break;
                }
            });
        }

        if (mains.Count == 0 || mains.Any(m => (types.GetValueOrDefault(m) ?? types.GetValueOrDefault(Path.GetExtension(m).TrimStart('.'))) != WorkbookType))
            throw new WorkbookException(NotWorkbook);
    }

    private static WorkbookException TooManySheets() => new($"The workbook has more than {MaxSheets} sheets.");

    /// <summary>The r:id attribute: the one called "id" that is in a namespace.</summary>
    private static string? RelationshipId(XmlReader r)
    {
        string? id = null;
        while (r.MoveToNextAttribute())
            if (r.LocalName == "id" && r.NamespaceURI.Length > 0) id = r.Value;
        r.MoveToElement();
        return id;
    }

    /// <summary>The "/part/name" a relationship target means, seen from the folder that holds the "_rels" folder.</summary>
    private static string Resolve(string relationshipPart, string target)
    {
        var folder = relationshipPart[..Math.Max(0, relationshipPart.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase))];
        return Uri.TryCreate(new Uri("http://package/" + folder), target, out var part) ? Uri.UnescapeDataString(part.AbsolutePath) : target;
    }

    private static ArraySegment<byte> Read(ZipArchiveEntry entry, ref long total)
    {
        using var source = entry.Open();
        // sized once from the declared length (capped), so a large part is not held twice while it grows
        var data = new MemoryStream((int)Math.Clamp(entry.Length, 0, MaxEntryBytes));
        var chunk = new byte[81920];
        int n;
        while ((n = source.Read(chunk)) > 0)
        {
            total += n;
            if (data.Length + n > MaxEntryBytes) throw new WorkbookException($"A part is larger than {MaxEntryBytes >> 20} MB decompressed.");
            if (total > MaxTotalBytes) throw new WorkbookException($"The package is larger than {MaxTotalBytes >> 20} MB decompressed.");
            data.Write(chunk, 0, n);
        }
        // .NET stops at the declared size, so a header that understates it leaves a cut-off part (the XML check fails on that);
        // one that overstates it shows up here.
        if (data.Length != entry.Length) throw new WorkbookException(NotZip);
        return new ArraySegment<byte>(data.GetBuffer(), 0, (int)data.Length);
    }

    private static bool StartsLikeXml(ArraySegment<byte> data)
    {
        // skips byte order marks, white space and the zero bytes of UTF-16 / UTF-32
        foreach (var b in data)
            if (b is not (0 or 0x09 or 0x0A or 0x0D or 0x20 or 0xEF or 0xBB or 0xBF or 0xFE or 0xFF)) return b == '<';
        return false;
    }

    private static void Xml(ArraySegment<byte> data, bool lenient, Action<XmlReader> element)
    {
        var started = false;
        try
        {
            using var r = XmlReader.Create(new MemoryStream(data.Array!, data.Offset, data.Count, false), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element) { started = true; element(r); }
        }
        catch (XmlException) when (lenient && started) { }
        catch (XmlException) { throw new WorkbookException("A part has a DTD or is not well-formed XML."); }
    }
}
