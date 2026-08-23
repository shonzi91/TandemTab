package com.tandemtab.app.data

import java.math.BigDecimal
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException
import java.util.Locale

/**
 * One transaction parsed from a downloaded statement. [amount] is SIGNED: negative = money out (an expense),
 * positive = money in.
 */
data class ImportedTransaction(val date: LocalDate, val amount: Double, val description: String)

/**
 * Statement parsing, on the device.
 *
 * ⚠️ **This is a port of `FinApp.Contracts.BankFileParser`, and the two have to keep agreeing.** They exist twice
 * because the parse cannot move to the server: the privacy promise is that the statement FILE never leaves the
 * phone, and only the rows the user reviewed and kept are posted to /import. Sending the file up to reuse one
 * parser would trade the promise for the convenience — so the duplication is deliberate, and the mitigation is
 * that both halves are pinned by tests over the same fixtures (see BankFileParserTest).
 *
 * ⚠️ **A parser is not a place to improvise.** Every rule below matches the C# one deliberately, including the ones
 * that look arbitrary — which separator wins when a file has both, why dd/MM is tried before MM/dd, why a bad line
 * is skipped rather than thrown on. Where behaviour differs it is stated in a comment, not left to be discovered.
 *
 * Formats: CSV/TSV (with a column mapper), OFX/QFX and QIF. XML (ISO 20022 CAMT) and the HTML-table exports some
 * banks name ".xls" are NOT ported — [detect] recognises them so the UI can say so plainly rather than presenting
 * an empty review list, which is the failure that reads as "the app is broken".
 */
object BankFileParser {

    enum class Format { UNKNOWN, OFX, QIF, CSV, XML, HTML }

    /** True for the formats this client can actually read. XML/HTML are detected but unported — see the class note. */
    fun isSupported(format: Format) = format == Format.CSV || format == Format.OFX || format == Format.QIF

    /**
     * Guess the format from the content, then the file name. Content comes first for the ambiguous ones: some banks
     * name an HTML-table export ".xls" and an XML export ".xml"/".camt".
     */
    fun detect(fileName: String?, text: String): Format {
        val head = trimBom(text).trimStart()
        if (head.contains("<OFX", true) || head.contains("<STMTTRN", true)) return Format.OFX
        if (head.startsWith("<html", true) || head.contains("<table", true)) return Format.HTML
        if (head.contains("BkToCstmrStmt", true) || head.contains(":camt.", true) ||
            head.contains("<AccountMovement", true) ||
            (head.startsWith("<") && head.contains("<Ntry", true))
        ) return Format.XML

        val ext = (fileName ?: "").lowercase(Locale.ROOT)
        if (ext.endsWith(".ofx") || ext.endsWith(".qfx")) return Format.OFX
        if (ext.endsWith(".qif")) return Format.QIF
        if (ext.endsWith(".htm") || ext.endsWith(".html")) return Format.HTML
        if (ext.endsWith(".xml") || ext.endsWith(".camt")) return Format.XML
        if (ext.endsWith(".csv") || ext.endsWith(".tsv") || ext.endsWith(".txt")) return Format.CSV
        if (head.startsWith("!Type:", true)) return Format.QIF
        return if (text.contains(',') || text.contains(';') || text.contains('\t')) Format.CSV else Format.UNKNOWN
    }

    private fun trimBom(s: String) = if (s.isNotEmpty() && s[0] == '﻿') s.substring(1) else s

    // ---- OFX / QFX ----------------------------------------------------------------------------

    fun parseOfx(text: String): List<ImportedTransaction> {
        val list = mutableListOf<ImportedTransaction>()
        // Blocks are split on the opening tag; each piece then holds one transaction's fields.
        val blocks = text.split("<STMTTRN>", ignoreCase = true, limit = 0)
        for (i in 1 until blocks.size) {
            val block = blocks[i]
            val amountRaw = tag(block, "TRNAMT") ?: continue
            val dateRaw = tag(block, "DTPOSTED") ?: continue
            val amount = tryAmount(amountRaw) ?: continue
            val date = tryOfxDate(dateRaw) ?: continue
            val desc = tag(block, "NAME") ?: tag(block, "MEMO") ?: ""
            list.add(ImportedTransaction(date, amount, clean(desc)))
        }
        return list
    }

    /**
     * Read an OFX/SGML tag value: works whether the tag is closed (`<T>v</T>`) or open (`<T>v` up to the next tag
     * or newline), because real OFX files mix both.
     */
    private fun tag(block: String, tag: String): String? {
        val open = "<$tag>"
        var s = block.indexOf(open, ignoreCase = true)
        if (s < 0) return null
        s += open.length
        var end = s
        while (end < block.length && block[end] != '<' && block[end] != '\r' && block[end] != '\n') end++
        return block.substring(s, end).trim()
    }

