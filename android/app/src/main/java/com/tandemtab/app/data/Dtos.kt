package com.tandemtab.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Wire models mirroring FinApp.Contracts. The server serializes records with camelCase property names
 * (System.Text.Json default), so these match by field name; @SerialName is used only where the Kotlin
 * name would differ.
 */

@Serializable
data class LoginRequest(
    val usernameOrEmail: String,
    val password: String,
)

@Serializable
data class RegisterRequest(
    val username: String,
    val email: String,
    val password: String,
)

@Serializable
data class ForgotPasswordRequest(val identifier: String)

@Serializable
data class ExternalProvidersDto(
    val google: Boolean = false,
    val facebook: Boolean = false,
)

@Serializable
data class ExchangeCodeRequest(val code: String)

@Serializable
data class AuthResponse(
    val token: String,
    val userId: String,
    val username: String,
    val email: String,
    val expiresAt: String,
    val refreshToken: String? = null,
)

@Serializable
data class LoginResponse(
    val twoFactorRequired: Boolean,
    val auth: AuthResponse? = null,
    val twoFactorTicket: String? = null,
)

@Serializable
data class RefreshRequest(val refreshToken: String)

@Serializable
data class LogoutRequest(val refreshToken: String)

@Serializable
data class TwoFactorLoginRequest(val ticket: String, val code: String)

@Serializable
data class MemberDto(
    val userId: String,
    // Server sends "displayName" (FinApp.Contracts.MemberDto), not "username".
    val displayName: String = "",
)

@Serializable
data class AccountSummaryDto(
    val id: String,
    val name: String,
    val currency: String,
    val ownerUserId: String,
    val isOwner: Boolean,
    val members: List<MemberDto> = emptyList(),
)

@Serializable
data class AccountOverviewDto(
    val currency: String,
    val current: Double,
    val free: Double,
    val saved: Double,
    val spent: Double,
    val contributed: Double,
    val billsDue: Double,
    val safeAfterBills: Double,
)

@Serializable
data class ExpenseDto(
    val id: String,
    val categoryId: String,
    val categoryName: String,
    val categoryIcon: String? = null,
    val fundId: String,
    val fundName: String,
    val amount: Double,
    val date: String, // ISO yyyy-MM-dd
    val note: String? = null,
    val autoFiled: Boolean = false,
    val fromSavings: Boolean = false,
    val onBehalfOfOtherAccount: Boolean = false,
    val isSettlementSource: Boolean = false,
    val isSettlementDestination: Boolean = false,
)

@Serializable
data class CategoryOptionDto(val id: String, val name: String, val icon: String? = null, val parentId: String? = null)

@Serializable
data class FundOptionDto(val id: String, val name: String, val synced: Boolean = false)

@Serializable
data class SpendingViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val expenses: List<ExpenseDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
)

/** The signed-in user (GET /me). `provider` is "google"/"facebook" for external sign-in (no local password), else
 *  null. Other fields (avatar/2FA/verification) are ignored by the client. Server sends "id"; we don't need it. */
@Serializable
data class UserDto(
    val username: String = "",
    val email: String = "",
    val provider: String? = null,
    val avatar: String? = null,               // data-URL profile picture (provider-sourced for external logins)
    val emailVerified: Boolean = false,
    val twoFactorEnabled: Boolean = false,
)

/** POST /auth/2fa/setup — begins enrollment. `qrImage` is a data-URL PNG of the otpauth URI to scan. */
@Serializable
data class TwoFactorSetupDto(val secret: String = "", val otpauthUri: String = "", val qrImage: String = "")

/** POST /auth/2fa/confirm or /disable — the 6-digit TOTP (or a recovery code). */
@Serializable
data class TwoFactorCodeRequest(val code: String)

/** POST /auth/2fa/confirm result — one-time recovery codes, shown once. */
@Serializable
data class TwoFactorRecoveryDto(val recoveryCodes: List<String> = emptyList())

/** PUT /me/avatar — set (or clear, with null) the profile picture as a data URL. */
@Serializable
data class SetAvatarRequest(val dataUrl: String?)

/** POST /auth/password — change the signed-in user's password. */
@Serializable
data class ChangePasswordRequest(val currentPassword: String, val newPassword: String)

/** PUT /accounts/{id}/name — rename an account (returns 204). */
@Serializable
data class RenameAccountRequest(val name: String)

/** POST /accounts/{id}/leave — leave a shared account. `newOwnerUserId` is required only when the owner leaves. */
@Serializable
data class LeaveAccountRequest(val newOwnerUserId: String? = null)

// --- Periods (for the current-period label in the top bar) --------------------------------------------

@Serializable
data class PeriodRowDto(
    val index: Int,
    val from: String,   // ISO yyyy-MM-dd
    val to: String,
    val isOpen: Boolean = false,
    val isLatest: Boolean = false,
)

