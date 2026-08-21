package com.tandemtab.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.math.BigDecimal
import java.math.RoundingMode

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
    // The rest of the web Home hero, computed server-side (see AccountOverview in the domain). Defaulted so an
    // older server still parses: the tiles that need them just render without their sub-lines.
    val moneyIn: Double = 0.0,          // fresh income + free carry-in; moneyIn − contributed = the carried half
    val transfersOut: Double = 0.0,     // the account-transfer half of money out; spent stays expenses-only
    val savedThisPeriod: Double = 0.0,  // set aside THIS period (vs `saved`, the standing earmark)
    val savedRate: Double? = null,      // savedThisPeriod / moneyIn, or null when nothing came in
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
    // ★ Who the settlement is with, and how much of this expense has moved. New to the read model in S108: the two
    // booleans above could mark a row but not act on it, and the undo route is addressed by the destination account
    // id — so without [settledToAccountId] the phone could see a settled expense and had no way to unsettle it.
    // Both accounts are ones this user belongs to, so the names come from the account list already in state.
    val settledToAccountId: String? = null,
    val settledFromAccountId: String? = null,
    val settledAmount: Double = 0.0,
    // R2 installment split: rows sharing an `installmentGroupId` are ONE logged loan payment, and the server
    // removes them as a unit. Parsing these is what stops a single-row delete from leaving a half-installment
    // (principal gone, interest kept) that reconciles to nothing. `installmentPart` is
    // "principal" | "interest" | "additional"; all three are null on an ordinary expense.
    // ⚠️ `debtBucketId` names the loan by **id only** — the DTO carries no debt name, so a thin client can label
    // the row "Principal" but can only say *which* loan if the Goals cache happens to be warm.
    val installmentGroupId: String? = null,
    val installmentPart: String? = null,
    val debtBucketId: String? = null,
    // The journey this expense points at, if any. A LINK, never a date test — a March flight belongs to a June trip.
    val tripId: String? = null,
    // Its labels. A list because the server's field has always been one, though the UI settles on at most one.
    val tagIds: List<String> = emptyList(),
    // The time of day, "HH:mm:ss" or null. ⚠️ Null is a REAL answer and must never become midnight: most bank feeds
    // report a booking date only, and 00:00 would sort a whole day's imports above everything logged that morning.
    // Untimed rows belong at the BOTTOM of their own day. (Server-side the same rule lives in Expense.SortTime.)
    val time: String? = null,
    // How much has come back on this expense — a refund, or a friend's share of a bill paid back into the wallet it
    // was paid from. ⚠️ [amount] above is ALREADY the reduced figure, so totals are right without touching this;
    // what it buys is the ability to say why the row reads €40 when the receipt said €60, and to offer the undo.
    val refundedAmount: Double = 0.0,
)

@Serializable
data class CategoryOptionDto(val id: String, val name: String, val icon: String? = null, val parentId: String? = null)

/** `currency`/`rate` describe a wallet holding FOREIGN cash: what it is denominated in, and what one unit of it is
 *  worth in the ACCOUNT's currency (1 SEK = 0.087 EUR → 0.087). Both null on an ordinary wallet, which is nearly
 *  always. They mean nothing apart — see [hasRate]. */
@Serializable
data class FundOptionDto(
    val id: String,
    val name: String,
    val synced: Boolean = false,
    val currency: String? = null,
    val rate: Double? = null,
) {
    /** Mirrors the domain's `Fund.HasRate`. A currency with no rate is a labelled wallet nobody has priced yet:
     *  it must NOT convert, because converting by a missing rate is how face value gets stored as if it had been
     *  through the arithmetic. */
    val hasRate: Boolean get() = !currency.isNullOrBlank() && (rate ?: 0.0) > 0.0

    /** What `typed` (in this wallet's currency) is worth in the account's — the same round-half-away-from-zero to
     *  two places the server's `Fund.ToAccountCurrency` applies, so both clients store the identical figure. */
    fun toAccountCurrency(typed: Double): Double =
        if (hasRate) BigDecimal.valueOf(typed).multiply(BigDecimal.valueOf(rate!!))
            .setScale(2, RoundingMode.HALF_UP).toDouble()
        else typed
}

/** A tag as a picker option. `categoryId` is the category picking it files the expense into (so a label is a filing
 *  decision, not a note); `tripTag` marks the seeded trip label set, which only the trip entry form offers. */
@Serializable
data class TagOptionDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val categoryId: String? = null,
    val tripTag: Boolean = false,
)

/** A tag as the MANAGE surface reads it, which is a different question from the picker's.
 *  `archived` is the whole reason this is a separate read: the picker's list is built from the server's active tags,
 *  so a client working only from that could archive a label and never see it again. `uses` is how many expenses
 *  carry it — removing a tag is a HARD delete, so that number is what makes the confirm a real question. */
@Serializable
data class TagRowDto(
    val id: String,
    val name: String,
    val icon: String? = null,
    val categoryId: String? = null,
    val categoryName: String? = null,
    val tripTag: Boolean = false,
    val archived: Boolean = false,
    val uses: Int = 0,
)

/** Every tag in the account, archived included, plus the categories the F2 binding picker needs — so the manage
 *  sheet is self-sufficient and does not depend on Spending having been loaded first. */
@Serializable
data class TagsViewDto(
    val version: Long = 0,
    val tags: List<TagRowDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
)

@Serializable
data class SpendingViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val expenses: List<ExpenseDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
    val tags: List<TagOptionDto> = emptyList(),
)

