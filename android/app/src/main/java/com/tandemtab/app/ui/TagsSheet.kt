package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tandemtab.app.TagsUi
import com.tandemtab.app.data.TagRowDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

/**
 * "Manage tags" — the native counterpart to the web's tag manager, reached from Spending's ⋯ menu exactly as it is
 * there. Lists every tag *including archived ones*, shows each one's F2 category binding on the row (a label that
 * silently changes the category on pick has to be visible from the list that owns it), and offers edit, archive or
 * restore, and remove.
 *
 * ⚠️ Its rows come from [TagsUi], not from the Spending picker's list — see that class for why.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ManageTagsSheet(
    tags: TagsUi,
    onLoad: () -> Unit,
    onAdd: (name: String, onDone: () -> Unit) -> Unit,
    onEdit: (id: String, name: String, icon: String?, categoryId: String?, onDone: () -> Unit) -> Unit,
    onSetArchived: (id: String, archived: Boolean) -> Unit,
    onDelete: (id: String, onDone: () -> Unit) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var editing by remember { mutableStateOf<TagRowDto?>(null) }
    var confirmDelete by remember { mutableStateOf<TagRowDto?>(null) }
    var newName by remember { mutableStateOf("") }

    LaunchedEffect(Unit) { onLoad() }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            val draft = editing
            if (draft != null) {
                TagEditor(
                    tag = draft,
                    tags = tags,
                    onSave = { name, categoryId ->
                        // The icon rides along untouched: the server's edit is a full replace, so dropping it here
                        // would strip the emoji off every tag anyone renames.
                        onEdit(draft.id, name, draft.icon, categoryId) { editing = null }
                    },
                    onCancel = { editing = null },
                )
                return@Column
            }

            Text(
                "Manage tags", fontSize = 20.sp, fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 2.dp),
            )
            Text(
                "Flat labels you attach to expenses alongside categories — to track things that cut across them " +
                    "(a trip, work, reimbursable).",
                color = tandem.muted, fontSize = 13.sp,
            )

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = newName, onValueChange = { newName = it },
                    label = { Text("New tag…") }, singleLine = true, modifier = Modifier.weight(1f),
                )
                Button(
                    onClick = { onAdd(newName) { newName = "" } },
                    enabled = !tags.saving && newName.isNotBlank(),
                ) {
                    if (tags.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                    else Icon(TandemIcons.Plus, contentDescription = "Add tag", modifier = Modifier.size(18.dp))
                }
            }

            tags.saveError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp) }

            when {
                tags.loading && tags.tags.isEmpty() ->
                    Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
                    }

                tags.error != null ->
                    Column(Modifier.fillMaxWidth().padding(vertical = 16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(tags.error, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                        Spacer(Modifier.height(6.dp))
                        Text("Tap to retry", color = tandem.positive, modifier = Modifier.clickable { onLoad() }.padding(8.dp))
                    }

                tags.tags.isEmpty() ->
                    Text(
                        "No tags yet — add one above, or from the tag box when logging an expense.",
                        color = tandem.muted, fontSize = 13.sp,
                    )

                else -> tags.tags.forEach { t ->
                    TagManageRow(
                        tag = t,
                        onEdit = { editing = t },
                        onToggleArchive = { onSetArchived(t.id, !t.archived) },
                        onDelete = { confirmDelete = t },
                    )
                }
            }

            Spacer(Modifier.height(4.dp))
            TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Done") }
        }
    }

    // Removing a tag is a HARD delete server-side — tagged expenses keep an id that no longer resolves — so it
    // always confirms, and the count is what makes that a real question rather than a formality.
    confirmDelete?.let { t ->
        AlertDialog(
            onDismissRequest = { if (!tags.saving) confirmDelete = null },
            title = { Text("Remove ${t.name}?") },
            text = {
                Column {
                    Text(
                        if (t.uses == 0) "This tag isn't on any expense yet, so removing it loses nothing."
                        else "${t.uses} ${if (t.uses == 1) "expense" else "expenses"} carry this tag. Removing it " +
                            "strips the label off them for good — archive it instead to hide it from the pickers " +
                            "while keeping its history.",
                    )
                    tags.saveError?.let {
                        Spacer(Modifier.height(10.dp))
                        Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = { onDelete(t.id) { confirmDelete = null } },
                    enabled = !tags.saving,
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),
                ) {
                    if (tags.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onError)
                    else Text("Remove")
                }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = null }, enabled = !tags.saving) { Text("Cancel") } },
        )
    }
}

@Composable
private fun TagManageRow(tag: TagRowDto, onEdit: () -> Unit, onToggleArchive: () -> Unit, onDelete: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable(onClick = onEdit).padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                TagChip(tag.name, tag.icon, dimmed = tag.archived)
                if (tag.tripTag) {
                    Spacer(Modifier.width(6.dp))
                    Icon(TandemIcons.Plane, contentDescription = "Trip label", tint = tandem.muted, modifier = Modifier.size(13.dp))
                }
            }
            // The F2 binding and the archived state both belong on the row, not only inside the editor.
            val sub = listOfNotNull(
                tag.categoryName?.let { "→ $it" },
                if (tag.archived) "archived" else null,
                if (tag.uses > 0) "${tag.uses} ${if (tag.uses == 1) "expense" else "expenses"}" else null,
            ).joinToString(" · ")
            if (sub.isNotEmpty()) {
                Spacer(Modifier.height(2.dp))
                Text(sub, color = tandem.muted, fontSize = 12.sp)
            }
        }
        Icon(
            if (tag.archived) TandemIcons.Rotate else TandemIcons.Archive,
            contentDescription = if (tag.archived) "Restore ${tag.name}" else "Archive ${tag.name}",
            tint = tandem.muted,
            modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = onToggleArchive).padding(6.dp).size(18.dp),
        )
        Icon(
            TandemIcons.Trash, contentDescription = "Remove ${tag.name}", tint = tandem.spent,
            modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = onDelete).padding(6.dp).size(18.dp),
        )
    }
}

/** The tag's own pill, matching the web's `.exp-tag` (and `.exp-tag-off` when archived). */
@Composable
private fun TagChip(name: String, icon: String?, dimmed: Boolean) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.clip(RoundedCornerShape(7.dp))
            .background(if (dimmed) MaterialTheme.colorScheme.surfaceVariant else tandem.savingsTileBg)
            .padding(horizontal = 8.dp, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (!icon.isNullOrBlank()) { Text(icon, fontSize = 12.sp); Spacer(Modifier.width(4.dp)) }
        Text(
            name,
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium,
            color = if (dimmed) tandem.muted else MaterialTheme.colorScheme.onSurface,
            textDecoration = if (dimmed) TextDecoration.LineThrough else null,
        )
    }
}

