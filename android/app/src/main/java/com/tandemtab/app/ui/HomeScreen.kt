package com.tandemtab.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AccountBalanceWallet
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.Flag
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.Payments
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material.icons.automirrored.rounded.ReceiptLong
import androidx.compose.material.icons.automirrored.rounded.TrendingUp
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import com.tandemtab.app.UiState
import com.tandemtab.app.data.MemberDto
import com.tandemtab.app.data.RunwayDto
import com.tandemtab.app.data.TargetDto
import com.tandemtab.app.ui.theme.BrandGreen
import com.tandemtab.app.ui.theme.BrandGreenDark
import com.tandemtab.app.ui.theme.LocalTandemColors
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.util.Currency
import java.util.Locale

// Mirrors the thick prod Dashboard's 4 tabs (Dashboard.razor: Overview/Budgets/Savings/Account),
// in the same order and labels.
private enum class NavDest(val label: String, val icon: ImageVector) {
    Home("Home", Icons.Rounded.Home),
    Spending("Spending", Icons.AutoMirrored.Rounded.ReceiptLong),
    Goals("Goals", Icons.Rounded.Flag),
    Wallets("Wallets", Icons.Rounded.AccountBalanceWallet),
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    state: UiState,
    onSelectAccount: (String) -> Unit,
    onSignOut: () -> Unit,
    onOpenSettings: () -> Unit,
    onChangePassword: (String, String) -> Unit,
    onRenameAccount: (String, () -> Unit) -> Unit,
    onLeaveAccount: (() -> Unit) -> Unit,
    onDeleteAccount: (() -> Unit) -> Unit,
    onLoadSpending: (Boolean) -> Unit,
    onLoadGoals: (Boolean) -> Unit,
    onLoadWallets: (Boolean) -> Unit,
    onLoadHealth: (Boolean) -> Unit,
    onLoadRecurring: (Boolean) -> Unit,
    onConfirmRecurring: (String, Double) -> Unit,
    onSkipRecurring: (String) -> Unit,
    onPrepareAdd: () -> Unit,
    onPrepareEditLast: () -> Unit,
    onClearEditing: () -> Unit,
    onAddExpenses: (List<com.tandemtab.app.data.AddExpenseRequest>, () -> Unit) -> Unit,
    onEditExpense: (String, com.tandemtab.app.data.AddExpenseRequest, () -> Unit) -> Unit,
    onAddIncomeQuick: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareTransfer: () -> Unit,
    onPrepareAddIncome: () -> Unit,
    onTransfer: (String, String, Double, String, String?, () -> Unit) -> Unit,
    onAddIncome: (String, String, Double, String, () -> Unit) -> Unit,
    onPrepareAllocate: () -> Unit,
    onPrepareSpend: () -> Unit,
    onAllocate: (String, Double, String, String?, () -> Unit) -> Unit,
    onSpendFromSavings: (String, String, String, Double, String, String?, () -> Unit) -> Unit,
) {
    val tandem = LocalTandemColors.current
    val scope = rememberCoroutineScope()
    val pagerState = rememberPagerState(pageCount = { NavDest.entries.size })
    val dest = NavDest.entries[pagerState.currentPage]

    val snackbar = remember { SnackbarHostState() }
    var showAddExpense by remember { mutableStateOf(false) }
    var showHealth by remember { mutableStateOf(false) }
    var showRunway by remember { mutableStateOf(false) }
    var showRecurring by remember { mutableStateOf(false) }
    var showProfile by remember { mutableStateOf(false) }
    var showAccount by remember { mutableStateOf(false) }

    // "Edit last" flows through the ViewModel: prepareEditLast loads + picks the last expense into state.editingExpense,
    // which we watch here to raise the add sheet in edit mode.
    val editing = state.editingExpense

    Scaffold(
        containerColor = tandem.canvas,
        topBar = {
            TopAppBar(
                title = {
                    // Brand, mirroring the web app-bar (logo + "TandemTab"). The account name lives in the body header.
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        TandemLogo(size = 26.dp)
                        Spacer(Modifier.width(8.dp))
                        Row {
                            Text("Tandem", fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onBackground)
                            Text("Tab", fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary)
                        }
                    }
                },
                actions = {
                    IconButton(onClick = { onOpenSettings(); showProfile = true }) {
                        Icon(Icons.Rounded.Person, contentDescription = "Profile", tint = tandem.muted)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = tandem.canvas,
                    titleContentColor = MaterialTheme.colorScheme.onBackground,
                ),
            )
        },
        // Custom bottom bar with the add-FAB cradled in the centre (tabs split 2-and-2). The FAB opens a speed-dial.
        bottomBar = {
            TandemBottomBar(
                current = dest,
                onSelect = { d -> scope.launch { pagerState.animateScrollToPage(d.ordinal) } },
                onAdd = { onPrepareAdd(); showAddExpense = true },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        // The add / edit sheet. The FAB opens it in add mode; the in-sheet "Edit last" pulls in the last expense
        // (via the VM's editingExpense), which switches this same sheet into edit mode.
        if (showAddExpense || editing != null) {
            AddSheet(
                spending = state.spending,
                editing = editing,
                onEditLast = onPrepareEditLast,
                onDismiss = { showAddExpense = false; if (editing != null) onClearEditing() },
                onSaveExpenses = onAddExpenses,
                onEditExpense = onEditExpense,
                onAddIncome = onAddIncomeQuick,
            )
        }
        LaunchedEffect(pagerState.currentPage, state.selectedAccountId) {
            when (dest) {
                NavDest.Spending -> onLoadSpending(false)
                NavDest.Goals -> onLoadGoals(false)
                NavDest.Wallets -> onLoadWallets(false)
                NavDest.Home -> { onLoadHealth(false); onLoadRecurring(false); onLoadGoals(false) }
            }
        }

        Box(Modifier.fillMaxSize().padding(padding)) {
            HorizontalPager(state = pagerState, modifier = Modifier.fillMaxSize()) { page ->
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .verticalScroll(rememberScrollState())
                        .padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 24.dp),
                ) {
                    when (NavDest.entries[page]) {
                        NavDest.Home -> HomePage(
                            state, onSelectAccount,
                            onOpenRecurring = { showRecurring = true },
                            onOpenHealth = { showHealth = true },
                            onOpenRunway = { showRunway = true },
                            onOpenAccount = { onOpenSettings(); showAccount = true },
                        )
                        NavDest.Spending -> SpendingScreen(spending = state.spending, onRetry = { onLoadSpending(true) })
                        NavDest.Goals -> GoalsScreen(
                            goals = state.goals,
                            spending = state.spending,
                            onRetry = { onLoadGoals(true) },
                            onPrepareAllocate = onPrepareAllocate,
                            onPrepareSpend = onPrepareSpend,
                            onAllocate = onAllocate,
                            onSpend = onSpendFromSavings,
                        )
                        NavDest.Wallets -> WalletsScreen(
                            wallets = state.wallets,
                            onRetry = { onLoadWallets(true) },
                            onPrepareTransfer = onPrepareTransfer,
                            onPrepareAddIncome = onPrepareAddIncome,
                            onTransfer = onTransfer,
                            onAddIncome = onAddIncome,
                        )
                    }
                }
            }
        }

        if (showHealth && state.health.data?.hasData == true) {
            HealthSheet(health = state.health, onDismiss = { showHealth = false })
        }
        state.runway?.let { rw ->
            if (showRunway) {
                RunwaySheet(runway = rw, fmt = rememberCurrency(rw.currency), onDismiss = { showRunway = false })
            }
        }
        if (showRecurring) {
            RecurringSheet(
                recurring = state.recurring,
                onConfirm = onConfirmRecurring,
                onSkip = onSkipRecurring,
                onDismiss = { showRecurring = false },
            )
        }
        if (showProfile) {
            ProfileSheet(
                state = state,
                onChangePassword = onChangePassword,
                onSignOut = { showProfile = false; onSignOut() },
                onDismiss = { showProfile = false },
            )
        }
        if (showAccount) {
            AccountSheet(
                state = state,
                onRenameAccount = onRenameAccount,
                onOpenRecurring = { showAccount = false; showRecurring = true },
                onLeave = { onLeaveAccount { showAccount = false } },
                onDelete = { onDeleteAccount { showAccount = false } },
                onDismiss = { showAccount = false },
            )
        }
    }
}

