using FinApp.Contracts;
using Xunit;

namespace FinApp.Persistence.Tests;

public class BankFileParserTests
{
    [Fact]
    public void Ofx_parses_signed_transactions_open_and_closed_tags()
    {
        var ofx = """
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260115120000<TRNAMT>-42.50<NAME>TESCO STORES</STMTTRN>
            <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260125</DTPOSTED><TRNAMT>2000.00</TRNAMT><NAME>SALARY</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var txns = BankFileParser.ParseOfx(ofx);
        Assert.Equal(2, txns.Count);
        Assert.Equal(new DateOnly(2026, 1, 15), txns[0].Date);
        Assert.Equal(-42.50m, txns[0].Amount);
        Assert.Equal("TESCO STORES", txns[0].Description);
        Assert.Equal(2000.00m, txns[1].Amount);
        Assert.Equal("SALARY", txns[1].Description);
    }

    [Fact]
    public void Qif_parses_records()
    {
        var qif = "!Type:Bank\nD01/15/2026\nT-42.50\nPTesco\n^\nD01/25/2026\nT2000.00\nPSalary\nMMonthly pay\n^\n";
        var txns = BankFileParser.ParseQif(qif);
        Assert.Equal(2, txns.Count);
        Assert.Equal(-42.50m, txns[0].Amount);
        Assert.Equal("Tesco", txns[0].Description);
        Assert.Equal(2000.00m, txns[1].Amount);
    }

    [Fact]
    public void Csv_signed_amount_column_with_semicolons_and_european_decimals()
    {
        var csv = "Date;Description;Amount\n15/01/2026;Grocery Store;-42,50\n25/01/2026;Salary;2.000,00\n";
        var (headers, rows) = BankFileParser.ReadCsv(csv);
        Assert.Equal(new[] { "Date", "Description", "Amount" }, headers);
        var txns = BankFileParser.ParseCsv(rows, datecol: 0, desccol: 1, amountcol: 2, debitcol: null, creditcol: null);
        Assert.Equal(2, txns.Count);
        Assert.Equal(new DateOnly(2026, 1, 15), txns[0].Date);
        Assert.Equal(-42.50m, txns[0].Amount);
        Assert.Equal(2000.00m, txns[1].Amount);
    }

    [Fact]
    public void Csv_separate_debit_and_credit_columns()
    {
        var csv = "Date,Description,Debit,Credit\n2026-01-15,Coffee,3.20,\n2026-01-20,Refund,,15.00\n";
        var (_, rows) = BankFileParser.ReadCsv(csv);
        var txns = BankFileParser.ParseCsv(rows, datecol: 0, desccol: 1, amountcol: -1, debitcol: 2, creditcol: 3);
        Assert.Equal(-3.20m, txns[0].Amount);   // debit → out
        Assert.Equal(15.00m, txns[1].Amount);   // credit → in
    }

    [Theory]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("(50.00)", -50.00)]
    [InlineData("-12,50", -12.50)]
    [InlineData("$ 9.99", 9.99)]
    public void Amount_parsing_handles_conventions(string raw, double expected)
    {
        Assert.True(BankFileParser.TryAmount(raw, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Fact]
    public void Revolut_csv_with_datetime_and_completed_date_column()
    {
        var csv =
            "Type,Product,Started Date,Completed Date,Description,Amount,Fee,Currency,State,Balance\n" +
            "Card Payment,Current,2026-06-29 17:12:50,2026-07-01 12:09:37,фантастико,-2.55,0.00,EUR,COMPLETED,131.66\n" +
            "Topup,Current,2026-07-03 21:42:49,2026-07-03 21:43:13,Top-up by *3337,200.00,0.00,EUR,COMPLETED,251.67\n";
        var (headers, rows) = BankFileParser.ReadCsv(csv);
        Assert.Equal("Completed Date", headers[3]);
        // Use the Completed Date column (index 3) — the datetime's time part must be tolerated.
        var txns = BankFileParser.ParseCsv(rows, datecol: 3, desccol: 4, amountcol: 5, debitcol: null, creditcol: null);
        Assert.Equal(2, txns.Count);
        Assert.Equal(new DateOnly(2026, 7, 1), txns[0].Date);
        Assert.Equal(-2.55m, txns[0].Amount);
        Assert.Equal("фантастико", txns[0].Description);
        Assert.Equal(200.00m, txns[1].Amount);   // top-up = money in
    }

    [Theory]
    [InlineData("2026-07-01 12:09:37", 2026, 7, 1)]
    [InlineData("2026-07-01T12:09:37", 2026, 7, 1)]
    [InlineData("15/01/2026 09:30", 2026, 1, 15)]
    public void Date_parsing_tolerates_a_time_component(string raw, int y, int m, int d)
    {
        Assert.True(BankFileParser.TryLooseDate(raw, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Fact]
    public void Detect_by_extension_and_content()
    {
        Assert.Equal(BankFileParser.Format.Ofx, BankFileParser.Detect("statement.ofx", ""));
        Assert.Equal(BankFileParser.Format.Qif, BankFileParser.Detect(null, "!Type:Bank\nD01/01/2026\n^"));
        Assert.Equal(BankFileParser.Format.Csv, BankFileParser.Detect("export.csv", ""));
        Assert.Equal(BankFileParser.Format.Ofx, BankFileParser.Detect(null, "<OFX><STMTTRN>"));
    }
}
