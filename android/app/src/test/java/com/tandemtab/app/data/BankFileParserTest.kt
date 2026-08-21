package com.tandemtab.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate

/**
 * The Kotlin parser against the SAME fixtures as `BankFileParserTests` on the C# side.
 *
 * ⚠️ That is the entire point of this file. Two parsers for one file format drift, and a statement parser drifts
 * into real money: a European date read as American files rent in April, and an amount whose thousands separator is
 * mistaken for a decimal point imports €1.23 instead of €1,234. Fixtures are copied verbatim rather than
 * paraphrased, so "the two agree" is something a test run can answer.
 *
 * The XML and HTML fixtures from the C# suite are deliberately absent — those formats are not ported. What IS
 * tested is that [BankFileParser.detect] still recognises them, because the UI has to say so out loud.
 */
class BankFileParserTest {

    @Test
    fun `ofx parses signed transactions with open and closed tags`() {
        val ofx = """
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260115120000<TRNAMT>-42.50<NAME>TESCO STORES</STMTTRN>
            <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260125</DTPOSTED><TRNAMT>2000.00</TRNAMT><NAME>SALARY</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
        """.trimIndent()
        val txns = BankFileParser.parseOfx(ofx)
        assertEquals(2, txns.size)
        assertEquals(LocalDate.of(2026, 1, 15), txns[0].date)
        assertEquals(-42.50, txns[0].amount, 0.0001)
        assertEquals("TESCO STORES", txns[0].description)
        assertEquals(2000.00, txns[1].amount, 0.0001)
        assertEquals("SALARY", txns[1].description)
    }

    @Test
    fun `qif parses records`() {
        val qif = "!Type:Bank\nD01/15/2026\nT-42.50\nPTesco\n^\nD01/25/2026\nT2000.00\nPSalary\nMMonthly pay\n^\n"
        val txns = BankFileParser.parseQif(qif)
        assertEquals(2, txns.size)
        assertEquals(-42.50, txns[0].amount, 0.0001)
        assertEquals("Tesco", txns[0].description)
        assertEquals(2000.00, txns[1].amount, 0.0001)
    }

    @Test
    fun `csv signed amount column with semicolons and european decimals`() {
        val csv = "Date;Description;Amount\n15/01/2026;Grocery Store;-42,50\n25/01/2026;Salary;2.000,00\n"
        val (headers, rows) = BankFileParser.readCsv(csv)
        assertEquals(listOf("Date", "Description", "Amount"), headers)
        val txns = BankFileParser.parseCsv(rows, dateCol = 0, descCol = 1, amountCol = 2)
        assertEquals(2, txns.size)
        assertEquals(LocalDate.of(2026, 1, 15), txns[0].date)
        assertEquals(-42.50, txns[0].amount, 0.0001)
        assertEquals(2000.00, txns[1].amount, 0.0001)
    }

    @Test
    fun `csv separate debit and credit columns`() {
        val csv = "Date,Description,Debit,Credit\n2026-01-15,Coffee,3.20,\n2026-01-20,Refund,,15.00\n"
        val (_, rows) = BankFileParser.readCsv(csv)
        val txns = BankFileParser.parseCsv(rows, dateCol = 0, descCol = 1, amountCol = -1, debitCol = 2, creditCol = 3)
        assertEquals(-3.20, txns[0].amount, 0.0001)   // debit → out
        assertEquals(15.00, txns[1].amount, 0.0001)   // credit → in
    }

    @Test
    fun `amount parsing handles both decimal conventions`() {
        assertEquals(1234.56, BankFileParser.tryAmount("1,234.56")!!, 0.0001)
        assertEquals(1234.56, BankFileParser.tryAmount("1.234,56")!!, 0.0001)
        assertEquals(-50.00, BankFileParser.tryAmount("(50.00)")!!, 0.0001)
        assertEquals(-12.50, BankFileParser.tryAmount("-12,50")!!, 0.0001)
        assertEquals(9.99, BankFileParser.tryAmount("$ 9.99")!!, 0.0001)
        assertNull(BankFileParser.tryAmount(""))
        assertNull(BankFileParser.tryAmount("n/a"))
    }