/** One trip. `state` is resolved by the SERVER against the local date we send it — deliberately not re-derived
 *  here: four states come out of three nullable dates plus a "today", and the rules are not obvious (a trip is
 *  finished when the traveller says so *or* its last day passes; it is active only once departure is CONFIRMED,
 *  because a date is not a departure). A second implementation in Kotlin is a second place for it to drift. */
@Serializable
data class TripDto(
    val id: String,
    val name: String,
    val destination: String? = null,
    val from: String,                          // ISO yyyy-MM-dd, inclusive
    val to: String,                            // ISO yyyy-MM-dd, inclusive
    val icon: String? = null,
    val savingCategoryId: String? = null,
    val savingCategoryName: String? = null,
    val budget: Double? = null,
    val categoryId: String? = null,
    val categoryName: String? = null,
    val categoryIcon: String? = null,
    val spendCurrency: String? = null,
    val rate: Double? = null,
    val startedOn: String? = null,
    val finishedOn: String? = null,
    val savingsApplied: Double = 0.0,
    val state: String = TripState.UPCOMING,
    val lengthInDays: Int = 1,
    val day: Int? = null,                      // which day of the trip today is, 1-based
    val daysUntil: Int? = null,
    val spent: Double = 0.0,
    val expenseCount: Int = 0,
    val prePaid: Double = 0.0,                 // paid before departure — the half people forget
    val onTrip: Double = 0.0,
    val afterReturn: Double = 0.0,
    val fundedFromSavings: Double = 0.0,
    val perDay: Double = 0.0,
    // D1 — spend on this trip paid from ANOTHER account. `spent` above stays this account's own, deliberately, so
    // a screen that hasn't been taught about this shows a total its own row list adds up to; `spentIncludingOtherAccounts`
    // is the combined figure and equals `spent` on an ordinary trip. Show `paidFromOtherAccounts` beside any total
    // that uses the combined one — an unlabelled shared figure is one nobody can reconcile.
    val spentIncludingOtherAccounts: Double = 0.0,
    val paidFromOtherAccounts: Double = 0.0,
    val bySourceAccount: List<TripSliceDto>? = null,
) {
    val isActive: Boolean get() = state == TripState.ACTIVE
    val isAwaitingStart: Boolean get() = state == TripState.AWAITING_START
    val isFinished: Boolean get() = state == TripState.FINISHED
    val overBudget: Boolean get() = budget?.let { it > 0.0 && spent > it } == true
}

/** The four trip states the server sends. Constants rather than an enum so an unknown value from a newer server
 *  degrades to "some other state" instead of throwing during deserialization. */
object TripState {
    const val UPCOMING = "upcoming"
    const val AWAITING_START = "awaiting-start"
    const val ACTIVE = "active"
    const val FINISHED = "finished"
}

/** A trip label (Stay, Travel, Food & drink…) — `categoryId` is where picking it files the expense. */
@Serializable
data class TripTagDto(val id: String, val name: String, val icon: String? = null, val categoryId: String? = null)

/** One wedge of a trip's spending — already labelled and already ranked by the server. */
@Serializable
data class TripSliceDto(
    val id: String,
    val label: String,
    val icon: String? = null,
    val amount: Double = 0.0,
    val count: Int = 0,
)

/** One expense on a trip, gathered by LINK across every period — so a March flight appears under a June trip.
 *  `when` is "before" | "during" | "after", resolved by the server against the trip's dates. */
@Serializable
data class TripExpenseRowDto(
    val id: String,
    val date: String,
    val amount: Double = 0.0,
    val note: String? = null,
    val categoryId: String = "",
    val categoryName: String = "",
    val categoryIcon: String? = null,
    val tagId: String? = null,
    val tagName: String? = null,
    val tagIcon: String? = null,
    val `when`: String = "during",
    // D1 — the account this row was paid from, when it was not this one. Null on an ordinary row. Show it: an
    // unlabelled foreign row reads as this account's spending, which is the one thing the link must never claim.
    val paidFromAccountId: String? = null,
    val paidFromAccountName: String? = null,
)

/** One trip opened up. `sliceAxis` ("tag" | "category") is the server's call, not ours: the tag split leads only
 *  when at least half the trip is labelled, and both clients must lead with the same one. `hasTagSlices`
 *  separates "mostly unlabelled" from "never labelled" — two different sentences under the chart. */
@Serializable
data class TripDetailDto(
    val trip: TripDto,
    val slices: List<TripSliceDto> = emptyList(),
    val sliceAxis: String = "category",
    val hasTagSlices: Boolean = false,
    val biggest: TripExpenseRowDto? = null,
    val expenses: List<TripExpenseRowDto> = emptyList(),
)

@Serializable
data class TripsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val trips: List<TripDto> = emptyList(),    // newest departure first
    val tripTags: List<TripTagDto> = emptyList(),
)

/** Create a trip. The dates say when to default new expenses to it and when to count down — they do NOT decide
 *  what is in it: an expense belongs because it carries the trip's id. */
@Serializable
data class CreateTripRequest(
    val name: String,
    val from: String,
    val to: String,
    val destination: String? = null,
    val icon: String? = null,
)

/** Edit a trip — a FULL REPLACE, like every other edit request on this API: an omitted field means "no longer set",
 *  not "leave alone". Send the whole intended state or you will clear the savings link, the budget and the rate. */
@Serializable
data class EditTripRequest(
    val name: String,
    val from: String,
    val to: String,
    val destination: String? = null,
    val icon: String? = null,
    val savingCategoryId: String? = null,
    val budget: Double? = null,
    val spendCurrency: String? = null,
    val rate: Double? = null,
    val categoryId: String? = null,
)