/** Name + the F2 "files into" binding — the same two fields the web's Edit tag modal carries. */
@Composable
private fun TagEditor(
    tag: TagRowDto,
    tags: TagsUi,
    onSave: (name: String, categoryId: String?) -> Unit,
    onCancel: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var name by remember(tag) { mutableStateOf(tag.name) }
    var categoryId by remember(tag) { mutableStateOf(tag.categoryId) }
    var catMenu by remember { mutableStateOf(false) }

    Text(
        "Edit tag", fontSize = 20.sp, fontWeight = FontWeight.Bold,
        color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 4.dp),
    )

    OutlinedTextField(
        value = name, onValueChange = { name = it },
        label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
    )

    Spacer(Modifier.height(2.dp))
    FieldLabel("FILES INTO")
    Text(
        "Applying this tag on a new expense pre-selects that category. It never re-files what you've already logged.",
        color = tandem.muted, fontSize = 12.sp,
    )
    Box {
        OutlinedButton(onClick = { catMenu = true }, modifier = Modifier.fillMaxWidth()) {
            Text(
                tags.categories.firstOrNull { it.id == categoryId }?.name ?: "— no category —",
                modifier = Modifier.weight(1f),
            )
            Icon(TandemIcons.Chevron, null, tint = tandem.muted, modifier = Modifier.size(16.dp))
        }
        DropdownMenu(expanded = catMenu, onDismissRequest = { catMenu = false }) {
            DropdownMenuItem(text = { Text("— no category —") }, onClick = { categoryId = null; catMenu = false })
            tags.categories.forEach { c ->
                DropdownMenuItem(
                    text = { Text("${c.icon.orEmpty()} ${c.name}".trim()) },
                    onClick = { categoryId = c.id; catMenu = false },
                )
            }
        }
    }

    tags.saveError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp)) }

    Spacer(Modifier.height(6.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        OutlinedButton(onClick = onCancel, enabled = !tags.saving, modifier = Modifier.weight(1f)) { Text("Cancel") }
        Button(
            onClick = { onSave(name, categoryId) },
            enabled = !tags.saving && name.isNotBlank(),
            modifier = Modifier.weight(1f),
        ) {
            if (tags.saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
            else Text("Save")
        }
    }
}