    @Test
    fun `revolut csv with a datetime and a completed date column`() {
        val csv =
            "Type,Product,Started Date,Completed Date,Description,Amount,Fee,Currency,State,Balance\n" +
                "Card Payment,Current,2026-06-29 17:12:50,2026-07-01 12:09:37,фантастико,-2.55,0.00,EUR,COMPLETED,131.66\n" +
                "Topup,Current,2026-07-03 21:42:49,2026-07-03 21:43:13,Top-up by *3337,200.00,0.00,EUR,COMPLETED,251.67\n"
        val (headers, rows) = BankFileParser.readCsv(csv)
        assertEquals("Completed Date", headers[3])
        val txns = BankFileParser.parseCsv(rows, dateCol = 3, descCol = 4, amountCol = 5)
        assertEquals(2, txns.size)
        assertEquals(LocalDate.of(2026, 7, 1), txns[0].date)
        assertEquals(-2.55, txns[0].amount, 0.0001)
        assertEquals("фантастико", txns[0].description)
        assertEquals(200.00, txns[1].amount, 0.0001)   // top-up = money in
    }

    @Test
    fun `date parsing tolerates a time component`() {
        assertEquals(LocalDate.of(2026, 7, 1), BankFileParser.tryLooseDate("2026-07-01 12:09:37"))
        assertEquals(LocalDate.of(2026, 7, 1), BankFileParser.tryLooseDate("2026-07-01T12:09:37"))
        assertEquals(LocalDate.of(2026, 1, 15), BankFileParser.tryLooseDate("15/01/2026 09:30"))
    }

    @Test
    fun `a european date is not read as an american one`() {
        // The single most expensive way for the two parsers to disagree: 03/04 is a real date under both readings,
        // so whichever format is tried first silently wins and nothing downstream looks wrong.
        assertEquals(LocalDate.of(2026, 4, 3), BankFileParser.tryLooseDate("03/04/2026"))
    }

    @Test
    fun `a row that does not parse is skipped rather than failing the file`() {
        // Banks put balance-carried-forward and subtotal lines in the middle of real rows.
        val csv = "Date,Description,Amount\n2026-01-15,Coffee,-3.20\nBalance carried forward,,\n2026-01-16,Bus,-1.50\n"
        val (_, rows) = BankFileParser.readCsv(csv)
        val txns = BankFileParser.parseCsv(rows, dateCol = 0, descCol = 1, amountCol = 2)
        assertEquals(2, txns.size)
        assertEquals("Bus", txns[1].description)
    }

    @Test
    fun `quoted fields keep their delimiters`() {
        val csv = "Date,Description,Amount\n2026-01-15,\"SHOP, THE\",-3.20\n"
        val (_, rows) = BankFileParser.readCsv(csv)
        assertEquals("SHOP, THE", rows[0][1])
    }

    @Test
    fun `detect recognises the formats this client cannot read`() {
        // Detected but unported. The UI must be able to say "not on the phone yet" instead of showing an empty
        // review list, which reads as the app being broken.
        val camt = "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.053.001.02\"><BkToCstmrStmt><Stmt>" +
            "<Ntry><Amt Ccy=\"EUR\">42.50</Amt></Ntry></Stmt></BkToCstmrStmt></Document>"
        assertEquals(BankFileParser.Format.XML, BankFileParser.detect("statement.xml", camt))
        assertTrue(!BankFileParser.isSupported(BankFileParser.Format.XML))

        val html = "<html><body><table><tr><td>x</td></tr></table></body></html>"
        assertEquals(BankFileParser.Format.HTML, BankFileParser.detect("export.xls", html))
        assertTrue(!BankFileParser.isSupported(BankFileParser.Format.HTML))
    }

    @Test
    fun `detect finds the supported formats`() {
        assertEquals(BankFileParser.Format.CSV, BankFileParser.detect("statement.csv", "Date,Amount\n"))
        assertEquals(BankFileParser.Format.QIF, BankFileParser.detect("x.qif", "!Type:Bank\n"))
        assertEquals(BankFileParser.Format.OFX, BankFileParser.detect("x.ofx", "<OFX><STMTTRN>"))
        assertTrue(BankFileParser.isSupported(BankFileParser.Format.CSV))
    }

    @Test
    fun `column guessing fills the mapper in for a common export`() {
        val (headers, _) = BankFileParser.readCsv("Date,Description,Debit,Credit\n2026-01-15,Coffee,3.20,\n")
        val guess = BankFileParser.guessColumns(headers)
        assertEquals(0, guess.date)
        assertEquals(1, guess.description)
        assertEquals(2, guess.debit)
        assertEquals(3, guess.credit)
    }
}