/** Attach an expense to a trip, or detach it with a null id. */
@Serializable
data class SetExpenseTripRequest(
    val tripId: String? = null,
    // D1 — the account that owns the trip, when it lives elsewhere. The expense does NOT move: it stays in this
    // account's period, spending and budgets. Trailing optional; nothing in the Compose UI sets it yet.
    val tripAccountId: String? = null,
)

/** Declare a trip over, or put it back on the road. */
@Serializable
data class FinishTripRequest(val finished: Boolean)

/** Confirm the trip has actually begun (or take that back) — the tap that turns "awaiting-start" into "active". */
@Serializable
data class StartTripRequest(val started: Boolean)

/** The signed-in user (GET /me). `provider` is "google"/"facebook" for external sign-in (no local password), else
 *  null. `id` tells "you" apart from the other people on a shared account — which member row wears the *you* tag,
 *  who can't be removed, and who is missing from the hand-over picker. `plan` ("free"/"pro"/"unlimited") decorates
 *  the Pro entry points; it never gates anything on its own — the server's 402 is the gate. */
@Serializable
data class UserDto(
    val id: String = "",
    val username: String = "",
    val email: String = "",
    val provider: String? = null,
    val avatar: String? = null,               // data-URL profile picture (provider-sourced for external logins)
    val emailVerified: Boolean = false,
    val twoFactorEnabled: Boolean = false,
    val plan: String = "",
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

// --- Sharing: invitations + membership ----------------------------------------------------------------
// The hero Pro feature, and the last thing a phone-only user couldn't do. Inviting is gated server-side
// (PlanFeatures.Share → 402); accepting never is, since one Pro on the account covers everyone on it.

/** POST /accounts/{id}/invitations — invite an existing user by username. Any contributor may send one. */
@Serializable
data class CreateInvitationRequest(val username: String)

/** GET /invitations/pending — an invitation waiting on the signed-in user. Only pending ones are ever returned,
 *  so `status` is informational; `accountName` and `invitedByUsername` are what the card actually shows. */
@Serializable
data class InvitationDto(
    val id: String,
    val accountId: String,
    val accountName: String = "",
    val invitedByUserId: String = "",
    val invitedByUsername: String = "",
    val status: String = "",
    val createdAt: String = "",
)

/** POST /invitations/{id}/accept — the account the caller has just joined, so the client can switch to it. */
@Serializable
data class AcceptInvitationDto(val accountId: String = "")

/**
 * GET /accounts/archived — an account this user deleted, still inside its grace window.
 *
 * ★ Deleting an account here is a **soft** delete: the server keeps it for 30 days and then purges it for good.
 * That undo is the whole reason this read exists on the phone — Android could already call `DELETE /accounts/{id}`
 * and had no way to reach `POST /accounts/{id}/reactivate`, so a mistake made on a phone could only be taken back
 * from a browser, and became permanent in silence if nobody did.
 *
 * `purgeAt` is an ISO-8601 instant; the UI turns it into "N days left" rather than showing a raw timestamp,
 * because the number that matters is how long is left to act.
 */
@Serializable
data class ArchivedAccountDto(
    val id: String,
    val name: String = "",
    val currency: String = "",
    val archivedAt: String = "",
    val purgeAt: String = "",
)

/** POST /accounts/{id}/transfer-ownership — hand the account to another current member (the caller stays on). */
@Serializable
data class TransferOwnershipRequest(val newOwnerUserId: String)

// --- Periods: the top-bar label + the lifecycle writes (roll forward / reschedule / undo) --------------

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

/** POST /accounts/{id}/periods/start-next — close the open period and open the next one.
 *
 *  The domain deliberately never reads a bank: the *caller* supplies what each fund really holds now, keyed by
 *  fund id, and those become the new period's opening balances. `syncedFundClosingBalance` is the one figure the
 *  client can't hand-enter — the bank-synced fund's balance at the closing period's end (informative only). A
 *  fund omitted from [fundOpenings] opens at zero, so send every non-synced fund. */
@Serializable
data class StartNextPeriodRequest(
    val copyBudgets: Boolean = false,
    val adjustBudgets: Boolean = false,
    val fundOpenings: Map<String, Double>? = null,
    val syncedFundClosingBalance: Double? = null,
    val today: String? = null,   // ISO yyyy-MM-dd; the server defaults to its own UTC today
)

/** PUT /accounts/{id}/periods/{index}/schedule — move a period's dates. Every later period shifts to stay
 *  contiguous, each keeping its own length. */
@Serializable
data class ReschedulePeriodRequest(val from: String, val to: String)

/** GET /accounts/{id}/bank/balance-at?date=… — the synced fund's balance recorded at that date, or null when
 *  sync history doesn't reach back that far. */
@Serializable
data class BankBalanceAtDto(val balance: Double? = null)

/** POST /accounts/{id}/contribution-categories — an income source. Needed by the rollover's "log as adjustment"
 *  path, which files unexplained money-IN under an "Adjustment" source. */
@Serializable
data class CreateContributionCategoryRequest(val name: String, val icon: String? = null)

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

// --- Notifications: the current period's alerts (GET /accounts/{id}/notifications) --------------------

/** One alert. `targetTab` names the tab that addresses it, so a tap can jump there. */
@Serializable
data class NotificationDto(
    val icon: String = "",
    val text: String = "",
    val desc: String? = null,
    val urgent: Boolean = false,
    val targetTab: String? = null,
)

@Serializable
data class NotificationsViewDto(
    val count: Int = 0,
    val urgentCount: Int = 0,
    val items: List<NotificationDto> = emptyList(),
)

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

/** One getting-started step. `key` is stable ("income" | "budget" | "expense" | "bucket"); `done` is derived
 *  server-side from the account, never re-derived here — the four rules for "have they done this yet" are the
 *  domain's, and a second reading of them in Kotlin is a second place to disagree with the web. */
@Serializable
data class OnboardingStepDto(val key: String, val title: String, val desc: String, val done: Boolean = false)

@Serializable
data class OnboardingViewDto(
    val dismissed: Boolean = false,
    val steps: List<OnboardingStepDto> = emptyList(),
)

/** GET /accounts/{id}/milestones — the Home tally only (earned / total / in progress). Deliberately separate from
 *  the full catalogue below: Home wants three integers on every visit, the catalogue only when the sheet opens. */
@Serializable
data class MilestonesDto(val earned: Int = 0, val total: Int = 0, val inProgress: Int = 0)

/** One achievement. `percent` is locked-progress (null once earned); `tier` is "Bronze" | "Silver" | "Gold", the
 *  medal metal; `earnedOn` is a yyyy-MM-dd stamp and is best-effort — null on anything the thick web app has
 *  never stamped. Title/desc arrive as English from the server (AchievementsView.cs's i18n note applies here too:
 *  the copy is baked server-side, so localizing it is one change on the server, not two clients). */
@Serializable
data class AchievementDto(
    val key: String,
    val icon: String = "",
    val title: String = "",
    val desc: String = "",
    val earned: Boolean = false,
    val percent: Int? = null,
    val tier: String = "Bronze",
    val earnedOn: String? = null,
)

/** GET /accounts/{id}/achievements — the whole catalogue, plus the same tallies the Home line shows. Both come
 *  from the one domain service, so the sheet and the line cannot disagree about how many are earned. */
@Serializable
data class AchievementsViewDto(
    val earned: Int = 0,
    val total: Int = 0,
    val inProgress: Int = 0,
    val items: List<AchievementDto> = emptyList(),
)

/** PUT /accounts/{id}/contribution-categories/{catId} — rename / re-icon an income source. */
@Serializable
data class EditContributionCategoryRequest(val name: String, val icon: String? = null)

/** Archive (hide) or restore a category/fund/bucket/tag. Reversible; keeps history. */
@Serializable
data class SetArchivedRequest(val archived: Boolean)

// --- Tag management (mirrors FinApp.Contracts) --------------------------------------------------------

@Serializable
data class CreateTagRequest(val name: String, val icon: String? = null, val isTripTag: Boolean = false)

/** The tag edit is a FULL replace on the server: an omitted `categoryId` CLEARS the F2 binding rather than
 *  leaving it alone, so the editor must always send back what it read. */
@Serializable
data class EditTagRequest(val name: String, val icon: String? = null, val categoryId: String? = null)

/** Label an expense (null clears it). One tag per expense is the model, though the stored field is a list. */
@Serializable
data class SetExpenseTagRequest(val tagId: String? = null)

/** One seeded trip label. The client sends its own localized names; the server ignores the whole call if the set
 *  already exists, so the split can't fork into two parallel label sets. */
@Serializable
data class TripTagSeed(val name: String, val icon: String? = null, val categoryId: String? = null)

@Serializable
data class SeedTripTagsRequest(val tags: List<TripTagSeed>)

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
    val tagId: String? = null,
    // File this straight onto a journey. The trip is a LINK, so this doesn't move the expense out of the period
    // it was paid in — which is exactly what lets a flight bought in March count toward a June trip.
    val tripId: String? = null,
    // "HH:mm" of day, stamped as NOW when logging something for today and left null otherwise. Never midnight —
    // a made-up 00:00 would sort ahead of everything genuinely logged that morning.
    val time: String? = null,
    // ⚠️ Only meaningful on an EDIT. An omitted time means "leave it alone" there, so clearing one needs a way to
    // say so that null cannot; on an add it is simply ignored.
    val clearTime: Boolean = false,
    // ⚠️ The same, for the label, and for the same reason — since S111 the server treats an omitted tag on an edit
    // as "leave it alone" (it used to CLEAR it, which is what stripped labels off rows edited from this app). So
    // "No label" now has to be said out loud.
    val clearTag: Boolean = false,
    // What was TYPED, before conversion, when the wallet holds foreign cash — `amount` is already the converted
    // figure. Display only: the row renders "€8.70 · 100.00 kr" from these, and they are recorded rather than
    // re-derived so editing the wallet's rate later can never rewrite what a past expense cost.
    // ⚠️ Meaningful on an ADD only. The edit route does not carry them; the server keeps the stored pair across an
    // edit, which is also why this sheet must not re-convert an amount it pre-filled.
    val foreignAmount: Double? = null,
    val foreignCurrency: String? = null,
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
    // --- edit-form prefill ---------------------------------------------------------------------------
    // Saving a bucket is a full overwrite, so the edit sheet has to send back everything it isn't editing.
    // `forecast` carries the debt/investment knobs; the four flat fields below carry the rest.
    val forecast: SavingBucketForecastDto? = null,
    val costs: List<PlannedCostDto>? = null,
    val debtInstallmentDay: Int? = null,
    val debtPaymentDriven: Boolean = false,
    val fundId: String? = null,
    val thresholdPercent: Double = 80.0,
    val notifyOnMilestone: Boolean = false,
    val initialAmount: Double = 0.0,
)

