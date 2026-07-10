using System.IO.Compression;
using System.Text;
using FinApp.Contracts;
using Xunit;

namespace FinApp.Persistence.Tests;

public class XlsxReaderTests
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // Build the minimum an .xlsx needs for XlsxReader: shared strings, one date cell-style, and a sheet.
    private static MemoryStream BuildWorkbook(int dateSerial)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            Add("xl/sharedStrings.xml",
                $"<sst xmlns=\"{Ns}\"><si><t>Date</t></si><si><t>Description</t></si><si><t>Amount</t></si><si><t>Grocery Store</t></si></sst>");
            Add("xl/styles.xml",
                $"<styleSheet xmlns=\"{Ns}\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>");
            Add("xl/worksheets/sheet1.xml",
                $"<worksheet xmlns=\"{Ns}\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>" +
                $"<row r=\"2\"><c r=\"A2\" s=\"1\"><v>{dateSerial}</v></c><c r=\"B2\" t=\"s\"><v>3</v></c><c r=\"C2\"><v>-42.5</v></c></row>" +
                "</sheetData></worksheet>");
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Reads_headers_shared_strings_amounts_and_converts_date_serials()
    {
        var serial = (new DateTime(2026, 1, 15) - new DateTime(1899, 12, 30)).Days;
        using var wb = BuildWorkbook(serial);

        var (headers, rows) = XlsxReader.Read(wb);

        Assert.Equal(new[] { "Date", "Description", "Amount" }, headers);
        Assert.Single(rows);
        Assert.Equal("2026-01-15", rows[0][0]);       // date serial → ISO date
        Assert.Equal("Grocery Store", rows[0][1]);    // shared string
        Assert.Equal("-42.5", rows[0][2]);            // number

        // And it flows through the same CSV pipeline into a transaction.
        var txns = BankFileParser.ParseCsv(rows, datecol: 0, desccol: 1, amountcol: 2, debitcol: null, creditcol: null);
        Assert.Single(txns);
        Assert.Equal(new DateOnly(2026, 1, 15), txns[0].Date);
        Assert.Equal(-42.5m, txns[0].Amount);
        Assert.Equal("Grocery Store", txns[0].Description);
    }
}
