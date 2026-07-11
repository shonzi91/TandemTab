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
    public void Camt053_xml_signs_from_credit_debit_indicator()
    {
        var xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02"><BkToCstmrStmt><Stmt>
              <Ntry><Amt Ccy="EUR">42.50</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-07-01</Dt></BookgDt>
                <NtryDtls><TxDtls><RmtInf><Ustrd>Tesco Stores</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry>
              <Ntry><Amt Ccy="EUR">2000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><BookgDt><Dt>2026-07-25</Dt></BookgDt>
                <NtryDtls><TxDtls><RmtInf><Ustrd>Salary</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry>
            </Stmt></BkToCstmrStmt></Document>
            """;
        Assert.Equal(BankFileParser.Format.Xml, BankFileParser.Detect("statement.xml", xml));
        var txns = BankFileParser.ParseXml(xml);
        Assert.Equal(2, txns.Count);
        Assert.Equal(new DateOnly(2026, 7, 1), txns[0].Date);
        Assert.Equal(-42.50m, txns[0].Amount);       // DBIT → out
        Assert.Equal("Tesco Stores", txns[0].Description);
        Assert.Equal(2000.00m, txns[1].Amount);       // CRDT → in
        Assert.Equal("Salary", txns[1].Description);
    }

    [Fact]
    public void Dais_ebank_xml_account_movements()
    {
        var xml =
            "<AccountMovements>" +
            "<AccountMovement><ValueDate>30.06.2026</ValueDate><Reason>ЕЛ ЕНЕРГИЯ</Reason><MovementType>Debit</MovementType><Amount>5,34</Amount></AccountMovement>" +
            "<AccountMovement><ValueDate>16.06.2026</ValueDate><Reason>НАЕМ</Reason><MovementType>Credit</MovementType><Amount>275,00</Amount></AccountMovement>" +
            "</AccountMovements>";
        Assert.Equal(BankFileParser.Format.Xml, BankFileParser.Detect("report.xml", xml));
        var t = BankFileParser.ParseXml(xml);
        Assert.Equal(2, t.Count);
        Assert.Equal(new DateOnly(2026, 6, 30), t[0].Date);
        Assert.Equal(-5.34m, t[0].Amount);           // Debit → out
        Assert.Equal("ЕЛ ЕНЕРГИЯ", t[0].Description);
        Assert.Equal(275.00m, t[1].Amount);          // Credit → in
    }

    [Fact]
    public void Html_table_export_named_xls_with_separate_debit_credit()
    {
        var html =
            "<html><body><table>" +
            "<tr><td colspan=\"10\">Движения по сметка</td></tr>" +
            "<tr><td><b>Дата</b></td><td><b>Основание</b></td><td><b>Дебит EUR</b></td><td><b>Кредит EUR</b></td></tr>" +
            "<tr><td>30.06.2026</td><td>ЕЛ ЕНЕРГИЯ</td><td>5,34</td><td/></tr>" +
            "<tr><td>16.06.2026</td><td>НАЕМ</td><td/><td>275,00</td></tr>" +
            "</table></body></html>";
        Assert.Equal(BankFileParser.Format.Html, BankFileParser.Detect("report.xls", html));
        var (headers, rows) = BankFileParser.ReadHtmlTable(html);
        Assert.Equal(new[] { "Дата", "Основание", "Дебит EUR", "Кредит EUR" }, headers);   // title banner skipped
        var t = BankFileParser.ParseCsv(rows, datecol: 0, desccol: 1, amountcol: -1, debitcol: 2, creditcol: 3);
        Assert.Equal(2, t.Count);
        Assert.Equal(-5.34m, t[0].Amount);
        Assert.Equal(275.00m, t[1].Amount);
    }

    [Fact]
    public void Csv_with_separate_debit_credit_columns_bulgarian()
    {
        var csv = "\"Дата\",\"Основание\",\"Дебит EUR\",\"Кредит EUR\"\n\"30.06.2026\",\"ЕЛ ЕНЕРГИЯ\",\"5,34\",\"\"\n\"16.06.2026\",\"НАЕМ\",\"\",\"1500,00\"\n";
        var (_, rows) = BankFileParser.ReadCsv(csv);
        var t = BankFileParser.ParseCsv(rows, datecol: 0, desccol: 1, amountcol: -1, debitcol: 2, creditcol: 3);
        Assert.Equal(-5.34m, t[0].Amount);
        Assert.Equal(1500.00m, t[1].Amount);
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