/** The raw projection knobs behind a debt / investment bucket — what the edit form prefills from (the flat
 *  `debtBalance` above is walked forward to today, whereas `debtStoredBalance` is the anchored figure). */
@Serializable
data class SavingBucketForecastDto(
    val demonstratedPace: Double? = null,
    val plannedContribution: Double? = null,
    val investmentRatePercent: Double? = null,
    val investmentTermYears: Double? = null,
    val investmentCompoundsPerYear: Int? = null,
    val debtStoredBalance: Double? = null,
    val debtOriginalBalance: Double? = null,
    val debtRatePercent: Double? = null,
    val debtInstallment: Double? = null,
    val debtBalanceAsOf: String? = null,
    val debtStartDate: String? = null,
)

/** One manual "Add to savings" deposit this period — the rows the activity list can edit or remove. */
@Serializable
data class SavingDepositRowDto(
    val id: String,
    val bucketId: String,
    val bucketName: String = "",
    val amount: Double = 0.0,
    val date: String = "",
    val note: String? = null,
)

/** One movement of money that is ALREADY saved: deployed to a fund, matured into a budget, moved to another bucket,
 *  or spent through the expense ledger. `amount` is always positive — the direction is `kind`, never the sign.
 *
 *  ⚠️ `undoable` is the SERVER's answer, not ours to infer. A "spent" row is a real movement but the undo endpoint
 *  refuses it (it is undone by deleting the expense), and the incoming half of a transfer is the outgoing half's
 *  reversal wearing a second button. Rendering an undo off `kind` alone produces controls that only ever 400. */