@Serializable
data class PeriodsViewDto(
    val currency: String = "",
    val currentIndex: Int = -1,
    val periods: List<PeriodRowDto> = emptyList(),
)

// --- Forecast: runway ("At this rate…") + targets ("You're on track for") -----------------------------

/** The Home runway (GET /accounts/{id}/runway; 204 → null). Everything the client needs to caption the card. */
@Serializable
data class RunwayDto(
    val currency: String = "",
    val months: Int = 0,
    val firstShortfallMonth: String? = null,   // ISO yyyy-MM-dd, or null when the balance never runs short in-window
    val monthlyIncome: Double = 0.0,
    val monthlySpending: Double = 0.0,
    val basedOnRecurring: Boolean = false,
    val completedPeriodCount: Int = 0,
    val hasUnknownAmounts: Boolean = false,
    val openingBalance: Double = 0.0,
    val fromMonth: String = "",
    val monthlyCommitted: Double = 0.0,
)

/** One Home "on track for" target. kind = "debt-free" (name empty; client supplies the label + flag) or "goal". */
@Serializable
data class TargetDto(
    val kind: String,
    val name: String = "",
    val icon: String? = null,
    val months: Int = 0,
    val reached: Boolean = false,
)

@Serializable
data class TargetsDto(val targets: List<TargetDto> = emptyList())

// --- Budgets (per-category coverage for the Spending → Categories view) -------------------------------

@Serializable
data class BudgetRowDto(
    val categoryId: String,
    val name: String,
    val icon: String? = null,
    val allocated: Double = 0.0,
    val spent: Double = 0.0,
    val remaining: Double = 0.0,
    val alertThreshold: Double = 0.0,
    val notifyEvery: Boolean = false,
    val over: Boolean = false,
    val essential: Boolean = false,
    val maxBudget: Double = 0.0,
)

@Serializable
data class BudgetsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val totalBudgeted: Double = 0.0,
    val totalSpent: Double = 0.0,
    val budgets: List<BudgetRowDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
)

/** Set (upsert) a category's budget for the current period. Mirrors the server's SetBudgetRequest. */
@Serializable
data class SetBudgetRequest(
    val amount: Double,
    val thresholdPercent: Double = 80.0,
    val notifyEvery: Boolean = false,
)

/** Returned by the budget PUT/DELETE: the new version + a freshly-computed budgets view to reconcile from. */
@Serializable
data class BudgetMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val view: BudgetsViewDto = BudgetsViewDto(),
)

// --- Category management (mirrors FinApp.Contracts) ---------------------------------------------------

@Serializable
data class CreateCategoryRequest(
    val name: String,
    val parentId: String? = null,
    val icon: String? = null,
    val essential: Boolean = false,
)

@Serializable
data class EditCategoryRequest(
    val name: String,
    val icon: String? = null,
    val essential: Boolean? = null,
)

/** Archive (hide) or restore a category/fund/bucket. Reversible; keeps history. */
@Serializable
data class SetArchivedRequest(val archived: Boolean)

// --- Add-expense write flow (mirrors FinApp.Contracts) ------------------------------------------------
// POST /accounts/{id}/expenses. The member is the caller and FundSynced is derived server-side, so neither
// travels in the request. `date` is an ISO yyyy-MM-dd string (maps to the server's DateOnly).

@Serializable
data class AddExpenseRequest(
    val categoryId: String,
    val amount: Double,
    val fundId: String,
    val date: String,
    val note: String? = null,
    val onBehalfOfOtherAccount: Boolean = false,
    // One tag per expense (the Android add sheet doesn't expose a tag picker yet, so this stays null).
    val tagId: String? = null,
)

/** What POST/PUT/DELETE /expenses returns: the new snapshot version, the row's id, the (added/edited) row for the
 *  client to splice into its list, and the recomputed bank-adjusted overview — so a thin client reconciles with no
 *  re-fetch. `expense` is null on a delete. */
@Serializable
data class ExpenseMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val expense: ExpenseDto? = null,
    val overview: AccountOverviewDto,
)

/** One past manual expense the add-expense modal derives its "recent" chips + per-category default fund from
 *  (GET /expense-entry — spans every period, newest-first). */
@Serializable
data class RecentExpenseDto(
    val categoryId: String,
    val fundId: String,
    val amount: Double,
    val note: String? = null,
    val date: String,
)

@Serializable
data class ExpenseEntryDto(
    val version: Long = 0,
    val recent: List<RecentExpenseDto> = emptyList(),
)

// --- Goals / Savings (mirrors FinApp.Contracts.SavingsView) ------------------------------------------
// Every figure resolved server-side; the client just renders. Kind is "goal"/"debt"/"investment"/"sinking".

