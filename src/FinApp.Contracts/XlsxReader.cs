using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace FinApp.Contracts;

/// <summary>
/// Reads an <c>.xlsx</c> (Office Open XML) workbook's first sheet into a header row + data rows of plain strings —
/// the same shape as <see cref="BankFileParser.ReadCsv"/>, so an uploaded Excel statement flows through the exact
/// same column-mapper and review step as a CSV. Pure (unzip + XML only, no external library), so it runs in the
/// Blazor WASM client. Date-formatted cells (stored as Excel serial numbers) are converted back to yyyy-MM-dd so the
/// generic date parser reads them; everything else comes through as its text/number value.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var sheet = FirstSheet(zip) ?? throw new InvalidOperationException("No worksheet in the workbook.");

        var rows = new List<IReadOnlyList<string>>();
        using (var s = sheet.Open())
        {
            var sheetData = XDocument.Load(s).Root?.Element(S + "sheetData");
            if (sheetData is null) return ([], []);
            foreach (var row in sheetData.Elements(S + "row"))
            {
                var cells = new List<string>();
                var col = 0;
                foreach (var c in row.Elements(S + "c"))
                {
                    var at = ColIndex(c.Attribute("r")?.Value);
                    while (col < at) { cells.Add(""); col++; }
                    cells.Add(CellValue(c, shared, dateStyles));
                    col++;
                }
                rows.Add(cells);
            }
        }

        // Some exports prepend blank/title rows before the real header — skip leading empty rows.
        while (rows.Count > 0 && rows[0].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(0);
        return rows.Count == 0 ? ([], []) : (rows[0], rows.Skip(1).ToList());
    }

    private static string CellValue(XElement c, IReadOnlyList<string> shared, HashSet<int> dateStyles)
    {
        var t = c.Attribute("t")?.Value;
        if (t == "inlineStr") return (c.Element(S + "is")?.Value ?? "").Trim();
        var v = c.Element(S + "v")?.Value;
        if (t == "s") return int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
        if (v is null) return "";
        var style = c.Attribute("s")?.Value;
        if (style is not null && int.TryParse(style, out var si) && dateStyles.Contains(si)
            && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(serial)).ToString("yyyy-MM-dd");
        return v;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var s = entry.Open();
        return XDocument.Load(s).Root?.Elements(S + "si").Select(si => si.Value).ToList() ?? [];
    }

    /// <summary>The set of cell-style indices whose number format is a date, so their numeric values are read as dates.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry is null) return result;
        using var s = entry.Open();
        var doc = XDocument.Load(s);

        var dateFmtIds = new HashSet<int> { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 }; // built-in date/time formats
        foreach (var nf in doc.Descendants(S + "numFmt"))
            if ((int?)nf.Attribute("numFmtId") is { } id && LooksLikeDate(nf.Attribute("formatCode")?.Value ?? ""))
                dateFmtIds.Add(id);

        var cellXfs = doc.Root?.Element(S + "cellXfs");
        if (cellXfs is null) return result;
        var i = 0;
        foreach (var xf in cellXfs.Elements(S + "xf"))
        {
            if (dateFmtIds.Contains((int?)xf.Attribute("numFmtId") ?? 0)) result.Add(i);
            i++;
        }
        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var c = code.ToLowerInvariant();
        return (c.Contains('y') || c.Contains('d')) && !c.Contains("0.0") && !c.Contains('#');
    }

    private static ZipArchiveEntry? FirstSheet(ZipArchive zip) =>
        zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                               && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
           .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal).FirstOrDefault();

    private static int ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (char.IsLetter(ch)) col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            else break;
        }
        return Math.Max(0, col - 1);
    }
}
