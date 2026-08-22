package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.GoalsUi
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.SavingBucketDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.time.LocalDate

/**
 * Log a loan installment against a debt bucket — R2's last L row, and the switch that has had nowhere to land since
 * S91 ("I log each installment here"). One payment posts several linked expense rows (interest + principal) and, on
 * a payment-driven bucket, takes the principal off the balance. Mirrors the web's Modal.LogInstallment
 * (Dashboard.razor): amount · fund · loan budget category · a live interest/principal split.
 *
 * The split is previewed locally from the balance owed today × the loan's APR, computed exactly as the server's
 * Period.LogInstallment does (LoanForecast.MonthlyInterest = round(balance × APR/100/12, 2), capped at what the
 * payment services). It's a preview only: the post is server-authoritative and recomputes it, and the balance move
 * is the server's — so a back-dated payment (where the true owed-then differs from owed-today) still posts correctly.
 *
 * ⚠️ Extra lines (insurance/tax riding along) and the principal/interest tag split are web-only for now — Android has
 * no tag picker yet — so this posts the payment as interest + principal under one category. That is the remaining
 * installment parity gap; the trap (a live switch with nowhere to log) is what this closes.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LogInstallmentSheet(
    bucket: SavingBucketDto,
    goals: GoalsUi,
    spending: SpendingUi,
    onDismiss: () -> Unit,
    onLog: (bucketId: String, total: Double, fundId: String, date: String, categoryId: String, note: String?, onDone: () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val fmt = moneyFormatter(goals.currency)
    val cats = spending.categories
    val funds = spending.funds

    // Prefill the contractual installment — the common case is paying exactly it, so the user types nothing.
    val installment = bucket.forecast?.debtInstallment ?: 0.0
    var amountText by remember { mutableStateOf(installment.takeIf { it > 0 }?.let { trimAmount(it) }.orEmpty()) }
    // The fund the bucket is earmarked in is where its money is understood to live; else the first non-synced one.
    var fundId by remember(spending.loaded) {
        mutableStateOf(bucket.fundId ?: funds.firstOrNull { !it.synced }?.id ?: funds.firstOrNull()?.id)
    }
    var categoryId by remember(spending.loaded) { mutableStateOf(cats.firstOrNull()?.id) }
    var date by remember { mutableStateOf(LocalDate.now().toString()) }
    var hint by remember { mutableStateOf<String?>(null) }

    val total = amountText.replace(',', '.').toDoubleOrNull() ?: 0.0
    val rate = bucket.forecast?.debtRatePercent ?: 0.0
    val owed = bucket.debtBalance ?: 0.0
    val interestDue = if (owed > 0 && rate > 0) round2(owed * (rate / 100.0 / 12.0)) else 0.0
    val interest = minOf(interestDue, total).coerceAtLeast(0.0)
    val principal = (total - interest).coerceAtLeast(0.0)

    val canSave = !goals.saving && total > 0 && fundId != null && categoryId != null

    SheetScaffold(
        title = "Log installment — ${bucket.name}",
        saving = goals.saving,
        canSave = canSave,
        onDismiss = onDismiss,
        onSave = {
            val f = fundId
            val c = categoryId
            when {
                total <= 0 -> hint = "Enter the amount you paid."
                f == null -> hint = "Pick the fund you paid from."
                c == null -> hint = "Pick a budget category."
                else -> onLog(bucket.id, total, f, date, c, bucket.name) { onDismiss() }
            }
        },
        sheetState = sheetState,
        saveLabel = "Log it",
    ) {
        OutlinedTextField(
            value = amountText,
            onValueChange = { amountText = it; hint = null },
            label = { Text("Amount paid") },
            prefix = { Text(currencySymbol(goals.currency)) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Date")
        DateField(date) { date = it }
        Spacer(Modifier.height(14.dp))

        FieldLabel("Paid from")
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            funds.forEach { f ->
                PickChip(label = if (f.synced) "🏦 ${f.name}" else f.name, icon = null, selected = fundId == f.id) { fundId = f.id }
            }
        }
        Spacer(Modifier.height(14.dp))

        // Interest and principal share one budget category — they're the same line ("Loan"); on the web it's the
        // auto-applied tags that split them in the Breakdown. Android has no tags yet, so it's just the one category.
        FieldLabel("Budget category for the loan")
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            cats.forEach { c ->
                PickChip(label = c.name, icon = c.icon, catName = c.name, selected = categoryId == c.id) { categoryId = c.id }
            }
        }
        Spacer(Modifier.height(16.dp))

        // The split read-out — the whole point of the feature. Computed the same way the server posts it.
        Column(
            Modifier.fillMaxWidth().background(tandem.savingsTileBg, RoundedCornerShape(12.dp)).padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            SplitRow("Interest", fmt(interest), tandem.muted)
            SplitRow("Principal", fmt(principal), MaterialTheme.colorScheme.onSurface)
        }

        if (total > 0 && principal <= 0.0) {
            Spacer(Modifier.height(8.dp))
            Text("This payment doesn't cover the interest, so it clears no principal.", color = tandem.warn, fontSize = 12.sp)
        }
        Spacer(Modifier.height(8.dp))
        Text(
            "Posts one row per part so each lands in its own budget slice. The whole payment is money out, counted " +
                "once. On a payment-driven loan it also drops the balance by the principal.",
            color = tandem.muted, fontSize = 12.sp,
        )

        Hints(hint, goals.saveError)
    }
}

@Composable
private fun SplitRow(label: String, value: String, valueColor: Color) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f), color = LocalTandemColors.current.muted, fontSize = 13.sp)
        Text(value, fontWeight = FontWeight.Bold, color = valueColor, fontSize = 14.sp)
    }
}

private fun round2(v: Double): Double = kotlin.math.round(v * 100.0) / 100.0