@Serializable
data class SavingBucketDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val saved: Double,
    val kind: String,
    val archived: Boolean = false,
    val goalTarget: Double? = null,
    val goalProgress: Double? = null,     // 0..1
    val debtBalance: Double? = null,       // owed today
    val debtProgress: Double? = null,      // 0..1 paid off
    val debtMonthsAhead: Int? = null,
    val investmentProjected: Double? = null,
    val monthlySetAside: Double? = null,
    val targetShortfall: Double? = null,
)

@Serializable
data class SavingsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val availableToSave: Double = 0.0,
    val maxAdditionalSavings: Double = 0.0,
    val buckets: List<SavingBucketDto> = emptyList(),
)

// --- Savings (Goals) write flows ---------------------------------------------------------------------

/** Generic write result: new version + the affected entity id. */
@Serializable
data class MutationResultDto(val version: Long = 0, val entityId: String? = null)

/** POST /accounts/{id}/savings/deposits — earmark money into a bucket ("Add to savings"). */
@Serializable
data class AddSavingDepositRequest(
    val savingCategoryId: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

/** POST /accounts/{id}/savings/spend — draw a bucket down as a real expense. `fundId` empty = the server's
 *  default spendable fund. */
@Serializable
data class SpendFromSavingsRequest(
    val savingCategoryId: String,
    val categoryId: String,
    val amount: Double,
    val date: String,
    val fundId: String,
    val note: String? = null,
)

/** What a savings money-movement returns: version, the row id, and a refreshed Savings view to reconcile. */
@Serializable
data class SavingsMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val view: SavingsViewDto,
)

// --- Wallets / Funds (mirrors FinApp.Contracts.WalletsView) ------------------------------------------

@Serializable
data class FundRowDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val note: String? = null,
    val balance: Double,
    val openingBalance: Double = 0.0,
    val synced: Boolean = false,
    val archived: Boolean = false,
    val availableToTransferOut: Double = 0.0,
)

@Serializable
data class FundTransferRowDto(
    val id: String,
    val fromFundId: String,
    val fromFundName: String,
    val toFundId: String,
    val toFundName: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

@Serializable
data class WalletsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val funds: List<FundRowDto> = emptyList(),
    val archivedFunds: List<FundRowDto> = emptyList(),
    val transfers: List<FundTransferRowDto> = emptyList(),
)

// --- Wallets write flows: move money between funds, record income into a fund --------------------------

/** POST /accounts/{id}/fund-transfers. `date` is an ISO yyyy-MM-dd string (server DateOnly?). */
@Serializable
data class TransferFundsRequest(
    val fromFundId: String,
    val toFundId: String,
    val amount: Double,
    val date: String? = null,
    val note: String? = null,
)

/** What a fund write (transfer / fund CRUD) returns: the new version, the affected entity id, and a fully
 *  refreshed Wallets view so the client reconciles balances/transfers with no re-fetch. */
@Serializable
data class FundMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val view: WalletsViewDto,
)

/** POST /accounts/{id}/deposits — record income into a fund. `categoryId` empty = general income; deposits with
 *  the same (member, category, fund) merge server-side. `date` is an ISO yyyy-MM-dd string. */
@Serializable
data class AddDepositRequest(
    val categoryId: String,
    val fundId: String,
    val amount: Double,
    val date: String,
)

/** What a deposit write returns: the new version, the (merged) deposit row id, and the recomputed overview
 *  (income moves Contributed/Current/Free, not the fund/transfer lists — so re-fetch Wallets for balances). */
@Serializable
data class DepositMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val overview: AccountOverviewDto,
)

// --- Recurring (bills / income expectations) ---------------------------------------------------------

@Serializable
data class RecurringRowDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val kind: String,          // "expense" | "income"
    val mode: String,          // "fixed" | "typical" | "reminder"
    val expected: Double = 0.0,
    val dayOfMonth: Int = 0,
    val categoryName: String = "",
    val fundName: String = "",
    val active: Boolean = true,
    val due: Boolean = false,
    val upcoming: Boolean = false,
    val daysUntilDue: Int = 0,
    val hasKnownAmount: Boolean = true,
)

@Serializable
data class RecurringViewDto(
    val version: Long = 0,
    val currency: String = "",
    val billsDue: Double = 0.0,
    val items: List<RecurringRowDto> = emptyList(),
)

/** What a recurring confirm/skip returns: version, the item id, and the refreshed Recurring view. */
@Serializable
data class RecurringMutationDto(
    val version: Long = 0,
    val entityId: String? = null,
    val view: RecurringViewDto,
)

/** POST /accounts/{id}/recurring/{id}/confirm — post the bill/income with its actual amount. */
@Serializable
data class ConfirmRecurringRequest(val actualAmount: Double)

// --- Income surface (for the Add-income contribution-category picker) ---------------------------------

@Serializable
data class IncomeViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val deposits: List<DepositRowDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
)

