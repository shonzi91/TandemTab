package com.tandemtab.app.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.data.BankFileParser
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.data.FundOptionDto
import com.tandemtab.app.data.ImportRowDto
import com.tandemtab.app.data.ImportedTransaction
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons
import java.time.LocalDate
import java.time.format.DateTimeFormatter

/** Where the import has got to. Three steps because the file answers three separate questions, and a statement
 *  whose columns were guessed wrong must be correctable before anything is posted. */
private enum class ImportStep { Pick, Map, Review }

/** One row on the review list: what was parsed, where it will be filed, and whether it is going at all. */
private data class ReviewRow(
    val tx: ImportedTransaction,
    val categoryId: String,
    val include: Boolean,
)

/**
 * Statement import on the phone — the second of R2's two stated lags.
 *
 * ★ **The file never leaves the device.** It is read through the system file picker, parsed here by
 * [BankFileParser], and only the rows the user reviewed and kept are posted. That is not an implementation detail:
 * a statement holds the account's balance, its number, and every transaction on it including the ones being
 * unticked. Uploading it to parse server-side would be less code and would quietly break the promise the product
 * is sold on.
 *
 * ⚠️ **Rows dated outside the open period are dropped, and the count is shown.** Without it, importing a
 * three-month statement files ninety days of spending into one month — the server posts what it is given and has
 * no opinion about the dates, so this guard only exists if the client writes it. The web does the same.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ImportSheet(
    currency: String,
    funds: List<FundOptionDto>,
    categories: List<CategoryOptionDto>,
    incomeCategories: List<CategoryOptionDto>,
    // The open period's bounds, ISO. Null means no open period — nothing can be imported into it.
    periodFrom: String?,
    periodTo: String?,
    saving: Boolean,
    saveError: String?,
    onDismiss: () -> Unit,
    onImport: (rows: List<ImportRowDto>, skipDuplicates: Boolean, onDone: (Int, Int) -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val context = LocalContext.current

    var step by remember { mutableStateOf(ImportStep.Pick) }
    var fileName by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var done by remember { mutableStateOf<String?>(null) }

    // CSV mapping state.
    var headers by remember { mutableStateOf<List<String>>(emptyList()) }
    var dataRows by remember { mutableStateOf<List<List<String>>>(emptyList()) }
    var dateCol by remember { mutableStateOf(0) }
    var descCol by remember { mutableStateOf(1) }
    var amountCol by remember { mutableStateOf(-1) }
    var debitCol by remember { mutableStateOf<Int?>(null) }
    var creditCol by remember { mutableStateOf<Int?>(null) }

    var rows by remember { mutableStateOf<List<ReviewRow>>(emptyList()) }
    var outOfPeriod by remember { mutableStateOf(0) }
    var fundId by remember { mutableStateOf(funds.firstOrNull { !it.synced }?.id ?: funds.firstOrNull()?.id) }
    var skipDuplicates by remember { mutableStateOf(true) }

    val defaultExpense = categories.firstOrNull()?.id
    val defaultIncome = incomeCategories.firstOrNull()?.id
    val from = remember(periodFrom) { periodFrom?.let { runCatching { LocalDate.parse(it) }.getOrNull() } }
    val to = remember(periodTo) { periodTo?.let { runCatching { LocalDate.parse(it) }.getOrNull() } }

    /** Filter to the open period, order by date, and file each row somewhere sensible to start from. */
    fun buildReview(txns: List<ImportedTransaction>) {
        val inPeriod = txns
            .filter { from == null || to == null || (!it.date.isBefore(from) && !it.date.isAfter(to)) }
            .sortedBy { it.date }
        outOfPeriod = txns.size - inPeriod.size
        rows = inPeriod.map {
            ReviewRow(
                tx = it,
                // Signed: an expense needs a spend category, income needs a contribution one. Getting this pair
                // wrong is a 400 for the WHOLE batch, so the default has to respect the sign from the start.
                categoryId = (if (it.amount < 0) defaultExpense else defaultIncome).orEmpty(),
                include = true,
            )
        }
        error = when {
            rows.isNotEmpty() -> null
            outOfPeriod > 0 -> "All $outOfPeriod transactions are dated outside this period — nothing to import here."
            else -> "No transactions found in that file."
        }
        step = ImportStep.Review
    }

    fun applyMapping() {
        val hasSplit = debitCol != null || creditCol != null
        buildReview(
            BankFileParser.parseCsv(
                dataRows, dateCol, descCol,
                amountCol = if (hasSplit) -1 else maxOf(0, amountCol),
                debitCol = if (hasSplit) debitCol else null,
                creditCol = if (hasSplit) creditCol else null,
            ),
        )
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        error = null
        val text = runCatching {
            context.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }
        }.getOrNull()
        if (text.isNullOrBlank()) {
            error = "That file is empty, or it couldn't be opened."
            return@rememberLauncherForActivityResult
        }
        fileName = uri.lastPathSegment?.substringAfterLast('/')
        when (val format = BankFileParser.detect(fileName, text)) {
            BankFileParser.Format.CSV -> {
                val (h, r) = BankFileParser.readCsv(text)
                if (r.isEmpty()) {
                    error = "That file has a header but no rows."
                } else {
                    headers = h
                    dataRows = r
                    val guess = BankFileParser.guessColumns(h)
                    dateCol = guess.date; descCol = guess.description
                    debitCol = guess.debit; creditCol = guess.credit
                    // A split debit/credit pair wins over a single Amount column: statements that have both often
                    // carry an empty "amount in transfer currency" that would import every row as zero.
                    amountCol = if (guess.debit != null || guess.credit != null) -1 else maxOf(0, guess.amount)
                    step = ImportStep.Map
                }
            }
            BankFileParser.Format.OFX -> buildReview(BankFileParser.parseOfx(text))
            BankFileParser.Format.QIF -> buildReview(BankFileParser.parseQif(text))
            // Recognised, but this client cannot read them. Said plainly — an empty review list would read as the
            // app being broken, and the web CAN open these, so "use the web for this one" is real advice.
            BankFileParser.Format.XML, BankFileParser.Format.HTML ->
                error = "That looks like a ${if (format == BankFileParser.Format.XML) "bank XML" else "bank HTML"} " +
                    "export. The phone reads CSV, OFX and QIF — open this one on the web app, or ask your bank for CSV."
            BankFileParser.Format.UNKNOWN ->
                error = "That doesn't look like a statement. Most banks offer a CSV download."
        }
    }

    val included = rows.count { it.include && it.categoryId.isNotBlank() }

    SheetScaffold(
        title = "Import a statement",
        saving = saving,
        canSave = step == ImportStep.Review && included > 0 && fundId != null && !saving,
        onDismiss = onDismiss,
        onSave = {
            val f = fundId ?: return@SheetScaffold
            val payload = rows.filter { it.include && it.categoryId.isNotBlank() }.map {
                ImportRowDto(
                    amount = it.tx.amount,
                    date = it.tx.date.toString(),
                    categoryId = it.categoryId,
                    fundId = f,
                    note = it.tx.description.ifBlank { null },
                )
            }
            onImport(payload, skipDuplicates) { imported, duplicates ->
                // All three numbers are reported. "Imported 12" out of 20 leaves eight rows unaccounted for, and
                // the eight are exactly what somebody would go looking for afterwards.
                done = buildString {
                    append("Imported $imported row${if (imported == 1) "" else "s"}")
                    if (duplicates > 0) append(", skipped $duplicates already here")
                    append(".")
                }
                rows = emptyList()
            }
        },
        sheetState = sheetState,
        saveLabel = if (included > 0) "Import $included" else "Import",
    ) {
        done?.let {
            Text(it, color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(10.dp))
            TextButton(onClick = onDismiss) { Text("Done") }
            return@SheetScaffold
        }

        when (step) {
            ImportStep.Pick -> {
                Text(
                    "Download a statement from your bank and open it here. CSV, OFX and QIF.",
                    fontSize = 13.sp, color = tandem.muted,
                )
                Spacer(Modifier.height(12.dp))
                // The claim that earns this feature its place, stated where the choice is made.
                Text(
                    "The file is read on this phone. Only the rows you keep are sent — the statement itself never " +
                        "leaves the device.",
                    fontSize = 12.sp, color = tandem.muted,
                )
                Spacer(Modifier.height(16.dp))
                OutlinedButton(
                    onClick = { picker.launch(arrayOf("*/*")) },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Choose file") }
                if (from == null || to == null) {
                    Spacer(Modifier.height(12.dp))
                    Text(
                        "There's no open period to import into. Start one first.",
                        fontSize = 12.sp, color = MaterialTheme.colorScheme.error,
                    )
                }
            }

            ImportStep.Map -> {
                Text(fileName ?: "Your file", fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(2.dp))
                Text(
                    "${dataRows.size} rows. Check the columns — a wrong guess here imports the wrong numbers.",
                    fontSize = 12.sp, color = tandem.muted,
                )
                Spacer(Modifier.height(14.dp))

                ColumnPicker("Date", headers, dateCol) { dateCol = it }
                Spacer(Modifier.height(10.dp))
                ColumnPicker("Description", headers, descCol) { descCol = it }
                Spacer(Modifier.height(10.dp))

                val hasSplit = debitCol != null || creditCol != null
                if (hasSplit) {
                    // Two columns, one for money out and one for money in — how most European exports do it.
                    ColumnPicker("Money out", headers, debitCol ?: -1, allowNone = true) { debitCol = it.takeIf { i -> i >= 0 } }
                    Spacer(Modifier.height(10.dp))
                    ColumnPicker("Money in", headers, creditCol ?: -1, allowNone = true) { creditCol = it.takeIf { i -> i >= 0 } }
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = { debitCol = null; creditCol = null; amountCol = 0 }) {
                        Text("One signed amount column instead")
                    }
                } else {
                    ColumnPicker("Amount", headers, amountCol) { amountCol = it }
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = { debitCol = 0; creditCol = 0; amountCol = -1 }) {
                        Text("Separate money-out and money-in columns")
                    }
                }

                Spacer(Modifier.height(14.dp))
                OutlinedButton(onClick = { applyMapping() }, modifier = Modifier.fillMaxWidth()) {
                    Text("Read the rows")
                }
            }

            ImportStep.Review -> {
                if (rows.isEmpty()) {
                    Text(error ?: "Nothing to import.", color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    Spacer(Modifier.height(10.dp))
                    TextButton(onClick = { step = ImportStep.Pick; error = null }) { Text("Choose another file") }
                    return@SheetScaffold
                }

                Text(
                    "$included of ${rows.size} rows will import into this period." +
                        if (outOfPeriod > 0) " ($outOfPeriod dated outside it were left out.)" else "",
                    fontSize = 12.sp, color = tandem.muted,
                )
                Spacer(Modifier.height(12.dp))

                FieldLabel("Into which wallet")
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    funds.filter { !it.synced }.forEach { f ->
                        PickChip(label = f.name, icon = null, selected = fundId == f.id, onClick = { fundId = f.id })
                    }
                }

                Spacer(Modifier.height(12.dp))
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("Skip rows already here", fontSize = 14.sp)
                        Text(
                            "Same date, amount and wallet. Makes re-importing the same statement safe.",
                            fontSize = 11.sp, color = tandem.muted,
                        )
                    }
                    Switch(checked = skipDuplicates, onCheckedChange = { skipDuplicates = it })
                }

                Spacer(Modifier.height(6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TextButton(onClick = { rows = rows.map { it.copy(include = true) } }) { Text("All") }
                    TextButton(onClick = { rows = rows.map { it.copy(include = false) } }) { Text("None") }
                }

                rows.forEachIndexed { i, r ->
                    ImportReviewRow(
                        row = r,
                        currency = currency,
                        options = if (r.tx.amount < 0) categories else incomeCategories,
                        onToggle = { rows = rows.toMutableList().also { l -> l[i] = r.copy(include = !r.include) } },
                        onCategory = { id -> rows = rows.toMutableList().also { l -> l[i] = r.copy(categoryId = id) } },
                    )
                }
            }
        }

        Hints(error.takeIf { rows.isNotEmpty() || step != ImportStep.Review }, saveError)
    }
}