/** The Home tab body: account header, the balance hero, then the health / bills / targets / runway cards. */
@Composable
private fun HomePage(
    state: UiState,
    onSelectAccount: (String) -> Unit,
    onOpenRecurring: () -> Unit,
    onOpenHealth: () -> Unit,
    onOpenRunway: () -> Unit,
    onOpenAccount: () -> Unit,
) {
    val tandem = LocalTandemColors.current
    AccountHeader(state, onSelectAccount, onOpenAccount)
    Spacer(Modifier.height(14.dp))

    val overview = state.overview
    when {
        state.busy && overview == null -> Box(
            Modifier.fillMaxWidth().height(200.dp),
            contentAlignment = Alignment.Center,
        ) { CircularProgressIndicator(color = MaterialTheme.colorScheme.primary) }

        overview == null -> Text("No overview to show.", color = tandem.muted)

        else -> {
            val fmt = rememberCurrency(overview.currency)
            // The web "hero" balance bar: Available (main) | Free | Saved, hairline-divided.
            BalanceHero(
                current = fmt(overview.current),
                free = fmt(overview.free),
                saved = fmt(overview.saved),
            )
            Spacer(Modifier.height(14.dp))
            // Order per the design: health score on top, then bills, then "on track for", and finally the runway.
            HealthCard(health = state.health, onOpen = onOpenHealth)
            Spacer(Modifier.height(14.dp))
            RecurringCard(recurring = state.recurring, onOpen = onOpenRecurring)
            Spacer(Modifier.height(14.dp))
            TargetsCard(state.targets, fmt)
            RunwayCard(state.runway, fmt, onOpen = onOpenRunway)
        }
    }
}

