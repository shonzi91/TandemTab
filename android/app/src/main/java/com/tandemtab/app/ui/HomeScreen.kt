package com.tandemtab.app.ui

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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.AccountBalanceWallet
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Flag
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.automirrored.rounded.ReceiptLong
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FabPosition
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import com.tandemtab.app.UiState
import com.tandemtab.app.ui.theme.BrandGreen
import com.tandemtab.app.ui.theme.BrandGreenDark
import com.tandemtab.app.ui.theme.LocalTandemColors
import java.text.NumberFormat
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
    onLoadSpending: (Boolean) -> Unit,
    onLoadGoals: (Boolean) -> Unit,
    onLoadWallets: (Boolean) -> Unit,
    onLoadHealth: (Boolean) -> Unit,
    onLoadRecurring: (Boolean) -> Unit,
    onConfirmRecurring: (String, Double) -> Unit,
    onSkipRecurring: (String) -> Unit,
    onPrepareAdd: () -> Unit,
    onAddExpenses: (List<com.tandemtab.app.data.AddExpenseRequest>, () -> Unit) -> Unit,
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
    var dest by rememberSaveable { mutableStateOf(NavDest.Home) }
    val snackbar = remember { SnackbarHostState() }
    var showAddExpense by remember { mutableStateOf(false) }
    var showHealth by remember { mutableStateOf(false) }
    var showRecurring by remember { mutableStateOf(false) }

    Scaffold(
        containerColor = tandem.canvas,
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        state.selectedAccount?.name ?: "TandemTab",
                        fontWeight = FontWeight.Bold,
                    )
                },
                actions = { TextButton(onClick = onSignOut) { Text("Sign out") } },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = tandem.canvas,
                    titleContentColor = MaterialTheme.colorScheme.onBackground,
                ),
            )
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                NavDest.entries.forEach { d ->
                    NavigationBarItem(
                        selected = dest == d,
                        onClick = { dest = d },
                        icon = { Icon(d.icon, contentDescription = d.label) },
                        label = { Text(d.label, fontSize = 11.sp) },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = MaterialTheme.colorScheme.primary,
                            selectedTextColor = MaterialTheme.colorScheme.primary,
                            indicatorColor = tandem.savingsTileBg,
                            unselectedIconColor = tandem.muted,
                            unselectedTextColor = tandem.muted,
                        ),
                    )
                }
            }
        },
        floatingActionButton = {
            // One prominent centre action on every tab — opens the unified Expense/Income sheet.
            AddFab(onClick = { onPrepareAdd(); showAddExpense = true })
        },
        floatingActionButtonPosition = FabPosition.Center,
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        if (showAddExpense) {
            AddSheet(
                spending = state.spending,
                onDismiss = { showAddExpense = false },
                onSaveExpenses = onAddExpenses,
                onAddIncome = onAddIncomeQuick,
            )
        }
        LaunchedEffect(dest, state.selectedAccountId) {
            when (dest) {
                NavDest.Spending -> onLoadSpending(false)
                NavDest.Goals -> onLoadGoals(false)
                NavDest.Wallets -> onLoadWallets(false)
                NavDest.Home -> { onLoadHealth(false); onLoadRecurring(false) }
            }
        }
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                // Extra bottom room so the centre-docked FAB never covers the last rows.
                .padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 96.dp),
        ) {
            when (dest) {
                NavDest.Spending -> {
                    SpendingScreen(spending = state.spending, onRetry = { onLoadSpending(true) })
                    return@Column
                }
                NavDest.Goals -> {
                    GoalsScreen(
                        goals = state.goals,
                        spending = state.spending,
                        onRetry = { onLoadGoals(true) },
                        onPrepareAllocate = onPrepareAllocate,
                        onPrepareSpend = onPrepareSpend,
                        onAllocate = onAllocate,
                        onSpend = onSpendFromSavings,
                    )
                    return@Column
                }
                NavDest.Wallets -> {
                    WalletsScreen(
                        wallets = state.wallets,
                        onRetry = { onLoadWallets(true) },
                        onPrepareTransfer = onPrepareTransfer,
                        onPrepareAddIncome = onPrepareAddIncome,
                        onTransfer = onTransfer,
                        onAddIncome = onAddIncome,
                    )
                    return@Column
                }
                NavDest.Home -> {}
            }

            if (state.accounts.size > 1) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    state.accounts.forEach { account ->
                        AccountChip(
                            name = account.name,
                            selected = account.id == state.selectedAccountId,
                            onClick = { onSelectAccount(account.id) },
                        )
                    }
                }
                Spacer(Modifier.height(14.dp))
            }

            val overview = state.overview
            when {
                state.busy && overview == null -> Box(
                    Modifier
                        .fillMaxWidth()
                        .height(200.dp),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator(color = MaterialTheme.colorScheme.primary) }

                overview == null -> Text("No overview to show.", color = tandem.muted)

                else -> {
                    val fmt = rememberCurrency(overview.currency)
                    // The web "hero" balance bar: Current (main) | Free | Saved, hairline-divided.
                    BalanceHero(
                        current = fmt(overview.current),
                        free = fmt(overview.free),
                        saved = fmt(overview.saved),
                    )
                    Spacer(Modifier.height(14.dp))
                    FigureGrid(
                        listOf(
                            Figure("Spent", fmt(overview.spent), tandem.spent),
                            Figure("Contributed", fmt(overview.contributed), null),
                            Figure("Bills due", fmt(overview.billsDue), null),
                            Figure("Safe after bills", fmt(overview.safeAfterBills), tandem.positive),
                        ),
                    )
                    Spacer(Modifier.height(14.dp))
                    RecurringCard(recurring = state.recurring, onOpen = { showRecurring = true })
                    Spacer(Modifier.height(14.dp))
                    HealthCard(health = state.health, onOpen = { showHealth = true })
                }
            }
        }

        if (showHealth && state.health.data?.hasData == true) {
            HealthSheet(health = state.health, onDismiss = { showHealth = false })
        }
        if (showRecurring) {
            RecurringSheet(
                recurring = state.recurring,
                onConfirm = onConfirmRecurring,
                onSkip = onSkipRecurring,
                onDismiss = { showRecurring = false },
            )
        }
    }
}

