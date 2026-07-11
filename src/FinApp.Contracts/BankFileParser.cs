using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace FinApp.Contracts;

/// <summary>One transaction parsed from an uploaded statement. <see cref="Amount"/> is signed:
/// negative = money out (an expense), positive = money in.</summary>
public readonly record struct ImportedTransaction(DateOnly Date, decimal Amount, string Description);

/// <summary>
/// Pure parsing of downloaded bank statements into <see cref="ImportedTransaction"/>s — a compliance-free,
/// zero-cost alternative to live Open Banking. Supports the two standardized formats (OFX, QIF) with no
/// per-bank mapping, plus generic CSV where the caller supplies the column indices (the UI offers a mapper).
/// No I/O and no dependencies, so it's fully unit-testable and runs the same in the WASM and MAUI hosts.
/// </summary>
public static class BankFileParser
{
    public enum Format { Unknown, Ofx, Qif, Csv, Xml, Html }

    /// <summary>Guess the format from the file name, then the content. Note some banks name an HTML-table export
    /// ".xls" and an XML export ".xml"/".camt" — content sniffing catches those.</summary>
    public static Format Detect(string? fileName, string text)
    {
        var head = TrimBom(text).TrimStart();
        // Content first for the ambiguous ones: an .xls that's really an HTML table, an .xml statement, etc.
        if (head.Contains("<OFX", StringComparison.OrdinalIgnoreCase) || head.Contains("<STMTTRN", StringComparison.OrdinalIgnoreCase)) return Format.Ofx;
        if (head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || head.Contains("<table", StringComparison.OrdinalIgnoreCase)) return Format.Html;
        if (head.Contains("BkToCstmrStmt", StringComparison.OrdinalIgnoreCase) || head.Contains(":camt.", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<AccountMovement", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith('<') && head.Contains("<Ntry", StringComparison.OrdinalIgnoreCase))) return Format.Xml;

        var ext = (fileName ?? "").ToLowerInvariant();
        if (ext.EndsWith(".ofx") || ext.EndsWith(".qfx")) return Format.Ofx;
        if (ext.EndsWith(".qif")) return Format.Qif;
        if (ext.EndsWith(".htm") || ext.EndsWith(".html")) return Format.Html;
        if (ext.EndsWith(".xml") || ext.EndsWith(".camt")) return Format.Xml;
        if (ext.EndsWith(".csv") || ext.EndsWith(".tsv") || ext.EndsWith(".txt")) return Format.Csv;
        if (head.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase)) return Format.Qif;
        return text.Contains(',') || text.Contains(';') || text.Contains('\t') ? Format.Csv : Format.Unknown;
    }

    private static string TrimBom(string s) => s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

    // ---- ISO 20022 CAMT.053 (bank-to-customer statement) XML ----

    // Row-element names across the bank statement schemas we've seen (ISO 20022 CAMT, DAIS eBank, generic).
    private static readonly string[] XmlRowNames = { "Ntry", "AccountMovement", "Transaction", "StatementLine", "StmtLine", "Movement", "Operation" };
    private static readonly string[] XmlAmountNames = { "Amt", "Amount", "Sum", "Value" };
    private static readonly string[] XmlDirNames = { "CdtDbtInd", "MovementType", "Type", "Direction" };
    private static readonly string[] XmlDateNames = { "ValueDate", "BookgDt", "ValDt", "Date", "Dt", "DtTm" };
    private static readonly string[] XmlDescNames = { "Ustrd", "Reason", "Description", "Narrative", "AddtlTxInf", "AddtlNtryInf", "Details" };

    /// <summary>Parse a statement XML into transactions, schema-flexible so it handles ISO 20022 CAMT.053
    /// (<c>&lt;Ntry&gt;</c> + <c>CdtDbtInd</c>), the DAIS eBank format (<c>&lt;AccountMovement&gt;</c> +
    /// <c>MovementType</c>), and similar. Direction (debit/credit) sets the sign; if a schema has none, the amount's
    /// own sign is kept. Namespace-agnostic (matched by local element name).</summary>
    public static IReadOnlyList<ImportedTransaction> ParseXml(string text)
    {
        var list = new List<ImportedTransaction>();
        XDocument doc;
        try { doc = XDocument.Parse(text); } catch { return list; }

        foreach (var row in doc.Descendants().Where(e => XmlRowNames.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
        {
            string? First(string[] names) => row.Descendants()
                .FirstOrDefault(x => names.Contains(x.Name.LocalName, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value))?.Value?.Trim();

            var amtRaw = First(XmlAmountNames);
            var dateRaw = First(XmlDateNames);
            var dir = First(XmlDirNames);
            var desc = First(XmlDescNames) ?? "";
            if (amtRaw is null || dateRaw is null || !TryAmount(amtRaw, out var amount) || !TryLooseDate(dateRaw, out var date)) continue;

            // DBIT / Debit → money out; CRDT / Credit → money in. No direction element → keep the amount's own sign.
            if (dir is not null)
                amount = dir.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? Math.Abs(amount) : -Math.Abs(amount);
            list.Add(new ImportedTransaction(date, amount, Clean(desc)));
        }
        return list;
    }

    // ---- HTML table (banks that export a .xls/.htm that is really an HTML <table>, e.g. DAIS eBank) ----

    /// <summary>Read an HTML <c>&lt;table&gt;</c> into a header row + data rows of plain strings — the same shape as
    /// <see cref="ReadCsv"/>, so a bank's HTML "Excel" export flows through the column mapper. Cells are de-tagged and
    /// HTML-entity-decoded; the header is the first row that has several cells (so a colspan title banner is skipped).</summary>
    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) ReadHtmlTable(string html)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (System.Text.RegularExpressions.Match tr in System.Text.RegularExpressions.Regex.Matches(
            html, "<tr\\b[^>]*>(.*?)</tr>", RxOptions))
        {
            var cells = new List<string>();
            foreach (System.Text.RegularExpressions.Match td in System.Text.RegularExpressions.Regex.Matches(
                tr.Groups[1].Value, "<t[dh]\\b[^>]*?/>|<t[dh]\\b[^>]*>(.*?)</t[dh]>", RxOptions))
                cells.Add(td.Groups[1].Success ? CleanHtml(td.Groups[1].Value) : "");
            if (cells.Count > 0) rows.Add(cells);
        }
        // The header is the first row with several columns (skips single-cell title/colspan banners).
        var headerIdx = rows.FindIndex(r => r.Count >= 3);
        if (headerIdx < 0) return ([], []);
        return (rows[headerIdx], rows.Skip(headerIdx + 1).Where(r => r.Count >= 3).ToList());
    }

    private const System.Text.RegularExpressions.RegexOptions RxOptions =
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase;

    private static string CleanHtml(string cell)
    {
        var noTags = System.Text.RegularExpressions.Regex.Replace(cell, "<[^>]+>", " ");
        var decoded = noTags.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")
            .Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'");
        return Clean(decoded);
    }

    // ---- OFX / QFX ----

    public static IReadOnlyList<ImportedTransaction> ParseOfx(string text)
    {
        var list = new List<ImportedTransaction>();
        foreach (var block in Between(text, "<STMTTRN>", "</STMTTRN>"))
        {
            var amtRaw = Tag(block, "TRNAMT");
            var dateRaw = Tag(block, "DTPOSTED");
            var name = Tag(block, "NAME");
            var memo = Tag(block, "MEMO");
            if (amtRaw is null || dateRaw is null) continue;
            if (!TryAmount(amtRaw, out var amount)) continue;
            if (!TryOfxDate(dateRaw, out var date)) continue;
            list.Add(new ImportedTransaction(date, amount, Clean(name ?? memo ?? "")));
        }
        return list;
    }

    private static IEnumerable<string> Between(string text, string open, string close)
    {
        var i = 0;
        while (true)
        {
            var s = text.IndexOf(open, i, StringComparison.OrdinalIgnoreCase);
            if (s < 0) yield break;
            var e = text.IndexOf(close, s, StringComparison.OrdinalIgnoreCase);
            if (e < 0) yield break;
            yield return text.Substring(s + open.Length, e - s - open.Length);
            i = e + close.Length;
        }
    }

    /// <summary>Read an OFX/SGML tag value: works whether the tag is closed (&lt;T&gt;v&lt;/T&gt;) or open
    /// (&lt;T&gt;v up to the next tag or newline), as real OFX files mix both.</summary>
    private static string? Tag(string block, string tag)
    {
        var open = "<" + tag + ">";
        var s = block.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (s < 0) return null;
        s += open.Length;
        var end = s;
        while (end < block.Length && block[end] != '<' && block[end] != '\r' && block[end] != '\n') end++;
        return block.Substring(s, end - s).Trim();
    }

    private static bool TryOfxDate(string raw, out DateOnly date)
    {
        date = default;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return false;
        return DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // ---- QIF ----

    public static IReadOnlyList<ImportedTransaction> ParseQif(string text)
    {
        var list = new List<ImportedTransaction>();
        DateOnly? date = null; decimal? amount = null; string payee = "", memo = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            switch (line[0])
            {
                case 'D': if (TryLooseDate(line[1..].Trim(), out var d)) date = d; break;
                case 'T' or 'U': if (TryAmount(line[1..], out var a)) amount = a; break;
                case 'P': payee = line[1..].Trim(); break;
                case 'M': memo = line[1..].Trim(); break;
                case '^':
                    if (date is { } dd && amount is { } aa)
                        list.Add(new ImportedTransaction(dd, aa, Clean(payee.Length > 0 ? payee : memo)));
                    date = null; amount = null; payee = ""; memo = ""; break;
            }
        }
        return list;
    }

    // ---- CSV ----

    /// <summary>Split CSV/TSV text into a header row + data rows, honouring quoted fields and auto-detecting the
    /// delimiter (comma, semicolon or tab). Returns an empty header when there are no rows.</summary>
    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) ReadCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return ([], []);
        var delimiter = DetectDelimiter(lines[0]);
        var all = lines.Select(l => SplitCsvLine(l, delimiter)).ToList();
        return (all[0], all.Skip(1).ToList());
    }

    private static char DetectDelimiter(string header)
    {
        int c(char ch) => header.Count(x => x == ch);
        var tab = c('\t'); var semi = c(';'); var comma = c(',');
        return tab >= semi && tab >= comma && tab > 0 ? '\t' : semi > comma ? ';' : ',';
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }

    /// <summary>Turn CSV data rows into transactions using the caller-chosen column indices. Amount can come from one
    /// signed column, or from separate debit/credit columns (debit = out, credit = in). Rows that don't parse are
    /// skipped rather than throwing, so one bad line never fails a whole import.</summary>
    public static IReadOnlyList<ImportedTransaction> ParseCsv(IReadOnlyList<IReadOnlyList<string>> rows,
        int datecol, int desccol, int amountcol, int? debitcol, int? creditcol)
    {
        var list = new List<ImportedTransaction>();
        foreach (var r in rows)
        {
            string? Col(int? i) => i is { } idx && idx >= 0 && idx < r.Count ? r[idx] : null;
            if (!TryLooseDate((Col(datecol) ?? "").Trim(), out var date)) continue;

            decimal amount;
            if (debitcol is not null || creditcol is not null)
            {
                var debit = TryAmount(Col(debitcol) ?? "", out var dv) ? Math.Abs(dv) : 0m;
                var credit = TryAmount(Col(creditcol) ?? "", out var cv) ? Math.Abs(cv) : 0m;
                amount = credit - debit;
            }
            else if (!TryAmount(Col(amountcol) ?? "", out amount)) continue;
            if (amount == 0m) continue;

            list.Add(new ImportedTransaction(date, amount, Clean(Col(desccol) ?? "")));
        }
        return list;
    }

    // ---- shared helpers ----

    /// <summary>Parse a money string tolerant of currency symbols, spaces, thousands separators, both decimal
    /// conventions (1,234.56 and 1.234,56) and parenthesised negatives.</summary>
    public static bool TryAmount(string raw, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')') || s.Contains('-');
        var cleaned = new string(s.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
        if (cleaned.Length == 0) return false;

        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Whichever separator is last is the decimal point; the other is a thousands separator.
            var decimalSep = lastDot > lastComma ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            cleaned = cleaned.Replace(thousandsSep.ToString(), "").Replace(decimalSep, '.');
        }
        else if (lastComma >= 0)
        {
            // Only commas: treat as decimal if it looks like one (e.g. "12,50"), else thousands.
            cleaned = cleaned.Length - lastComma - 1 <= 2 && cleaned.Count(c => c == ',') == 1
                ? cleaned.Replace(',', '.') : cleaned.Replace(",", "");
        }
        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out amount)) return false;
        amount = Math.Abs(amount);
        if (negative) amount = -amount;
        return true;
    }

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd.MM.yyyy", "dd-MM-yyyy",
        "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy", "yyyyMMdd", "dd/MM/yy", "MM/dd/yy",
    };

    /// <summary>Parse a date across the common bank formats. Handles a trailing time component (e.g. Revolut's
    /// "2026-07-01 12:09:37") by keeping only the date part. ISO and dd/MM are tried before MM/dd, so European
    /// statements read correctly; genuinely ambiguous values are shown to the user in the review step.</summary>
    public static bool TryLooseDate(string raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Replace("'", "/");
        // Drop a trailing time part ("2026-07-01 12:09:37" or "2026-07-01T12:09:37") — statements often include it.
        var sep = s.IndexOfAny([' ', 'T', 't']);
        var datePart = sep > 0 ? s[..sep] : s;
        foreach (var f in DateFormats)
            if (DateOnly.TryParseExact(datePart, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateOnly.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        // Last resort: parse the whole thing as a datetime and take the date.
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) { date = DateOnly.FromDateTime(dt); return true; }
        return false;
    }

    private static string Clean(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