/** The bottom bar: four tabs split 2-and-2 with the brand-gradient add-FAB cradled in the centre gap. The FAB
 *  straddles the top edge of the bar, so it reads as a docked primary action and never covers content. */
@Composable
private fun TandemBottomBar(current: NavDest, onSelect: (NavDest) -> Unit, onAdd: () -> Unit) {
    val barHeight = 70.dp
    val fabSize = 60.dp
    Box(
        Modifier
            .fillMaxWidth()
            .height(barHeight + 22.dp),   // extra top room for the FAB to rise above the bar
    ) {
        Surface(
            modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(barHeight),
            color = MaterialTheme.colorScheme.surface,
            shadowElevation = 10.dp,
        ) {
            Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                BarItem(NavDest.Home, current, Modifier.weight(1f), onSelect)
                BarItem(NavDest.Spending, current, Modifier.weight(1f), onSelect)
                Spacer(Modifier.weight(1f))   // the cradle gap the FAB sits in
                BarItem(NavDest.Goals, current, Modifier.weight(1f), onSelect)
                BarItem(NavDest.Wallets, current, Modifier.weight(1f), onSelect)
            }
        }
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .size(fabSize)
                .shadow(10.dp, CircleShape, clip = false)
                .clip(CircleShape)
                .background(Brush.linearGradient(listOf(BrandGreen, BrandGreenDark)))
                .clickable(onClick = onAdd),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Rounded.Add, contentDescription = "Add", tint = Color.White, modifier = Modifier.size(30.dp))
        }
    }
}

@Composable
private fun BarItem(dest: NavDest, current: NavDest, modifier: Modifier, onSelect: (NavDest) -> Unit) {
    val tandem = LocalTandemColors.current
    val selected = dest == current
    val tint = if (selected) MaterialTheme.colorScheme.primary else tandem.muted
    Column(
        modifier
            .fillMaxHeight()
            .clickable(onClick = { onSelect(dest) }),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Icon(dest.icon, contentDescription = dest.label, tint = tint)
        Spacer(Modifier.height(3.dp))
        Text(dest.label, fontSize = 11.sp, color = tint, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal)
    }
}

