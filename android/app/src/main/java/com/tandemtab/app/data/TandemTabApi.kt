package com.tandemtab.app.data

import com.tandemtab.app.BuildConfig
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.defaultRequest
import io.ktor.client.request.delete
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.put
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.json.Json
import java.time.Instant
import java.time.OffsetDateTime

/** Raised for a non-2xx response so the UI can show a message instead of crashing. */
class ApiException(val status: Int, override val message: String) : Exception(message)

/**
 * Thin HTTP client over the TandemTab server API. Holds the session in memory and mirrors it to a
 * [TokenStore] so it survives an app restart. The access token is short-lived; a long-lived refresh
 * token (rotated on every use) transparently re-mints it — proactively when it's about to expire, and
 * reactively on a 401. One instance per app.
 */
class TandemTabApi(
    private val baseUrl: String = BuildConfig.API_BASE_URL,
    private val store: TokenStore? = null,
) {
    @Volatile
    var accessToken: String? = null
        private set

    @Volatile
    private var refreshToken: String? = null

    @Volatile
    private var expiresAt: Instant? = null

    // Refresh a little before the access token actually lapses, to dodge clock skew and in-flight latency.
    private val expirySkew = java.time.Duration.ofSeconds(60)

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        encodeDefaults = true
    }

    private val client = HttpClient(OkHttp) {
        // Non-2xx responses are inspected by hand in each call, so keep the raw status rather than throwing.
        expectSuccess = false
        install(ContentNegotiation) { json(json) }
        defaultRequest {
            url(baseUrl)
            contentType(ContentType.Application.Json)
        }
    }

    /** Seed in-memory tokens from the persisted session. Returns it so the caller can show the last identity. */
    suspend fun restore(): SavedSession? {
        val s = store?.load() ?: return null
        accessToken = s.accessToken
        refreshToken = s.refreshToken
        expiresAt = parseInstant(s.expiresAt)
        return s
    }

    suspend fun login(usernameOrEmail: String, password: String): LoginResponse {
        val resp = client.post("/auth/login") {
            setBody(LoginRequest(usernameOrEmail.trim(), password))
        }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, loginError(resp.status))
        }
        val result: LoginResponse = resp.body()
        result.auth?.let { adoptSession(it) }
        return result
    }

    /** Register a new account. The server returns tokens (auto sign-in), mirroring the web. */
    suspend fun register(username: String, email: String, password: String): AuthResponse {
        val resp = client.post("/auth/register") {
            setBody(RegisterRequest(username.trim(), email.trim(), password))
        }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, serverMessageOr(resp.bodyAsText(), "Couldn't create your account."))
        }
        val result: AuthResponse = resp.body()
        adoptSession(result)
        return result
    }

    /** Request a password-reset link. Always succeeds (never reveals whether the identifier matched). */
    suspend fun forgotPassword(identifier: String) {
        val resp = client.post("/auth/password/forgot") { setBody(ForgotPasswordRequest(identifier.trim())) }
        if (resp.status.value !in 200..299 && resp.status.value != 204) {
            throw ApiException(resp.status.value, "Couldn't send the reset link. Try again.")
        }
    }

    /** Which external sign-in providers the server has configured (controls which buttons to show). */
    suspend fun getProviders(): ExternalProvidersDto {
        val resp = client.get("/auth/providers")
        if (resp.status.value !in 200..299) return ExternalProvidersDto()
        return resp.body()
    }

    /** The URL to open in a browser to start an external sign-in; `native=1` tells the server to
     *  redirect the result back into the app via the com.tandemtab.app:// deep link. */
    fun externalAuthUrl(provider: String): String = "$baseUrl/auth/external/$provider?native=1"

    /** Exchange the one-time code from the external-sign-in deep link for a session. Returns the LoginResponse
     *  envelope — like /auth/login it can 2FA-gate, so the caller handles twoFactorRequired. */
    suspend fun exchangeCode(code: String): LoginResponse {
        val resp = client.post("/auth/exchange") { setBody(ExchangeCodeRequest(code)) }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, "Sign-in didn't complete. Please try again.")
        }
        val result: LoginResponse = resp.body()
        result.auth?.let { adoptSession(it) }
        return result
    }

    /** Complete a 2FA-gated sign-in with the ticket from login/exchange plus a TOTP or recovery code. */
    suspend fun twoFactor(ticket: String, code: String): AuthResponse {
        val resp = client.post("/auth/2fa") { setBody(TwoFactorLoginRequest(ticket, code.trim())) }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, when (resp.status.value) {
                401, 400 -> "That code didn't match. Try again."
                429 -> "Too many attempts. Wait a moment and try again."
                else -> "Couldn't verify the code (${resp.status.value})."
            })
        }
        val result: AuthResponse = resp.body()
        adoptSession(result)
        return result
    }

    suspend fun listAccounts(): List<AccountSummaryDto> = authedGet("/accounts").body()

    /** Create a new budget account (name + ISO currency). Free plan is capped at one — the 2nd needs Pro, surfaced
     *  as the server's 402. Returns the created account summary. */
    suspend fun createAccount(req: CreateAccountRequest): AccountSummaryDto = authedPost("/accounts", req).body()

    /** Seed a freshly-created account server-side (default categories/funds + the first period). `today` dates the
     *  first period to the caller's local month. 409 if already set up. */
    suspend fun bootstrapAccount(accountId: String, today: String): MutationResultDto =
        authedPost("/accounts/$accountId/bootstrap", BootstrapAccountRequest(today)).body()

    suspend fun spending(accountId: String, period: Int? = null): SpendingViewDto = authedGet("/accounts/$accountId/spending${periodQ(period)}").body()

    /** Per-category budget coverage (allocated / spent / remaining) for the Spending → Categories view. */
    suspend fun budgets(accountId: String, period: Int? = null): BudgetsViewDto = authedGet("/accounts/$accountId/budgets${periodQ(period)}").body()

    /** Upsert a category's budget for the current period; returns a refreshed budgets view to reconcile from. */
    suspend fun setBudget(accountId: String, categoryId: String, req: SetBudgetRequest): BudgetMutationDto =
        authedPut("/accounts/$accountId/budgets/$categoryId", req).body()

    /** Remove a category's budget for the current period; returns a refreshed budgets view. */
    suspend fun removeBudget(accountId: String, categoryId: String): BudgetMutationDto =
        authedDelete("/accounts/$accountId/budgets/$categoryId").body()

    /** Add a spend category (optionally nested under a parent). Re-fetch /spending after to refresh the list. */
    suspend fun createCategory(accountId: String, req: CreateCategoryRequest): MutationResultDto =
        authedPost("/accounts/$accountId/categories", req).body()

    /** Edit a spend category's name and icon (a null icon clears it). */
    suspend fun editCategory(accountId: String, categoryId: String, req: EditCategoryRequest): MutationResultDto =
        authedPut("/accounts/$accountId/categories/$categoryId", req).body()

    /** Archive (hide) or restore a spend category. */
    suspend fun archiveCategory(accountId: String, categoryId: String, archived: Boolean): MutationResultDto =
        authedPut("/accounts/$accountId/categories/$categoryId/archived", SetArchivedRequest(archived)).body()

    suspend fun savings(accountId: String, period: Int? = null): SavingsViewDto = authedGet("/accounts/$accountId/savings${periodQ(period)}").body()

    /** Earmark money into a savings bucket. Returns a refreshed Savings view to reconcile without a re-fetch. */
    suspend fun allocateSaving(accountId: String, req: AddSavingDepositRequest): SavingsMutationDto =
        authedPost("/accounts/$accountId/savings/deposits", req).body()

    /** Draw a bucket down as a real expense (also lands in Spending). Returns version + the new expense id. */
    suspend fun spendFromSavings(accountId: String, req: SpendFromSavingsRequest): MutationResultDto =
        authedPost("/accounts/$accountId/savings/spend", req).body()

    /** Log a loan installment against a debt bucket — posts interest/principal (+ any extra) rows as one linked
     *  group and, on a payment-driven bucket, takes the principal off the balance. Also lands in Spending. The
     *  server returns a richer InstallmentMutationDto; we only need the write to succeed, so read the lean shape. */
    suspend fun logInstallment(accountId: String, req: LogInstallmentRequest): MutationResultDto =
        authedPost("/accounts/$accountId/installments", req).body()

    /** Undo a logged installment — removes **every** row of it and gives a payment-driven debt its principal back.
     *  The server refuses to remove half a payment, which is why this takes the group id, not an expense id. */
    suspend fun deleteInstallment(accountId: String, groupId: String): MutationResultDto =
        authedDelete("/accounts/$accountId/installments/$groupId").body()

    /** Create a savings bucket (goal / debt / investment / expenses fund). Rejects a duplicate name. */
    suspend fun createSavingBucket(accountId: String, req: SaveSavingBucketRequest): MutationResultDto =
        authedPost("/accounts/$accountId/savings/buckets", req).body()

    /** Reconfigure a bucket. ⚠️ A full OVERWRITE, not a patch — every field in the request is applied, so send the
     *  bucket's current values for anything the form doesn't edit or they are cleared. */
    suspend fun editSavingBucket(accountId: String, bucketId: String, req: SaveSavingBucketRequest): MutationResultDto =
        authedPut("/accounts/$accountId/savings/buckets/$bucketId", req).body()

    /** Archive (hide) or restore a bucket. Reversible; keeps its history and its money. */
    suspend fun archiveSavingBucket(accountId: String, bucketId: String, archived: Boolean): MutationResultDto =
        authedPut("/accounts/$accountId/savings/buckets/$bucketId/archived", SetArchivedRequest(archived)).body()

    /** Delete a bucket outright. 400s when the domain blocks it (sub-buckets, or savings activity to preserve) —
     *  that message is worth showing verbatim, since "archive instead" is the answer. */
    suspend fun deleteSavingBucket(accountId: String, bucketId: String): MutationResultDto =
        authedDelete("/accounts/$accountId/savings/buckets/$bucketId").body()

    suspend fun wallets(accountId: String, period: Int? = null): WalletsViewDto = authedGet("/accounts/$accountId/wallets${periodQ(period)}").body()

    suspend fun income(accountId: String): IncomeViewDto = authedGet("/accounts/$accountId/income").body()

    /** Move money between two funds. Returns a refreshed Wallets view to reconcile without a re-fetch. */
    suspend fun transferFunds(accountId: String, req: TransferFundsRequest): FundMutationDto =
        authedPost("/accounts/$accountId/fund-transfers", req).body()

    /** Retarget/re-price a transfer already recorded this period. Its original date is kept by the server. */
    suspend fun editFundTransfer(accountId: String, transferId: String, req: EditFundTransferRequest): MutationResultDto =
        authedPut("/accounts/$accountId/fund-transfers/$transferId", req).body()

    /** Undo a transfer. 400s if it isn't in the open period (history isn't rewritten). */
    suspend fun deleteFundTransfer(accountId: String, transferId: String): MutationResultDto =
        authedDelete("/accounts/$accountId/fund-transfers/$transferId").body()

    /** The account's editable settings (name, currency, savings-rate target). */
    suspend fun accountSettings(accountId: String): AccountSettingsDto =
        authedGet("/accounts/$accountId/settings").body()

    /** Set the account's target savings rate. [percent] is 0..100; outside that the domain 400s. */
    suspend fun setSavingsTarget(accountId: String, percent: Double): MutationResultDto =
        authedPut("/accounts/$accountId/savings-target", SetSavingsTargetRequest(percent)).body()

    /** Create a fund. Returns a refreshed Wallets view (and the new fund's id as `entityId`). */
    suspend fun createFund(accountId: String, req: CreateFundRequest): FundMutationDto =
        authedPost("/accounts/$accountId/funds", req).body()

    /** Rename / re-note / re-icon a fund. ⚠️ A full overwrite — see [EditFundRequest]. */
    suspend fun editFund(accountId: String, fundId: String, req: EditFundRequest): MutationResultDto =
        authedPut("/accounts/$accountId/funds/$fundId", req).body()

    /** Archive (hide) or restore a fund. Reversible; keeps its history. Does *not* move any money — the caller
     *  transfers a remaining balance out first, exactly as the web does. */
    suspend fun archiveFund(accountId: String, fundId: String, archived: Boolean): MutationResultDto =
        authedPut("/accounts/$accountId/funds/$fundId/archived", SetArchivedRequest(archived)).body()

    /** Remove a fund for good. Pass [moveOpeningBalancesTo] to land its opening balance on another top-level fund
     *  first (total-preserving); without it the balance is dropped with the fund. 400s on a domain blocker
     *  (sub-funds / the only fund / referenced by an expense or transfer) — show that message verbatim, since
     *  "archive it instead" is the answer. */
    suspend fun deleteFund(accountId: String, fundId: String, moveOpeningBalancesTo: String? = null): MutationResultDto =
        authedDelete(
            "/accounts/$accountId/funds/$fundId" +
                (moveOpeningBalancesTo?.let { "?moveOpeningBalancesTo=$it" } ?: ""),
        ).body()

    /** Set what a fund held at the start of the open period (overwrites any existing opening balance). */
    suspend fun setFundOpeningBalance(accountId: String, fundId: String, amount: Double): MutationResultDto =
        authedPut("/accounts/$accountId/funds/$fundId/opening-balance", SetFundOpeningBalanceRequest(amount)).body()

    /** Record income into a fund (a deposit). Returns the recomputed overview. */
    suspend fun addDeposit(accountId: String, req: AddDepositRequest): DepositMutationDto =
        authedPost("/accounts/$accountId/deposits", req).body()

    /** Edit an existing deposit (the income "edit last"). Reuses the add-deposit body; returns the recomputed overview. */
    suspend fun editDeposit(accountId: String, depositId: String, req: AddDepositRequest): DepositMutationDto =
        authedPut("/accounts/$accountId/deposits/$depositId", req).body()

    suspend fun overview(accountId: String, period: Int? = null): AccountOverviewDto = authedGet("/accounts/$accountId/overview${periodQ(period)}").body()

    /** The account's periods (oldest→newest) + the current index — used for the top-bar period label. */
    suspend fun periods(accountId: String): PeriodsViewDto = authedGet("/accounts/$accountId/periods").body()

    /** Roll into the next period: close the open one and open the next, carrying the supplied opening balances.
     *  400s if the current period hasn't ended yet (the server mirrors the web's CanStartNextPeriod guard). */
    suspend fun startNextPeriod(accountId: String, req: StartNextPeriodRequest): MutationResultDto =
        authedPost("/accounts/$accountId/periods/start-next", req).body()

    /** Move a period's date range (later periods shift to stay contiguous). Index is positional, oldest = 0. */
    suspend fun reschedulePeriod(accountId: String, index: Int, req: ReschedulePeriodRequest): MutationResultDto =
        authedPut("/accounts/$accountId/periods/$index/schedule", req).body()

    /** Undo the last rollover: delete the newest period and everything in it, re-opening the previous one.
     *  400s when it's the only period. */
    suspend fun removeLatestPeriod(accountId: String): MutationResultDto =
        authedDelete("/accounts/$accountId/periods/latest").body()

    /** The synced fund's balance as recorded at `dateIso` (yyyy-MM-dd). Null — including on any non-2xx, since
     *  this is a best-effort input to the rollover, not a gate on it. */
    suspend fun bankBalanceAt(accountId: String, dateIso: String): Double? = runCatching {
        authedGet("/accounts/$accountId/bank/balance-at?date=$dateIso").body<BankBalanceAtDto>().balance
    }.getOrNull()

    /** Add an income-source (contribution) category. Used by the rollover's "log as adjustment" path. */
    suspend fun createContributionCategory(accountId: String, req: CreateContributionCategoryRequest): MutationResultDto =
        authedPost("/accounts/$accountId/contribution-categories", req).body()

    /** The Health/Insights read: score, savings rate, outgoings trend, signals, mini-trends, quick wins. */
    suspend fun insights(accountId: String): InsightsDto = authedGet("/accounts/$accountId/insights").body()

    /** The Home cash runway ("At this rate…"). Server returns 204 when there's no trustworthy basis → null. */
    suspend fun runway(accountId: String): RunwayDto? {
        val resp = authedGet("/accounts/$accountId/runway")
        if (resp.status == HttpStatusCode.NoContent) return null
        return resp.body()
    }

    /** The Home "on track for" targets (debt-free date + each savings goal's projected month). */
    suspend fun targets(accountId: String): TargetsDto = authedGet("/accounts/$accountId/targets").body()

    /** The alerts for the CURRENT period — deficit, over-budget categories, bills due, no income yet. Home shows
     *  only the urgent ones; the server has always sent them, this client simply never asked. */
    suspend fun notifications(accountId: String): NotificationsViewDto = authedGet("/accounts/$accountId/notifications").body()

    /** Recurring bills / income expectations with their due state for the open period. */
    suspend fun recurring(accountId: String, period: Int? = null): RecurringViewDto = authedGet("/accounts/$accountId/recurring${periodQ(period)}").body()

    /** Confirm a due bill/income (posts a real expense/income with the actual amount). Returns a refreshed view. */
    suspend fun confirmRecurring(accountId: String, recurringId: String, req: ConfirmRecurringRequest): RecurringMutationDto =
        authedPost("/accounts/$accountId/recurring/$recurringId/confirm", req).body()

    /** Skip a due item for this period (marks it handled, posts nothing). Returns a refreshed view. */
    suspend fun skipRecurring(accountId: String, recurringId: String): RecurringMutationDto =
        authedPostEmpty("/accounts/$accountId/recurring/$recurringId/skip").body()

    /**
     * Undo a skip — the item falls due again this period. The server refuses if the item was CONFIRMED rather than
     * skipped, since re-arming a bill whose expense is already booked invites a second payment.
     */
    suspend fun unskipRecurring(accountId: String, recurringId: String): RecurringMutationDto =
        authedPostEmpty("/accounts/$accountId/recurring/$recurringId/unskip").body()

    /** Declare a new bill / income expectation. */
    suspend fun addRecurring(accountId: String, req: AddRecurringRequest): MutationResultDto =
        authedPost("/accounts/$accountId/recurring", req).body()

    /** Edit an item (kind can't change). */
    suspend fun updateRecurring(accountId: String, recurringId: String, req: UpdateRecurringRequest): MutationResultDto =
        authedPut("/accounts/$accountId/recurring/$recurringId", req).body()

    /** Pause or resume an item. */
    suspend fun setRecurringActive(accountId: String, recurringId: String, active: Boolean): MutationResultDto =
        authedPut("/accounts/$accountId/recurring/$recurringId/active", SetActiveRequest(active)).body()

    /** Remove an item for good (its posted expenses/income stay). */
    suspend fun deleteRecurring(accountId: String, recurringId: String): MutationResultDto =
        authedDelete("/accounts/$accountId/recurring/$recurringId").body()

    /** Recent manual-expense history the add-expense modal derives its "most-used" chips + default funds from. */
    suspend fun expenseEntry(accountId: String): ExpenseEntryDto = authedGet("/accounts/$accountId/expense-entry").body()

    /** Log a manual expense in the open period. Returns the new row + recomputed overview to reconcile without a re-fetch. */
    suspend fun addExpense(accountId: String, req: AddExpenseRequest): ExpenseMutationDto =
        authedPost("/accounts/$accountId/expenses", req).body()

    /** Edit an existing expense (used by the FAB's "Edit last"). Reuses the add-expense body shape; returns the edited
     *  row + recomputed overview so the client reconciles without a re-fetch. */
    suspend fun editExpense(accountId: String, expenseId: String, req: AddExpenseRequest): ExpenseMutationDto =
        authedPut("/accounts/$accountId/expenses/$expenseId", req).body()

    /** Delete an expense from the open period; returns the recomputed overview to reconcile. */
    suspend fun deleteExpense(accountId: String, expenseId: String): ExpenseMutationDto =
        authedDelete("/accounts/$accountId/expenses/$expenseId").body()

    // --- Bank sync (Open Banking) -------------------------------------------

    /** Whether bank sync is available to this user + this account's connection. Enabled=false → hide all bank UI.
     *  The endpoint requires a verified email server-side; on a non-2xx (unverified / not allowlisted) treat the
     *  feature as unavailable rather than surfacing an error. */
    suspend fun bankStatus(accountId: String): BankSyncStatusDto {
        ensureFreshToken()
        var resp = client.get("/accounts/$accountId/bank/status") { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.get("/accounts/$accountId/bank/status") { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        }
        return if (resp.status.value in 200..299) resp.body() else BankSyncStatusDto(enabled = false)
    }

    /** Banks the aggregator lists for the account's country (filtered to Revolut server-side). */
    suspend fun bankInstitutions(accountId: String, country: String? = null): List<BankInstitutionDto> =
        authedGet("/accounts/$accountId/bank/institutions${if (country == null) "" else "?country=$country"}").body()

    /** Record consent for a scope (bank_link is required before /bank/link). */
    suspend fun recordConsent(req: RecordConsentRequest) {
        authedPost("/consent", req)
    }

    /** Begin linking: returns the bank's consent URL to open in a browser. `native=true` sends the outcome back
     *  through the com.tandemtab.app://bank/callback deep link. */
    suspend fun startBankLink(accountId: String, req: StartBankLinkRequest): StartBankLinkResponse =
        authedPost("/accounts/$accountId/bank/link", req).body()

    /** Pull booked transactions and stage new ones (returns 204). */
    suspend fun syncBank(accountId: String) {
        authedPostEmpty("/accounts/$accountId/bank/sync")
    }

    /** Transactions fetched but not yet turned into (or dismissed from becoming) a FinApp entry. */
    suspend fun bankPending(accountId: String): List<PendingBankTransactionDto> =
        authedGet("/accounts/$accountId/bank/pending").body()

    /** Mark a staged transaction handled (confirmed=true, turned into an entry) or dismissed (false). */
    suspend fun ackBank(accountId: String, externalId: String, confirmed: Boolean) {
        authedPost("/accounts/$accountId/bank/ack", BankTransactionAck(externalId, confirmed))
    }

    /** Drop the bank connection so the account can be linked afresh (keeps already-handled rows). Returns 204. */
    suspend fun disconnectBank(accountId: String) {
        authedDelete("/accounts/$accountId/bank/connection")
    }

    /** The signed-in user (identity for the profile sheet). */
    suspend fun me(): UserDto = authedGet("/me").body()

    /** Change the signed-in user's password. Returns 204 on success. */
    suspend fun changePassword(req: ChangePasswordRequest) {
        authedPost("/auth/password", req)
    }

    // --- Two-factor authentication ------------------------------------------

    /** Begin 2FA enrollment: returns the shared secret + a QR data-URL to scan into an authenticator app. */
    suspend fun setupTwoFactor(): TwoFactorSetupDto = authedPostEmpty("/auth/2fa/setup").body()

    /** Confirm enrollment with a live code; returns the one-time recovery codes (shown once). */
    suspend fun confirmTwoFactor(code: String): TwoFactorRecoveryDto =
        authedPost("/auth/2fa/confirm", TwoFactorCodeRequest(code.trim())).body()

    /** Turn 2FA off (requires a current code to prove possession of the second factor). Returns 204. */
    suspend fun disableTwoFactor(code: String) {
        authedPost("/auth/2fa/disable", TwoFactorCodeRequest(code.trim()))
    }

    /** Resend the email-verification link to the signed-in user's address. Returns 204. */
    suspend fun resendVerification() {
        authedPostEmpty("/auth/resend-verification")
    }

    /** Set (or clear, with null) the signed-in user's avatar as a data URL. Returns 204. */
    suspend fun setAvatar(dataUrl: String?) {
        authedPut("/me/avatar", SetAvatarRequest(dataUrl))
    }

    /** Rename an account. Returns 204 on success. */
    suspend fun renameAccount(accountId: String, name: String) {
        authedPut("/accounts/$accountId/name", RenameAccountRequest(name))
    }

    /** Leave a shared account. The OWNER must name the member who takes over (the server 400s otherwise); a
     *  non-owner passes null. The sole member of an account archives it instead — same call, server decides. */
    suspend fun leaveAccount(accountId: String, newOwnerUserId: String? = null) {
        authedPost("/accounts/$accountId/leave", LeaveAccountRequest(newOwnerUserId))
    }

    // --- Sharing: invitations + membership ----------------------------------

    /** Invite an existing user to this account by username. 402 when the caller's plan doesn't include sharing —
     *  that message is the upgrade prompt and is worth showing verbatim. 404/409 name the real problem too
     *  ("No user named 'x'.", "already a contributor", "already has a pending invitation"). */
    suspend fun invite(accountId: String, username: String) {
        authedPost("/accounts/$accountId/invitations", CreateInvitationRequest(username.trim()))
    }

    /** Invitations waiting on the signed-in user. Not account-scoped — an invite arrives before there's any
     *  membership to hang it off, and for a brand-new user it may be the only thing on the screen. */
    suspend fun pendingInvitations(): List<InvitationDto> = authedGet("/invitations/pending").body()

    /** Accept an invitation; returns the id of the account just joined so the caller can switch straight to it. */
    suspend fun acceptInvitation(invitationId: String): String =
        authedPostEmpty("/invitations/$invitationId/accept").body<AcceptInvitationDto>().accountId

    /** Decline an invitation (the sender isn't told; it simply stops being pending). */
    suspend fun declineInvitation(invitationId: String) {
        authedPostEmpty("/invitations/$invitationId/decline")
    }

    /** Owner removes another member. Their recorded contributions and expenses stay on the account. */
    suspend fun removeMember(accountId: String, memberUserId: String) {
        authedDelete("/accounts/$accountId/members/$memberUserId")
    }

    /** Owner hands ownership to another current member, staying on the account as an ordinary contributor. */
    suspend fun transferOwnership(accountId: String, newOwnerUserId: String) {
        authedPost("/accounts/$accountId/transfer-ownership", TransferOwnershipRequest(newOwnerUserId))
    }

    /** Profile pictures of everyone on the account, keyed by user id (members without one are absent).
     *  Best-effort: a face next to a name is decoration, never a reason to fail the sheet. */
    suspend fun memberAvatars(accountId: String): Map<String, String> = runCatching {
        authedGet("/accounts/$accountId/avatars").body<Map<String, String>>()
    }.getOrDefault(emptyMap())

    /** Delete the account (owner only; soft-delete with a 30-day grace server-side). Returns 204. */
    suspend fun deleteAccount(accountId: String) {
        authedDelete("/accounts/$accountId")
    }

    /** Revoke the refresh token server-side (best-effort) and forget the session locally. */
    suspend fun signOut() {
        val rt = refreshToken
        accessToken = null
        refreshToken = null
        expiresAt = null
        if (rt != null) runCatching { client.post("/auth/logout") { setBody(LogoutRequest(rt)) } }
        store?.clear()
    }

    // --- session plumbing ---------------------------------------------------

    /** GET an authed endpoint, refreshing the token first if it's stale and once more if it 401s. */
    /** `?period=N` query for the period-scoped GET reads, or empty for the current period. */
    private fun periodQ(period: Int?): String = if (period == null) "" else "?period=$period"

    private suspend fun authedGet(path: String): HttpResponse {
        ensureFreshToken()
        var resp = client.get(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.get(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        }
        ensureOk(resp.status, resp.bodyAsText())
        return resp
    }

    /** POST an authed endpoint with a JSON body, same stale/401 refresh handling as [authedGet]. */
    private suspend inline fun <reified T> authedPost(path: String, body: T): HttpResponse {
        ensureFreshToken()
        var resp = client.post(path) {
            header(HttpHeaders.Authorization, "Bearer ${requireToken()}")
            setBody(body)
        }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.post(path) {
                header(HttpHeaders.Authorization, "Bearer ${requireToken()}")
                setBody(body)
            }
        }
        ensureOk(resp.status, serverMessageOr(resp.bodyAsText(), "That didn't save. Please try again."))
        return resp
    }

    /** PUT an authed endpoint with a JSON body, same stale/401 refresh handling as [authedPost]. */
    private suspend inline fun <reified T> authedPut(path: String, body: T): HttpResponse {
        ensureFreshToken()
        var resp = client.put(path) {
            header(HttpHeaders.Authorization, "Bearer ${requireToken()}")
            setBody(body)
        }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.put(path) {
                header(HttpHeaders.Authorization, "Bearer ${requireToken()}")
                setBody(body)
            }
        }
        ensureOk(resp.status, serverMessageOr(resp.bodyAsText(), "That didn't save. Please try again."))
        return resp
    }

    /** DELETE an authed endpoint, same stale/401 refresh handling as [authedGet]. */
    private suspend fun authedDelete(path: String): HttpResponse {
        ensureFreshToken()
        var resp = client.delete(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.delete(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        }
        ensureOk(resp.status, serverMessageOr(resp.bodyAsText(), "That didn't work. Please try again."))
        return resp
    }

    /** POST an authed endpoint that takes no body (e.g. recurring skip), same refresh handling as [authedGet]. */
    private suspend fun authedPostEmpty(path: String): HttpResponse {
        ensureFreshToken()
        var resp = client.post(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.post(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        }
        ensureOk(resp.status, serverMessageOr(resp.bodyAsText(), "That didn't save. Please try again."))
        return resp
    }

    /** Proactively refresh when the access token is at/near expiry — best-effort (a 401 still retries). */
    private suspend fun ensureFreshToken() {
        val exp = expiresAt ?: return
        if (!Instant.now().isBefore(exp.minus(expirySkew))) tryRefresh()
    }

    /** Rotate the session via /auth/refresh. Returns false (without throwing) if there's no token or it's dead. */
    private suspend fun tryRefresh(): Boolean {
        val rt = refreshToken ?: return false
        return runCatching {
            val resp = client.post("/auth/refresh") { setBody(RefreshRequest(rt)) }
            if (resp.status.value !in 200..299) return false
            adoptSession(resp.body())
            true
        }.getOrDefault(false)
    }

    private suspend fun adoptSession(auth: AuthResponse) {
        accessToken = auth.token
        auth.refreshToken?.let { refreshToken = it }
        expiresAt = parseInstant(auth.expiresAt)
        store?.save(auth)
    }

    private fun parseInstant(iso: String): Instant? =
        runCatching { OffsetDateTime.parse(iso).toInstant() }
            .recoverCatching { Instant.parse(iso) }
            .getOrNull()

    private fun requireToken(): String = accessToken ?: throw ApiException(401, "Not signed in.")

    private fun ensureOk(status: HttpStatusCode, body: String) {
        if (status.value !in 200..299) {
            throw ApiException(status.value, if (body.isBlank()) status.description else body)
        }
    }

    /** Pull a human message out of the server's error body ({"error":…} or {"title":…}), else a default. */
    private fun serverMessageOr(body: String, default: String): String {
        return runCatching {
            val el = json.parseToJsonElement(body)
            val obj = (el as? kotlinx.serialization.json.JsonObject) ?: return default
            (obj["error"] ?: obj["title"] ?: obj["message"])
                ?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content }
                ?.takeIf { it.isNotBlank() }
        }.getOrNull() ?: default
    }

    private fun loginError(status: HttpStatusCode): String = when (status.value) {
        401, 400 -> "Wrong username/email or password."
        429 -> "Too many attempts. Wait a moment and try again."
        else -> "Couldn't sign in (${status.value})."
    }
}