@Serializable
data class SavingMovementRowDto(
    val id: String,
    val bucketId: String,
    val bucketName: String = "",
    val kind: String = "",
    val amount: Double = 0.0,
    val date: String = "",
    val note: String? = null,
    val counterpart: String? = null,
    val undoable: Boolean = false,
)

@Serializable
data class SavingsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val availableToSave: Double = 0.0,
    val maxAdditionalSavings: Double = 0.0,
    val buckets: List<SavingBucketDto> = emptyList(),
    val deposits: List<SavingDepositRowDto> = emptyList(),
    val movements: List<SavingMovementRowDto> = emptyList(),
)

// --- Savings (Goals) write flows ---------------------------------------------------------------------

/** Generic write result: new version + the affected entity id. */
@Serializable
data class MutationResultDto(val version: Long = 0, val entityId: String? = null)

/** POST /accounts/{id}/installments — log a loan payment as its parts. `principalCategoryId` and
 *  `interestCategoryId` are the same category (the split shows in the Breakdown by tag on the web); the
 *  principal/interest tag ids and `additional` extra lines are web-only for now, so the phone sends them null. */
@Serializable
data class LogInstallmentRequest(
    val bucketId: String,
    val total: Double,
    val fundId: String,
    val date: String,               // ISO yyyy-MM-dd
    val principalCategoryId: String,
    val interestCategoryId: String,
    val additional: List<InstallmentExtraDto>? = null,
    val principalTagId: String? = null,
    val interestTagId: String? = null,
    val note: String? = null,
)

/** One non-loan line riding along on an installment (insurance, tax, a fee), with its own budget category. */
@Serializable
data class InstallmentExtraDto(
    val amount: Double,
    val categoryId: String,
    val tagId: String? = null,
    val note: String? = null,
)

/** POST /accounts — create a new budget account. Free plan is capped at one (the 2nd needs Pro → server 402). */
@Serializable
data class CreateAccountRequest(val name: String, val currency: String)

/** POST /accounts/{id}/bootstrap — seed a freshly-created account (default categories/funds + the first period).
 *  `today` (ISO yyyy-MM-dd) dates the first period to the caller's local month. */
@Serializable
data class BootstrapAccountRequest(val today: String? = null)

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

/** PUT /accounts/{id}/savings/deposits/{allocationId} — change a deposit's amount (its date is kept). */
@Serializable
data class EditSavingDepositRequest(val amount: Double)