/** A prominent, brand-gradient circular add button — the single centre action, docked over the nav bar. */
@Composable
private fun AddFab(onClick: () -> Unit) {
    Box(
        Modifier
            .size(62.dp)
            .shadow(10.dp, CircleShape, clip = false)
            .clip(CircleShape)
            .background(Brush.linearGradient(listOf(BrandGreen, BrandGreenDark)))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(Icons.Rounded.Add, contentDescription = "Add", tint = Color.White, modifier = Modifier.size(30.dp))
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
            .padding(horizontal = 14.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(
            label.uppercase(),
            fontSize = 10.sp,
            letterSpacing = 1.3.sp,
            fontWeight = FontWeight.Bold,
            color = tandem.muted,
        )
        Text(
            value,
            fontSize = if (main) 26.sp else 17.sp,
            fontWeight = if (main) FontWeight.ExtraBold else FontWeight.Bold,
            color = valueColor,
        )
    }
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

private data class Figure(val label: String, val value: String, val valueColor: androidx.compose.ui.graphics.Color?)

@Composable
private fun FigureGrid(items: List<Figure>) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        items.chunked(2).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                row.forEach { f -> FigureCard(f, Modifier.weight(1f)) }
                if (row.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun FigureCard(f: Figure, modifier: Modifier = Modifier) {
    val tandem = LocalTandemColors.current
    Column(
        modifier = modifier
            .background(MaterialTheme.colorScheme.surface, RoundedCornerShape(14.dp))
            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(14.dp))
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text(f.label, fontSize = 12.sp, color = tandem.muted)
        Text(
            f.value,
            fontSize = 19.sp,
            fontWeight = FontWeight.Bold,
            color = f.valueColor ?: MaterialTheme.colorScheme.onSurface,
        )
    }
}

@Composable
private fun AccountChip(name: String, selected: Boolean, onClick: () -> Unit) {
    val tandem = LocalTandemColors.current
    val bg = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface
    val fg = if (selected) MaterialTheme.colorScheme.onPrimary else tandem.muted
    TextButton(
        onClick = onClick,
        shape = RoundedCornerShape(999.dp),
        modifier = Modifier
            .background(bg, RoundedCornerShape(999.dp))
            .border(1.dp, if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, RoundedCornerShape(999.dp)),
    ) {
        Text(name, color = fg, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** Currency formatter for the account's ISO code, matching the web app's money formatting. */
private fun rememberCurrency(currencyCode: String): (Double) -> String {
    val nf = NumberFormat.getCurrencyInstance(Locale.getDefault())
    runCatching { nf.currency = Currency.getInstance(currencyCode) }
    return { amount -> nf.format(amount) }
}