@Composable
private fun BalanceHero(current: String, free: String, saved: String) {
    val tandem = LocalTandemColors.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(IntrinsicSize.Min)
            .background(tandem.hero, RoundedCornerShape(18.dp))
            .border(1.dp, tandem.hairline, RoundedCornerShape(18.dp))
            .padding(vertical = 14.dp),
    ) {
        HeroPart("Available", current, main = true, valueColor = MaterialTheme.colorScheme.onBackground, weight = 1.4f)
        HeroDivider()
        HeroPart("Free", free, valueColor = tandem.positive)
        HeroDivider()
        HeroPart("Saved", saved, valueColor = tandem.saved)
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.HeroPart(
    label: String,
    value: String,
    main: Boolean = false,
    valueColor: androidx.compose.ui.graphics.Color,
    weight: Float = 1f,
) {
    val tandem = LocalTandemColors.current
    Column(
        modifier = Modifier
            .weight(weight)
            .padding(horizontal = 12.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(
            label.uppercase(),
            fontSize = 10.sp,
            letterSpacing = 1.3.sp,
            fontWeight = FontWeight.Bold,
            color = tandem.muted,
            maxLines = 1,
        )
        // Shrink long amounts by length so big balances never clip on a narrow phone.
        Text(
            value,
            fontSize = fitFont(value, base = if (main) 26 else 17, min = if (main) 15 else 12),
            fontWeight = if (main) FontWeight.ExtraBold else FontWeight.Bold,
            color = valueColor,
            maxLines = 1,
            softWrap = false,
        )
    }
}

/** A length-based font size so long currency strings fit their column without wrapping or clipping. */
private fun fitFont(text: String, base: Int, min: Int): TextUnit {
    val n = text.length
    val size = when {
        n <= 8 -> base
        n <= 11 -> base - 3
        n <= 14 -> base - 6
        else -> min
    }
    return size.coerceAtLeast(min).sp
}

@Composable
private fun HeroDivider() {
    Box(
        Modifier
            .fillMaxHeight()
            .width(1.dp)
            .background(LocalTandemColors.current.hairline),
    )
}

/** The Home "At this rate…" runway card, driven by the server's cash-flow projection (GET /runway). Amber when the
 *  balance runs short in-window, else a neutral "keeps growing / lasts beyond N months". Hidden when there's no
 *  trustworthy basis (server returned 204 → null). */
@Composable
private fun RunwayCard(runway: RunwayDto?, fmt: (Double) -> String, onOpen: () -> Unit) {
    val tandem = LocalTandemColors.current
    val rw = runway ?: return
    val warn = rw.firstShortfallMonth != null
    val headline = when {
        warn -> "At this rate, money runs short in ${monthLabel(rw.firstShortfallMonth!!)}"
        rw.monthlyIncome >= rw.monthlySpending -> "At this rate, your balance keeps growing"
        else -> "At this rate, your balance lasts beyond ${rw.months} months"
    }
    val bg = if (warn) tandem.alertBg else MaterialTheme.colorScheme.surface
    val border = if (warn) tandem.alertBorder else MaterialTheme.colorScheme.outline
    Column(
        Modifier.fillMaxWidth()
            .background(bg, RoundedCornerShape(16.dp))
            .border(1.dp, border, RoundedCornerShape(16.dp))
            .clickable(onClick = onOpen)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(if (warn) "⚠️" else "🛡️", fontSize = 16.sp)
            Spacer(Modifier.width(8.dp))
            Text(headline, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 14.sp, modifier = Modifier.weight(1f))
            Text("Details ›", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
        }
        Text(
            "${fmt(rw.monthlyIncome)} in · ${fmt(rw.monthlySpending)} out each month" +
                if (rw.basedOnRecurring) " · from your declared bills" else " · averaged from recent months",
            color = tandem.muted, fontSize = 12.sp,
        )
    }
    Spacer(Modifier.height(14.dp))
}

/** The Home "You're on track for" card, driven by the server's targets (GET /targets): the combined debt-free date and
 *  each savings goal's projected month (or "reached 🎉"). Hidden when the server returns nothing to project. */
@Composable
private fun TargetsCard(targets: List<TargetDto>, fmt: (Double) -> String) {
    val tandem = LocalTandemColors.current
    if (targets.isEmpty()) return
    Column(
        Modifier.fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(16.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(16.dp))
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.AutoMirrored.Rounded.TrendingUp, null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text("You're on track for", fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 15.sp)
        }
        targets.forEach { t ->
            // Debt-free is a synthetic target: the server sends an empty name, the client supplies the label + flag.
            val icon = if (t.kind == "debt-free") "🏁" else t.icon
            val name = if (t.kind == "debt-free") "Debt-free" else t.name
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (!icon.isNullOrBlank()) { Text(icon, fontSize = 16.sp); Spacer(Modifier.width(8.dp)) }
                Text(name, color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f), maxLines = 1)
                if (t.reached) {
                    Text("reached 🎉", color = tandem.positive, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                } else {
                    Column(horizontalAlignment = Alignment.End) {
                        Text(payoffDate(t.months), fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface, fontSize = 13.sp)
                        Text(monthsText(t.months), color = tandem.muted, fontSize = 11.sp)
                    }
                }
            }
        }
        Text("Projections at your current pace — they don't move real money.", color = tandem.muted, fontSize = 11.sp)
    }
    Spacer(Modifier.height(14.dp))
}