/** POST /accounts/{id}/savings/disburse — deploy a bucket to its purpose via `fundId`. */
@Serializable
data class DisburseSavingRequest(
    val savingCategoryId: String,
    val fundId: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

/** POST /accounts/{id}/savings/to-budget — mature a bucket into a category's budget this period. */
@Serializable
data class ConvertSavingToBudgetRequest(
    val savingCategoryId: String,
    val categoryId: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

/** POST /accounts/{id}/savings/transfer — move money between buckets. Total-preserving. */
@Serializable
data class MoveSavingsRequest(
    val fromBucketId: String,
    val toBucketId: String,
    val amount: Double,
    val date: String,
    val note: String? = null,
)

/** POST /accounts/{id}/trips/{tripId}/use-savings — release a linked pot into the trip's budget. */
@Serializable
data class UseTripSavingsRequest(val amount: Double, val date: String, val note: String? = null)

/** One planned future cost of an expenses (sinking) fund. Pure planning data — it never moves money; the server
 *  turns the list into the monthly set-aside. `cadence` is "one-off"/"monthly"/"quarterly"/"yearly"; `dueDate`
 *  (ISO yyyy-MM-dd) applies only to a one-off. */
@Serializable
data class PlannedCostDto(
    val label: String,
    val amount: Double,
    val cadence: String,
    val dueDate: String? = null,
)

/**
 * Create (POST /savings/buckets) or reconfigure (PUT /savings/buckets/{id}) a bucket — one upsert covering all
 * four kinds. The server picks the kind from the flags in priority order: `isDebt` → `isInvestment` →
 * `isExpensesFund` → otherwise an ordinary goal. Send only the fields for the kind being saved; the rest keep
 * their defaults.
 *
 * `initialAmount` is honoured only while the account has a single period (money you already had before using
 * the app), which is why the sheet hides the field otherwise rather than sending a value that's silently dropped.
 */
@Serializable
data class SaveSavingBucketRequest(
    val name: String,
    val icon: String? = null,
    val goalAmount: Double? = null,
    val thresholdPercent: Double = 80.0,
    val notifyOnMilestone: Boolean = false,
    val initialAmount: Double = 0.0,
    val isDebt: Boolean = false,
    val debtBalance: Double = 0.0,
    val debtRate: Double = 0.0,
    val debtInstallment: Double = 0.0,
    // Lets the client send the "original principal + already paid" input mode (original >= current); null keeps
    // the default of original = current balance, which reads as 0% paid off.
    val debtOriginalBalance: Double? = null,
    val debtInstallmentDay: Int? = null,
    val debtStartDate: String? = null,   // ISO yyyy-MM-dd; makes "interest paid so far" exact instead of estimated
    val plannedContribution: Double? = null,
    val isInvestment: Boolean = false,
    val invRate: Double = 0.0,
    val invTermYears: Double = 0.0,
    val invCompounds: Int = 12,
    val fundId: String? = null,
    val costs: List<PlannedCostDto>? = null,
    val isExpensesFund: Boolean = false,
    val debtPaymentDriven: Boolean = false,
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
    // The foreign cash this wallet holds and its rate into the account's currency — see FundOptionDto.
    val currency: String? = null,
    val rate: Double? = null,
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

/** One transfer of money to ANOTHER account — distinct from [FundTransferRowDto], which moves money between wallets
 *  inside this account and never changes the total.
 *
 *  `pairId` is the link both halves carry and the id the edit/delete endpoints take; it is null for a transfer
 *  written before that link existed, which is why `editable` is a separate flag the server answers rather than
 *  something to infer. `toAccountName` is null when the caller can no longer see that account — the row still shows,
 *  because the money left either way and hiding it would make the balance unexplainable. */
@Serializable
data class AccountTransferRowDto(
    val id: String,
    val pairId: String? = null,
    val fromFundId: String = "",
    val fromFundName: String = "",
    val toAccountId: String? = null,
    val toAccountName: String? = null,
    val amount: Double = 0.0,
    val date: String = "",
    val note: String? = null,
    val editable: Boolean = false,
)

@Serializable
data class WalletsViewDto(
    val version: Long = 0,
    val currency: String = "",
    val overview: AccountOverviewDto,
    val funds: List<FundRowDto> = emptyList(),
    val archivedFunds: List<FundRowDto> = emptyList(),
    val transfers: List<FundTransferRowDto> = emptyList(),
    val accountTransfers: List<AccountTransferRowDto> = emptyList(),
)

/** POST /accounts/{id}/transfers-out — send money to another account you belong to (same currency).
 *  Empty `destinationFundId` picks the destination's first unsynced wallet. */
@Serializable
data class TransferToAccountRequest(
    val destinationAccountId: String,
    val fromFundId: String,
    val amount: Double,
    val destinationFundId: String? = null,
    val note: String? = null,
    val date: String? = null,
)

/** The empty Guid, which several handlers read as "you choose" rather than as a value. */
const val EMPTY_GUID = "00000000-0000-0000-0000-000000000000"

/**
 * POST /accounts/{id}/expenses/{expenseId}/settle — push part of an expense you paid on behalf of another account
 * onto that account: it records a matching expense there and reduces this one by the same amount, atomically.
 *
 * ⚠️ The destination **fund and category are sent empty on purpose.** The server falls back to the destination's
 * first spendable wallet and its first category when they are `Guid.Empty` — the same accommodation
 * `SpendFromSavingsRequest` documents. The alternative would be for the phone to fetch another account's whole
 * structure just to fill two pickers, and the web's own defaults are what it would land on anyway.
 */
@Serializable
data class SettleExpenseRequest(
    val destinationAccountId: String,
    val destinationFundId: String = EMPTY_GUID,
    val destinationCategoryId: String = EMPTY_GUID,
    val amount: Double,
    val note: String? = null,
)

/**
 * POST /accounts/{id}/expenses/{expenseId}/refund — money back on an expense.
 *
 * ★ [amount] is what came back NOW, not the running total. The server adds it to whatever has already come back,
 * inside the same lock that writes it, so two phones acking two credits against one bill both land. Sending a total
 * would mean read-modify-write, and the second phone's stale read would silently erase the first phone's refund.
 */
@Serializable
data class RefundExpenseRequest(val amount: Double)

/** PUT /accounts/{id}/account-transfers/{pairId} — rewrite BOTH halves at once. Null fund ids and a null date keep
 *  what the transfer already has. */
@Serializable
data class EditAccountTransferRequest(
    val destinationAccountId: String,
    val amount: Double,
    val fromFundId: String? = null,
    val destinationFundId: String? = null,
    val note: String? = null,
    val date: String? = null,
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

/** PUT /accounts/{id}/fund-transfers/{transferId} — retarget/re-price an existing transfer. The original date is
 *  preserved server-side (there is no date field), so this edits the *what*, never the *when*. */
@Serializable
data class EditFundTransferRequest(
    val fromFundId: String,
    val toFundId: String,
    val amount: Double,
    val note: String? = null,
)

// --- Account settings (mirrors FinApp.Contracts.AccountSettings) ---------------------------------------

/** GET /accounts/{id}/settings. `savingsRateTarget` is a fraction 0..1; the UI shows it as a percent.
 *  ⚠️ This is the *whole* thin settings surface — F4 round-up config (step + destination bucket) is NOT on it,
 *  and has no command endpoint either (`BudgetingState.ConfigureRoundUps` is still a whole-snapshot push marked
 *  TODO(cutover)), so a thin client cannot offer round-ups until the server grows both. */
@Serializable
data class AccountSettingsDto(
    val name: String = "",
    val currency: String = "",
    val savingsRateTarget: Double = 0.20,
)

/** PUT /accounts/{id}/savings-target. `percent` is 0..100 (the server stores it as a fraction and 400s outside
 *  that range), so the client clamps rather than sending something it knows will be refused. */
@Serializable
data class SetSavingsTargetRequest(val percent: Double)

// --- Fund CRUD (mirrors the web's Wallets fund actions) ------------------------------------------------

/** POST /accounts/{id}/funds. `parentId` nests an informational sub-fund — the web only ever creates top-level
 *  funds from its Wallets section, and so do we. */
@Serializable
data class CreateFundRequest(
    val name: String,
    val parentId: String? = null,
    val note: String? = null,
    val icon: String? = null,
)

/** PUT /accounts/{id}/funds/{fundId}. ⚠️ A full OVERWRITE of (name, note, icon), not a patch: the server calls
 *  RenameFund + SetFundNote + SetFundIcon unconditionally, so an omitted field is *cleared*, not kept. Send the
 *  row's current values for anything the form didn't touch — `FundRowDto.icon` is the raw stored icon precisely
 *  so it can be round-tripped (the name-based fallback is applied for display only). */
@Serializable
data class EditFundRequest(
    val name: String,
    val note: String? = null,
    val icon: String? = null,
)

/** PUT /accounts/{id}/funds/{fundId}/opening-balance — what the fund held at the start of the open period.
 *  Overwrites any existing opening balance. */
@Serializable
data class SetFundOpeningBalanceRequest(val amount: Double)

/** A saved "always file this merchant here" rule. `matchKey` is the normalized description the rule is stored
 *  under; `kind` is "category" (file it as spending) or "fund" (it came from one of your own wallets, so it is a
 *  transfer). `tagId` is an optional label the rule also applies — debit rules only. */
@Serializable
data class BankMappingDto(
    val matchKey: String,
    val kind: String,
    val targetId: String,
    val tagId: String? = null,
)

/** PUT /accounts/{id}/bank/mappings — save a rule from a transaction's description. ⚠️ The rule is written WHOLE
 *  every time, so an absent tag is a CLEARED tag, not an untouched one. That is the opposite of the expense-edit
 *  rule and it is deliberate: there is no older client to protect on this route. */
@Serializable
data class SetBankMappingRequest(
    val description: String,
    val kind: String,
    val targetId: String,
    val tagId: String? = null,
)

/** One reviewed statement row, ready to post. `amount` is SIGNED — negative books an expense against a spend
 *  category, positive books income against a contribution category — and the server validates the category against
 *  the sign, so the two must be chosen together. */
@Serializable
data class ImportRowDto(
    val amount: Double,
    val date: String,
    val categoryId: String,
    val fundId: String,
    val note: String? = null,
)

/** POST /accounts/{id}/import — a batch of reviewed rows in ONE save, all-or-nothing: a row naming a category or
 *  fund that doesn't exist fails the whole batch with 400 rather than importing most of a statement.
 *
 *  `skipDuplicates` compares each row against what is ALREADY in the period (same date + amount + fund), so
 *  re-importing the same statement is safe. ⚠️ Duplicates *within* one batch still post — two identical coffees on
 *  one day are a real thing that happens, and the check is against pre-existing data only. */
@Serializable
data class ImportTransactionsRequest(val rows: List<ImportRowDto>, val skipDuplicates: Boolean = true)

/** What an import did: how many rows posted, how many were dropped as unusable, and how many the server recognised
 *  as already present. All three are reported — "imported 12" alone leaves the other rows unaccounted for. */
@Serializable
data class ImportResultDto(
    val version: Long = 0,
    val imported: Int = 0,
    val skipped: Int = 0,
    val duplicates: Int = 0,
)

/** PUT /accounts/{id}/funds/{fundId}/currency — make this wallet a pile of foreign cash, or put it back to the
 *  account's own currency.
 *
 *  ⚠️ Both null CLEARS it, and clearing is deliberately NOT Pro-gated on the server: a downgrade has to be able to
 *  return a wallet to the account currency. Setting one is gated on the trips feature.
 *
 *  ⚠️ Its own endpoint, separate from [EditFundRequest] — which is a full overwrite of (name, note, icon), so
 *  folding the currency into it would mean every rename re-stated the rate, and a client that had not learned
 *  about currencies yet would wipe it. Write this only when it actually changed, or a Free user renaming an
 *  ordinary wallet trips a paywall over a field they never touched. */
@Serializable
data class SetFundCurrencyRequest(val currency: String? = null, val rate: Double? = null)

/** What a wallet editor decided about the currency — both null meaning "an ordinary wallet in the account's own
 *  money". Distinct from the request because the ABSENCE of one of these means "not touched, don't write", which
 *  is the state that keeps a rename off a Pro-gated endpoint. */
data class FundCurrencyEdit(val currency: String?, val rate: Double?)

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
    val categoryId: String = "",
    val categoryName: String = "",
    val fundId: String = "",
    val fundName: String = "",
    val active: Boolean = true,
    val due: Boolean = false,
    val upcoming: Boolean = false,
    val daysUntilDue: Int = 0,
    val hasKnownAmount: Boolean = true,
    val autoPost: Boolean = false,
    // Set when this bill services a loan: posting it splits into interest/principal rows against that debt.
    val linkedDebtBucketId: String? = null,
    val linkedDebtName: String? = null,
    // Deliberately skipped this period rather than posted — the only state /unskip will undo. A skip removes the
    // bill from "still due", which moves safe-to-spend, so it stays visible with a way back.
    val skippedThisPeriod: Boolean = false,
    // Still expected this period. NOT derivable from `due`/`upcoming` — an item due in three weeks is pending but
    // neither, so without this the list cannot tell "not yet" from "already done" (O5).
    val pending: Boolean = false,
    // Where the part of this bill ABOVE the loan's contractual installment is filed — insurance, a servicing fee.
    // Null means nowhere was named, so all of it services the loan (what happened before this existed). Read-only
    // here for now: nothing in the Compose editor sets it yet, and an older server simply sends nothing.
    val excessCategoryId: String? = null,
    val excessCategoryName: String? = null,
    val excessLabel: String? = null,
)

/** A debt bucket a bill can be linked to. `paymentDriven` mirrors the bucket's "I log each installment here"
 *  switch — a linked bill only drives the balance when it's on, so the editor hints rather than flipping it. */
@Serializable
data class DebtOptionDto(val id: String, val name: String, val paymentDriven: Boolean = false)

/** The Recurring surface in one read. The pickers travel with it, so the editor needs no second call: `categories`
 *  for a bill, `contributionCategories` for income, `funds`, and the `debts` a bill can service. */
@Serializable
data class RecurringViewDto(
    val version: Long = 0,
    val currency: String = "",
    val billsDue: Double = 0.0,
    val items: List<RecurringRowDto> = emptyList(),
    val categories: List<CategoryOptionDto> = emptyList(),
    val contributionCategories: List<CategoryOptionDto> = emptyList(),
    val funds: List<FundOptionDto> = emptyList(),
    val debts: List<DebtOptionDto> = emptyList(),
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

/** POST /accounts/{id}/recurring — a new bill or income expectation. `kind` is "expense"/"income" and `mode` is
 *  "fixed"/"typical"/"reminder" (language-independent strings the server maps to its enums). */
@Serializable
data class AddRecurringRequest(
    val name: String,
    val kind: String,
    val mode: String,
    val expected: Double,
    val dayOfMonth: Int,
    val categoryId: String,
    val fundId: String,
    val icon: String? = null,
    val autoPost: Boolean = false,
    val linkedDebtBucketId: String? = null,
    // File the part above the loan's contractual installment here instead of onto the loan as principal.
    val excessCategoryId: String? = null,
    val excessLabel: String? = null,
)

/** PUT /accounts/{id}/recurring/{recurringId} — edit an item (its kind can't change). A null
 *  `linkedDebtBucketId` is authoritative: it unlinks. */
@Serializable
data class UpdateRecurringRequest(
    val name: String,
    val mode: String,
    val expected: Double,
    val dayOfMonth: Int,
    val categoryId: String,
    val fundId: String,
    val icon: String? = null,
    val autoPost: Boolean = false,
    val linkedDebtBucketId: String? = null,
    // NOT authoritative, unlike `linkedDebtBucketId` above: null leaves the excess configuration alone, the empty
    // GUID clears it, an id sets it. That asymmetry exists FOR this client — sending null here (which is what an
    // editor that doesn't know about the field does) must not wipe a setting made on the web.
    val excessCategoryId: String? = null,
    val excessLabel: String? = null,
)

/** PUT /accounts/{id}/recurring/{recurringId}/active — pause or resume (a paused item never falls due). */
@Serializable
data class SetActiveRequest(val active: Boolean)

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

// --- Plans and the paywall ----------------------------------------------------------------------------
// The server owns the CATALOGUE (which features exist, and where the Pro line falls); the client owns the
// WORDING. That split is why these carry stable keys rather than display strings — and it is why the phone must
// not hard-code the free/pro split: a list written here would drift from the gates the server enforces.

/** One capability and which tiers include it. Mirrors PlanFeatureDto on the server. */
@Serializable
data class PlanFeatureDto(val key: String = "", val inFree: Boolean = false, val inPro: Boolean = false)

/** GET /plans — the tier table plus the caller's resolved plan. [enabled] is the global monetization flag: while
 *  it is off nothing is on sale, which is the state during beta. ⚠️ Gating still follows the PLAN, not this flag —
 *  a post-cap beta account resolves to "free" and is gated while billing is off. */
@Serializable
data class PlansDto(
    val enabled: Boolean = false,
    val currentPlan: String = "",
    val isBetaCohort: Boolean = false,
    val currency: String = "",
    val annualPrice: String = "",
    val monthlyPrice: String = "",
    val features: List<PlanFeatureDto> = emptyList(),
    val paymentProvider: String = "",
    val paymentSandbox: Boolean = false,
)

/** The feature keys the server gates on (PlanFeatures in FinApp.Contracts). Used to ASK a gate, never to decide
 *  one — which tier a key belongs to comes from [PlansDto.features]. */
object PlanFeatures {
    const val BUDGETS = "budgets"
    const val GOALS = "goals"
    const val EXPORT = "export"
    const val SECURITY = "security"
    const val SHARE = "share"
    const val IMPORT = "import"
    const val DEBT = "debt"
    const val INSIGHTS = "insights"
    const val HISTORY = "history"
    const val CAPS = "caps"

    /** Trips. The line is **starting a journey, not running one**: Free cannot create a trip, edit one, fund it
     *  from a savings pot, or spend it in another currency — but on a trip it already has it may always read,
     *  start, finish (early, and undo), attach/detach while it still runs, and delete. A paywall must never
     *  strand state; see PlanFeatures.Trips on the server for the whole line. */
    const val TRIPS = "trips"
}