    private fun tryOfxDate(raw: String): LocalDate? {
        val digits = raw.filter { it.isDigit() }
        if (digits.length < 8) return null
        return try {
            LocalDate.parse(digits.substring(0, 8), DateTimeFormatter.ofPattern("yyyyMMdd", Locale.ROOT))
        } catch (_: DateTimeParseException) {
            null
        }
    }

    // ---- QIF ----------------------------------------------------------------------------------

    fun parseQif(text: String): List<ImportedTransaction> {
        val list = mutableListOf<ImportedTransaction>()
        var date: LocalDate? = null
        var amount: Double? = null
        var payee = ""
        var memo = ""
        for (rawLine in text.split('\n')) {
            val line = rawLine.trimEnd('\r')
            if (line.isEmpty()) continue
            when (line[0]) {
                'D' -> tryLooseDate(line.substring(1).trim())?.let { date = it }
                'T', 'U' -> tryAmount(line.substring(1))?.let { amount = it }
                'P' -> payee = line.substring(1).trim()
                'M' -> memo = line.substring(1).trim()
                '^' -> {
                    val d = date
                    val a = amount
                    if (d != null && a != null) {
                        list.add(ImportedTransaction(d, a, clean(if (payee.isNotEmpty()) payee else memo)))
                    }
                    date = null; amount = null; payee = ""; memo = ""
                }
            }
        }
        return list
    }

    // ---- CSV ----------------------------------------------------------------------------------

    /**
     * Split CSV/TSV text into a header row + data rows, honouring quoted fields and auto-detecting the delimiter
     * (comma, semicolon or tab). Empty header when there are no rows.
     */
    fun readCsv(text: String): Pair<List<String>, List<List<String>>> {
        val lines = text.replace("\r\n", "\n").split('\n').filter { it.isNotBlank() }
        if (lines.isEmpty()) return emptyList<String>() to emptyList()
        val delimiter = detectDelimiter(lines[0])
        val all = lines.map { splitCsvLine(it, delimiter) }
        return all[0] to all.drop(1)
    }

    private fun detectDelimiter(header: String): Char {
        val tab = header.count { it == '\t' }
        val semi = header.count { it == ';' }
        val comma = header.count { it == ',' }
        return if (tab >= semi && tab >= comma && tab > 0) '\t' else if (semi > comma) ';' else ','
    }

    private fun splitCsvLine(line: String, delimiter: Char): List<String> {
        val fields = mutableListOf<String>()
        val sb = StringBuilder()
        var inQuotes = false
        var i = 0
        while (i < line.length) {
            val ch = line[i]
            when {
                inQuotes -> when {
                    ch == '"' && i + 1 < line.length && line[i + 1] == '"' -> { sb.append('"'); i++ }
                    ch == '"' -> inQuotes = false
                    else -> sb.append(ch)
                }
                ch == '"' -> inQuotes = true
                ch == delimiter -> { fields.add(sb.toString().trim()); sb.clear() }
                else -> sb.append(ch)
            }
            i++
        }
        fields.add(sb.toString().trim())
        return fields
    }

    /**
     * Turn CSV data rows into transactions using the caller-chosen column indices. The amount can come from one
     * signed column, or from separate debit/credit columns (debit = out, credit = in).
     *
     * ⚠️ A row that does not parse is SKIPPED, not thrown on, so one bad line never fails a whole import — banks
     * put subtotal and balance-carried-forward rows in the middle of real ones.
     */
    fun parseCsv(
        rows: List<List<String>>,
        dateCol: Int,
        descCol: Int,
        amountCol: Int,
        debitCol: Int? = null,
        creditCol: Int? = null,
    ): List<ImportedTransaction> {
        val list = mutableListOf<ImportedTransaction>()
        for (r in rows) {
            fun col(i: Int?): String? = if (i != null && i >= 0 && i < r.size) r[i] else null
            val date = tryLooseDate((col(dateCol) ?: "").trim()) ?: continue

            val amount: Double
            if (debitCol != null || creditCol != null) {
                val debit = tryAmount(col(debitCol) ?: "")?.let { kotlin.math.abs(it) } ?: 0.0
                val credit = tryAmount(col(creditCol) ?: "")?.let { kotlin.math.abs(it) } ?: 0.0
                amount = credit - debit
            } else {
                amount = tryAmount(col(amountCol) ?: "") ?: continue
            }
            if (amount == 0.0) continue

            list.add(ImportedTransaction(date, amount, clean(col(descCol) ?: "")))
        }
        return list
    }

    // ---- shared helpers -----------------------------------------------------------------------

