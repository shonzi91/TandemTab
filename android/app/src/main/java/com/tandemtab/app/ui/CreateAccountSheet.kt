package com.tandemtab.app.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.util.Currency
import java.util.Locale

/**
 * Create a new budget account — name + currency. For a phone-only user this is the ONLY route to a first account:
 * registration makes the user, not an account, so without this a fresh sign-up lands on an empty list with no exit.
 *
 * Currency is chosen once and is effectively permanent (it stamps every figure), so it's a deliberate picker, not a
 * free-text field. The device's own currency leads the list; a short common set follows. The server seeds the
 * starter categories/funds/period on bootstrap, so this sheet asks for nothing else.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CreateAccountSheet(
    busy: Boolean,
    error: String?,
    onDismiss: () -> Unit,
    onCreate: (name: String, currency: String, onDone: () -> Unit) -> Unit,
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val tandem = LocalTandemColors.current

    // Device currency first (deduped), then a common set — enough for the vast majority without a full ISO list.
    val deviceCurrency = remember { runCatching { Currency.getInstance(Locale.getDefault()).currencyCode }.getOrNull() }
    val currencies = remember(deviceCurrency) {
        (listOfNotNull(deviceCurrency) + listOf("EUR", "USD", "GBP", "BGN", "CHF", "CAD", "AUD", "JPY")).distinct()
    }

    var name by remember { mutableStateOf("") }
    var currency by remember { mutableStateOf(deviceCurrency ?: "EUR") }
    var hint by remember { mutableStateOf<String?>(null) }

    val canSave = !busy && name.isNotBlank()

    SheetScaffold(
        title = "New account",
        saving = busy,
        canSave = canSave,
        onDismiss = onDismiss,
        onSave = {
            if (name.isBlank()) hint = "Give the account a name."
            else onCreate(name.trim(), currency) { onDismiss() }
        },
        sheetState = sheetState,
        saveLabel = "Create",
    ) {
        OutlinedTextField(
            value = name,
            onValueChange = { name = it; hint = null },
            label = { Text("Name") },
            placeholder = { Text("e.g. Household") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(14.dp))

        FieldLabel("Currency")
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            currencies.forEach { code ->
                val symbol = currencySymbol(code)
                PickChip(label = if (symbol == code) code else "$code  $symbol", icon = null, selected = currency == code) { currency = code }
            }
        }
        Spacer(Modifier.height(8.dp))
        Text(
            "Every amount in this account is in this currency, and it can't be changed later — so pick the one you " +
                "actually budget in.",
            color = tandem.muted, fontSize = 12.sp,
        )
        Spacer(Modifier.height(2.dp))
        Text(
            "We'll set up starter categories, wallets and this month for you — you can change all of it after.",
            color = tandem.muted, fontSize = 12.sp, fontWeight = FontWeight.Normal,
        )

        Hints(hint, error)
    }
}
