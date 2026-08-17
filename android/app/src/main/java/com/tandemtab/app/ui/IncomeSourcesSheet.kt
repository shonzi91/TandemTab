package com.tandemtab.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.SpendingUi
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * "Manage income sources" — the contribution categories (Salary, Vouchers, …) the Income tab files deposits under.
 *
 * They could be created from the add sheet and never corrected: a typo in "Salray" was permanent, and a source
 * added by mistake stayed in the picker for good. Rename and remove close that.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ManageIncomeSourcesSheet(
    spending: SpendingUi,
    onEdit: (id: String, name: String, icon: String?, onDone: () -> Unit) -> Unit,
    onDelete: (id: String, onDone: () -> Unit) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var editing by remember { mutableStateOf<CategoryOptionDto?>(null) }
    var confirmDelete by remember { mutableStateOf<CategoryOptionDto?>(null) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(
                "Manage income sources", fontSize = 20.sp, fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 2.dp),
            )
            Text(
                "Where money comes from — salary, a refund, vouchers. New ones are made while recording income; " +
                    "this is where they get corrected.",
                color = tandem.muted, fontSize = 13.sp,
            )

            if (spending.incomeCategories.isEmpty()) {
                Text("No sources yet — add one while recording income.", color = tandem.muted, fontSize = 13.sp)
            } else {
                spending.incomeCategories.forEach { c ->
                    Row(
                        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable { editing = c }.padding(vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        CatIcon(c.icon, c.name)
                        Spacer(Modifier.width(10.dp))
                        Text(c.name, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
                        Icon(TandemIcons.Pencil, contentDescription = "Edit ${c.name}", tint = tandem.muted, modifier = Modifier.size(17.dp))
                        Icon(
                            TandemIcons.Trash, contentDescription = "Remove ${c.name}", tint = tandem.spent,
                            modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable { confirmDelete = c }.padding(6.dp).size(17.dp),
                        )
                    }
                }
            }

            spending.saveError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp) }
            Spacer(Modifier.height(4.dp))
            TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Done") }
        }
    }

    editing?.let { c ->
        var name by remember(c) { mutableStateOf(c.name) }
        AlertDialog(
            onDismissRequest = { if (!spending.saving) editing = null },
            title = { Text("Rename source") },
            text = {
                Column {
                    OutlinedTextField(
                        value = name, onValueChange = { name = it },
                        label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    )
                    spending.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    // The icon is passed back untouched: the edit is a full replace, so dropping it here would
                    // strip the emoji off every source anyone renames.
                    onClick = { onEdit(c.id, name, c.icon) { editing = null } },
                    enabled = !spending.saving && name.isNotBlank(),
                ) {
                    if (spending.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    else Text("Save")
                }
            },
            dismissButton = { TextButton(onClick = { editing = null }, enabled = !spending.saving) { Text("Cancel") } },
        )
    }

    confirmDelete?.let { c ->
        AlertDialog(
            onDismissRequest = { if (!spending.saving) confirmDelete = null },
            title = { Text("Remove ${c.name}?") },
            text = {
                Column {
                    Text(
                        "It stops being offered when you record income. Deposits already filed under it keep their " +
                            "amount — they just stop naming where the money came from.",
                    )
                    spending.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = { onDelete(c.id) { confirmDelete = null } },
                    enabled = !spending.saving,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                ) {
                    if (spending.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                    else Text("Remove")
                }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = null }, enabled = !spending.saving) { Text("Cancel") } },
        )
    }
}