// --- Insights / Health (mirrors FinApp.Contracts InsightsDto) -----------------------------------------
// Narrative is language-independent: each message is a stable `code` + `args` the client renders via
// InsightNarrator. Amounts/percents are formatted client-side (args carry raw numbers).

@Serializable
data class InsightArgDto(val kind: String, val number: Double = 0.0, val text: String? = null)

@Serializable
data class InsightMessageDto(val code: String, val args: List<InsightArgDto> = emptyList())

@Serializable
data class InsightSignalDto(
    val kind: String,          // "warn" | "good" | "info"
    val title: InsightMessageDto,
    val desc: InsightMessageDto,
    val delta: InsightMessageDto,
    val dir: String,           // "up" | "down" | "flat"
)

@Serializable
data class InsightCategoryDto(val name: String, val icon: String? = null, val amount: Double, val barFraction: Double, val dir: String)

@Serializable
data class InsightTrendPointDto(val label: String, val outgoings: Double, val barFraction: Double, val isCurrent: Boolean = false)

@Serializable
data class InsightMiniTrendDto(
    val label: InsightMessageDto,
    val icon: String? = null,
    val points: List<Double> = emptyList(),
    val currentText: InsightMessageDto,
    val deltaNote: InsightMessageDto,
    val dir: String,
)

@Serializable
data class InsightsDto(
    val hasData: Boolean = false,
    val score: Int = 0,
    val scoreDelta: Int? = null,
    val band: String = "average",             // "healthy" | "average" | "at_risk"
    val savingsRate: Double? = null,           // 0..1
    val savingsTarget: Double = 0.20,          // 0..1
    val savingsShortfall: Double? = null,
    val trendUp: Boolean = false,
    val trendAverage: Double = 0.0,
    val trendAvgFraction: Double = 0.0,
    val verdict: InsightMessageDto = InsightMessageDto("verdict.average"),
    val summary: List<InsightMessageDto> = emptyList(),
    val savingsCritique: List<InsightMessageDto> = emptyList(),
    val trendNote: InsightMessageDto = InsightMessageDto("trend.none"),
    val signals: List<InsightSignalDto> = emptyList(),
    val breakdown: List<InsightCategoryDto> = emptyList(),
    val trend: List<InsightTrendPointDto> = emptyList(),
    val miniTrends: List<InsightMiniTrendDto> = emptyList(),
    val quickWins: List<InsightMessageDto> = emptyList(),
)

// --- Bank sync (Open Banking via Enable Banking) ------------------------------------------------------
// Allowlist- and email-verification-gated server-side. The status read reports Enabled=false to anyone who
// can't use it, so the client hides all bank UI. Turning a pending transaction into an expense/income posts
// through the normal write endpoints, then acks the row so a later sync won't resurface it.

/** GET /accounts/{id}/bank/status — whether the feature is available + this account's connection (if any). */
@Serializable
data class BankSyncStatusDto(
    val enabled: Boolean = false,
    val connected: Boolean = false,
    val institutionName: String? = null,
    val consentExpiresAt: String? = null,
    val lastSyncedAt: String? = null,
    val fundId: String? = null,
    val balance: Double? = null,
    val balanceCurrency: String? = null,
    val accountRef: String? = null,
    val institutionLogo: String? = null,
)

/** GET /accounts/{id}/bank/institutions — a bank the aggregator knows about (name + country + logo URL). */
@Serializable
data class BankInstitutionDto(val name: String, val country: String, val logo: String? = null)

/** POST /accounts/{id}/bank/link. `native = true` routes the bank's callback back through the app deep link. */
@Serializable
data class StartBankLinkRequest(
    val institutionName: String,
    val country: String,
    val logo: String? = null,
    val native: Boolean = true,
)

/** The URL to open in a browser to complete the bank's consent flow. */
@Serializable
data class StartBankLinkResponse(val linkUrl: String)

/** One fetched bank transaction not yet turned into (or dismissed from becoming) a FinApp entry. A negative
 *  amount is a debit (spend → expense); a positive amount is a credit (money in → income). */
@Serializable
data class PendingBankTransactionDto(
    val externalId: String,
    val amount: Double,
    val date: String,
    val description: String,
)

/** POST /accounts/{id}/bank/ack — mark a staged transaction handled (turned into an entry) or dismissed. */
@Serializable
data class BankTransactionAck(val externalId: String, val confirmed: Boolean)

/** POST /consent — record the caller's consent for a scope (bank_link before linking a bank). */
@Serializable
data class RecordConsentRequest(val scope: String, val accountId: String?, val granted: Boolean)

@Serializable
data class DepositRowDto(
    val id: String,
    val memberName: String = "",
    val categoryId: String,
    val categoryName: String = "",
    val categoryIcon: String? = null,
    val fundId: String,
    val fundName: String = "",
    val amount: Double,
    val date: String,
)
