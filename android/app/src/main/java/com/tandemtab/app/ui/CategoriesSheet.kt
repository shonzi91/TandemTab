package com.tandemtab.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
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
import androidx.compose.material3.Button
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
import com.tandemtab.app.data.CategoryOptionDto
import com.tandemtab.app.ui.theme.LocalTandemColors
import com.tandemtab.app.ui.theme.TandemIcons

// A category being created (id == null) or edited in the sheet's editor.
private data class CatDraft(val id: String?, val name: String, val icon: String, val parentId: String?)

/**
 * "Manage categories" — the native counterpart to the web's category manager: list every category (top-level with
 * its sub-categories indented), edit a name/emoji or archive it, and add a new one (optionally nested). Mirrors the
 * web by keeping user emoji as-is and preferring archive over delete. Figures/rows come from /spending; each save
 * re-fetches so the list is always live.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ManageCategoriesSheet(
    categories: List<CategoryOptionDto>,
    saving: Boolean,
    saveError: String?,
    onAdd: (name: String, parentId: String?, icon: String?, onDone: (String?) -> Unit) -> Unit,
    onEdit: (id: String, name: String, icon: String?, onDone: () -> Unit) -> Unit,
    onArchive: (id: String, onDone: () -> Unit) -> Unit,
    onDismiss: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var editing by remember { mutableStateOf<CatDraft?>(null) }

    val tops = remember(categories) { categories.filter { it.parentId == null } }
    val subsByParent = remember(categories) { categories.filter { it.parentId != null }.groupBy { it.parentId } }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheetState, containerColor = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier.fillMaxWidth().imePadding().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            val draft = editing
            if (draft == null) {
                Text("Manage categories", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 2.dp))
                if (tops.isEmpty()) {
                    Text("No categories yet — add your first below.", color = tandem.muted, fontSize = 13.sp)
                }
                tops.forEach { c ->
                    CategoryManageRow(c.icon, c.name, indent = false) { editing = CatDraft(c.id, c.name, c.icon ?: "", null) }
                    subsByParent[c.id]?.forEach { s ->
                        CategoryManageRow(s.icon, s.name, indent = true) { editing = CatDraft(s.id, s.name, s.icon ?: "", s.parentId) }
                    }
                }
                Spacer(Modifier.height(4.dp))
                Button(onClick = { editing = CatDraft(null, "", "", null) }, modifier = Modifier.fillMaxWidth()) {
                    Icon(TandemIcons.Plus, null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("New category")
                }
                TextButton(onClick = onDismiss, modifier = Modifier.fillMaxWidth()) { Text("Done") }
            } else {
                CategoryEditor(
                    draft = draft,
                    parents = tops,
                    saving = saving,
                    saveError = saveError,
                    onSave = { name, parentId, icon ->
                        if (draft.id == null) onAdd(name, parentId, icon) { editing = null }
                        else onEdit(draft.id, name, icon) { editing = null }
                    },
                    onArchive = { draft.id?.let { id -> onArchive(id) { editing = null } } },
                    onCancel = { editing = null },
                )
            }
        }
    }
}

@Composable
private fun CategoryManageRow(icon: String?, name: String, indent: Boolean, onEdit: () -> Unit) {
    val tandem = LocalTandemColors.current
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable(onClick = onEdit)
            .padding(start = if (indent) 20.dp else 0.dp).padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CatIcon(icon, name); Spacer(Modifier.width(10.dp))
        Text(name, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1, fontWeight = if (indent) FontWeight.Normal else FontWeight.Medium)
        Icon(TandemIcons.Pencil, contentDescription = "Edit ${name}", tint = tandem.muted, modifier = Modifier.size(17.dp))
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CategoryEditor(
    draft: CatDraft,
    parents: List<CategoryOptionDto>,
    saving: Boolean,
    saveError: String?,
    onSave: (name: String, parentId: String?, icon: String?) -> Unit,
    onArchive: () -> Unit,
    onCancel: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    var name by remember(draft) { mutableStateOf(draft.name) }
    // The editor works in icon *names* now (matching the migrated data); null = "Auto" (guessed from the name).
    var iconName by remember(draft) { mutableStateOf(draft.icon.takeIf { it.isNotBlank() }?.let { CategoryIcons.effective(it, draft.name) }) }
    var parentId by remember(draft) { mutableStateOf(draft.parentId) }
    var parentMenu by remember { mutableStateOf(false) }
    val isNew = draft.id == null

    Text(if (isNew) "New category" else "Edit category", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.padding(top = 4.dp, bottom = 4.dp))

    OutlinedTextField(
        value = name, onValueChange = { name = it },
        label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
    )
    Spacer(Modifier.height(2.dp))
    Text("ICON", fontSize = 10.sp, letterSpacing = 1.1.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
    IconPalette(selected = iconName, name = name, onPick = { iconName = it })

    // Parent — only when creating (the edit endpoint doesn't move a category).
    if (isNew && parents.isNotEmpty()) {
        Spacer(Modifier.height(2.dp))
        Text("PARENT", fontSize = 10.sp, letterSpacing = 1.1.sp, fontWeight = FontWeight.Bold, color = tandem.muted)
        Box {
            OutlinedButton(onClick = { parentMenu = true }, modifier = Modifier.fillMaxWidth()) {
                Text(parents.firstOrNull { it.id == parentId }?.name ?: "Top level", modifier = Modifier.weight(1f))
                Icon(TandemIcons.Chevron, null, tint = tandem.muted, modifier = Modifier.size(16.dp))
            }
            DropdownMenu(expanded = parentMenu, onDismissRequest = { parentMenu = false }) {
                DropdownMenuItem(text = { Text("Top level") }, onClick = { parentId = null; parentMenu = false })
                parents.forEach { p ->
                    DropdownMenuItem(text = { Text("${p.icon.orEmpty()} ${p.name}".trim()) }, onClick = { parentId = p.id; parentMenu = false })
                }
            }
        }
    }

    saveError?.let { Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp)) }

    Spacer(Modifier.height(6.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        OutlinedButton(onClick = onCancel, enabled = !saving, modifier = Modifier.weight(1f)) { Text("Cancel") }
        Button(onClick = { onSave(name, parentId, iconName) }, enabled = !saving && name.isNotBlank(), modifier = Modifier.weight(1f)) {
            if (saving) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
            else Text(if (isNew) "Add" else "Save")
        }
    }
    if (!isNew) {
        TextButton(onClick = onArchive, enabled = !saving, modifier = Modifier.fillMaxWidth()) {
            Text("Archive category", color = tandem.spent)
        }
    }
}

/** A wrapping grid of the line-icon palette (plus an "Auto" chip that guesses from the name). Selected = highlighted.
 *  Shared with the fund editor — categories and funds draw from the same palette, so they should look identical. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun IconPalette(selected: String?, name: String, onPick: (String?) -> Unit) {
    val tandem = LocalTandemColors.current
    val autoName = CategoryIcons.guess(name)
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        // "Auto" — no stored icon; the app guesses from the name.
        IconChip(selectedState = selected == null, iconName = autoName, dimmed = true) { onPick(null) }
        CategoryIcons.palette.forEach { ic ->
            IconChip(selectedState = selected == ic, iconName = ic, dimmed = false) { onPick(ic) }
        }
    }
}

@Composable
private fun IconChip(selectedState: Boolean, iconName: String, dimmed: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    val border = if (selectedState) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline
    Box(
        Modifier.size(44.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(if (selectedState) tandem.savingsTileBg else androidx.compose.ui.graphics.Color.Transparent)
            .border(1.dp, border, RoundedCornerShape(10.dp))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(TandemIcons.forCategory(iconName), contentDescription = null, tint = if (dimmed) tandem.muted else tandem.catAccent, modifier = Modifier.size(20.dp))
    }
}