/** "Mar 2040" from an ISO date the server sends for the first-shortfall month. */
private fun monthLabel(iso: String): String = runCatching {
    LocalDate.parse(iso).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))
}.getOrDefault(iso)

private fun payoffDate(months: Int): String =
    LocalDate.now().plusMonths(months.toLong()).format(DateTimeFormatter.ofPattern("MMM yyyy", Locale.getDefault()))

private fun monthsText(months: Int): String = when {
    months <= 0 -> "this month"
    months == 1 -> "in 1 month"
    months < 12 -> "in $months months"
    else -> { val y = months / 12; val m = months % 12; "in ${y}y${if (m > 0) " ${m}m" else ""}" }
}

/** The body account header (mirroring web): the account switcher (pills for multi-account, else a name heading),
 *  the member avatars, and the current-period label — so the account name is shown once, not duplicated. */
@Composable
private fun AccountHeader(state: UiState, onSelectAccount: (String) -> Unit, onOpenAccount: () -> Unit) {
    val tandem = LocalTandemColors.current
    val account = state.selectedAccount
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            if (state.accounts.size > 1) {
                Row(
                    Modifier.weight(1f).horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    state.accounts.forEach { a ->
                        AccountChip(a.name, a.id == state.selectedAccountId) { onSelectAccount(a.id) }
                    }
                }
            } else if (account != null) {
                Text(account.name, fontSize = 22.sp, fontWeight = FontWeight.ExtraBold, color = MaterialTheme.colorScheme.onBackground, maxLines = 1, modifier = Modifier.weight(1f))
            } else {
                Spacer(Modifier.weight(1f))
            }
            // Account actions (rename / members / recurring / leave / delete), mirroring the web account menu.
            IconButton(onClick = onOpenAccount) {
                Icon(Icons.Rounded.Tune, contentDescription = "Account actions", tint = tandem.muted)
            }
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            MemberAvatars(account?.members ?: emptyList())
            Spacer(Modifier.weight(1f))
            state.periodLabel?.let { Text(it, fontSize = 13.sp, color = tandem.muted, fontWeight = FontWeight.Medium) }
        }
    }
}

private val avatarPalette = listOf(
    Color(0xFF13A06E), Color(0xFF3B82F6), Color(0xFFF59E0B), Color(0xFF8B5CF6), Color(0xFFEF4444),
)

@Composable
private fun MemberAvatars(members: List<MemberDto>) {
    if (members.isEmpty()) return
    val tandem = LocalTandemColors.current
    Row(verticalAlignment = Alignment.CenterVertically) {
        Row(horizontalArrangement = Arrangement.spacedBy((-10).dp)) {
            members.take(4).forEachIndexed { i, m ->
                AvatarCircle(initialOf(m.displayName), avatarPalette[i % avatarPalette.size])
            }
            if (members.size > 4) AvatarCircle("+${members.size - 4}", tandem.muted)
        }
        Spacer(Modifier.width(10.dp))
        val names = members.joinToString(", ") { it.displayName.ifBlank { "—" } }
        Text(
            if (names.length > 26) names.take(24) + "…" else names,
            fontSize = 12.sp, color = tandem.muted, maxLines = 1, fontWeight = FontWeight.Medium,
        )
    }
}

@Composable
private fun AvatarCircle(text: String, color: Color) {
    Box(
        Modifier
            .size(30.dp)
            .clip(CircleShape)
            .background(color)
            .border(2.dp, MaterialTheme.colorScheme.surface, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, color = Color.White, fontSize = if (text.length > 1) 11.sp else 13.sp, fontWeight = FontWeight.Bold)
    }
}

private fun initialOf(name: String): String = name.trim().firstOrNull()?.uppercase() ?: "?"

@Composable
private fun AccountChip(name: String, selected: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    val bg = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else tandem.muted
    Text(
        name,
        color = fg,
        fontSize = 13.sp,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier
            .background(bg, RoundedCornerShape(999.dp))
            .border(1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, RoundedCornerShape(999.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

/** Currency formatter for the account's ISO code, matching the web app's money formatting. */
private fun rememberCurrency(currencyCode: String): (Double) -> String {
    val nf = NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