/** One header choice. [allowNone] adds a "—" for the debit/credit pair, where one half may genuinely be absent. */
@Composable
private fun ColumnPicker(label: String, headers: List<String>, selected: Int, allowNone: Boolean = false, onPick: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val tandem = LocalTandemColors.current
    Column(Modifier.fillMaxWidth()) {
        FieldLabel(label)
        Box {
            Row(
                Modifier.fillMaxWidth().clickable { open = true }.padding(vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    headers.getOrNull(selected) ?: "—",
                    modifier = Modifier.weight(1f),
                    color = if (selected >= 0) MaterialTheme.colorScheme.onSurface else tandem.muted,
                    fontSize = 14.sp,
                )
                Icon(TandemIcons.Chevron, contentDescription = null, tint = tandem.muted, modifier = Modifier.size(18.dp))
            }
            DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                if (allowNone) {
                    DropdownMenuItem(text = { Text("—") }, onClick = { onPick(-1); open = false })
                }
                headers.forEachIndexed { i, h ->
                    DropdownMenuItem(
                        text = { Text(h.ifBlank { "Column ${i + 1}" }) },
                        onClick = { onPick(i); open = false },
                    )
                }
            }
        }
    }
}

@Composable
private fun ImportReviewRow(
    row: ReviewRow,
    currency: String,
    options: List<CategoryOptionDto>,
    onToggle: () -> Unit,
    onCategory: (String) -> Unit,
) {
    val tandem = LocalTandemColors.current
    var open by remember { mutableStateOf(false) }
    val isExpense = row.tx.amount < 0
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = row.include, onCheckedChange = { onToggle() })
        Column(Modifier.weight(1f)) {
            Text(
                row.tx.description.ifBlank { if (isExpense) "Payment" else "Money in" },
                fontSize = 14.sp, maxLines = 1,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    row.tx.date.format(DateTimeFormatter.ofPattern("d MMM")),
                    fontSize = 11.sp, color = tandem.muted,
                )
                Spacer(Modifier.width(8.dp))
                Box {
                    Text(
                        options.firstOrNull { it.id == row.categoryId }?.name ?: "Pick a category",
                        fontSize = 11.sp,
                        color = if (row.categoryId.isBlank()) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                        modifier = Modifier.clickable { open = true },
                    )
                    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                        options.forEach { c ->
                            DropdownMenuItem(text = { Text(c.name) }, onClick = { onCategory(c.id); open = false })
                        }
                    }
                }
            }
        }
        Text(
            (if (isExpense) "−" else "+") + currencySymbol(currency) + trimAmount(kotlin.math.abs(row.tx.amount)),
            fontSize = 14.sp,
            fontWeight = FontWeight.Bold,
            color = if (isExpense) tandem.spent else MaterialTheme.colorScheme.primary,
        )
    }
}