    /**
     * Parse a money string tolerant of currency symbols, spaces, thousands separators, both decimal conventions
     * (1,234.56 and 1.234,56) and parenthesised negatives. Null when it is not a number at all.
     */
    fun tryAmount(raw: String): Double? {
        if (raw.isBlank()) return null
        val s = raw.trim()
        val negative = (s.startsWith("(") && s.endsWith(")")) || s.contains('-')
        var cleaned = s.filter { it.isDigit() || it == '.' || it == ',' }
        if (cleaned.isEmpty()) return null

        val lastDot = cleaned.lastIndexOf('.')
        val lastComma = cleaned.lastIndexOf(',')
        if (lastDot >= 0 && lastComma >= 0) {
            // Whichever separator comes LAST is the decimal point; the other is a thousands separator. This is what
            // makes "1.234,56" and "1,234.56" both read as the same amount instead of one of them as 1.23.
            val decimalSep = if (lastDot > lastComma) '.' else ','
            val thousandsSep = if (decimalSep == '.') ',' else '.'
            cleaned = cleaned.replace(thousandsSep.toString(), "").replace(decimalSep, '.')
        } else if (lastComma >= 0) {
            // Only commas: a decimal if it looks like one ("12,50"), otherwise a thousands separator ("1,234").
            cleaned = if (cleaned.length - lastComma - 1 <= 2 && cleaned.count { it == ',' } == 1) {
                cleaned.replace(',', '.')
            } else {
                cleaned.replace(",", "")
            }
        }
        val parsed = cleaned.toBigDecimalOrNull() ?: return null
        val abs = parsed.abs()
        return if (negative) abs.negate().toDouble() else abs.toDouble()
    }

    private fun String.toBigDecimalOrNull(): BigDecimal? = try {
        BigDecimal(this)
    } catch (_: NumberFormatException) {
        null
    }

    // ⚠️ ORDER IS LOAD-BEARING. ISO and dd/MM come before MM/dd so European statements read correctly; 03/04 is a
    // real date under both, so whichever is tried first silently wins. Same list, same order, as the C# parser.
    private val dateFormats = listOf(
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd.MM.yyyy", "dd-MM-yyyy",
        "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy", "yyyyMMdd", "dd/MM/yy", "MM/dd/yy",
    )

    /**
     * Parse a date across the common bank formats, dropping a trailing time component (Revolut's
     * "2026-07-01 12:09:37"). Genuinely ambiguous values are shown to the user in the review step rather than
     * guessed at more cleverly here.
     */
    fun tryLooseDate(raw: String): LocalDate? {
        if (raw.isBlank()) return null
        val s = raw.trim().replace("'", "/")
        val sep = s.indexOfFirst { it == ' ' || it == 'T' || it == 't' }
        val datePart = if (sep > 0) s.substring(0, sep) else s
        for (f in dateFormats) {
            try {
                return LocalDate.parse(datePart, DateTimeFormatter.ofPattern(f, Locale.ROOT))
            } catch (_: DateTimeParseException) {
                // try the next one
            }
        }
        return try {
            LocalDate.parse(datePart)
        } catch (_: DateTimeParseException) {
            null
        }
    }

    /** Collapse runs of whitespace — bank descriptions are full of padding. */
    private fun clean(s: String) = s.split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ").trim()

    // ---- column guessing ----------------------------------------------------------------------

    /**
     * A first guess at which column is which, so the mapper opens already filled in for the common exports. Only a
     * guess: every one is user-changeable, because the cost of a wrong guess nobody can correct is a mis-imported
     * statement.
     */
    fun guessColumns(headers: List<String>): ColumnGuess {
        fun find(vararg words: String): Int = headers.indexOfFirst { h ->
            val t = h.lowercase(Locale.ROOT)
            words.any { t.contains(it) }
        }
        val debit = find("debit", "withdrawal", "money out", "paid out", "outflow")
        val credit = find("credit", "deposit", "money in", "paid in", "inflow")
        return ColumnGuess(
            date = find("date", "дата", "posted", "booking").coerceAtLeast(0),
            description = find("description", "narrative", "details", "payee", "reason", "merchant", "name")
                .let { if (it >= 0) it else 1.coerceAtMost(headers.lastIndex.coerceAtLeast(0)) },
            amount = find("amount", "sum", "value", "сума"),
            debit = debit.takeIf { it >= 0 },
            credit = credit.takeIf { it >= 0 },
        )
    }

    /** Which column holds what. When [debit]/[credit] are set they win over [amount] — see [parseCsv]. */
    data class ColumnGuess(
        val date: Int,
        val description: Int,
        val amount: Int,
        val debit: Int?,
        val credit: Int?,
    )
}
