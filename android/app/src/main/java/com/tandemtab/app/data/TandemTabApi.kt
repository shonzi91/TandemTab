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

    suspend fun wallets(accountId: String, period: Int? = null): WalletsViewDto = authedGet("/accounts/$accountId/wallets${periodQ(period)}").body()

    suspend fun income(accountId: String): IncomeViewDto = authedGet("/accounts/$accountId/income").body()

    /** Move money between two funds. Returns a refreshed Wallets view to reconcile without a re-fetch. */
    suspend fun transferFunds(accountId: String, req: TransferFundsRequest): FundMutationDto =
        authedPost("/accounts/$accountId/fund-transfers", req).body()

    /** Record income into a fund (a deposit). Returns the recomputed overview. */
    suspend fun addDeposit(accountId: String, req: AddDepositRequest): DepositMutationDto =
        authedPost("/accounts/$accountId/deposits", req).body()

    suspend fun overview(accountId: String, period: Int? = null): AccountOverviewDto = authedGet("/accounts/$accountId/overview${periodQ(period)}").body()

    /** The account's periods (oldest→newest) + the current index — used for the top-bar period label. */
    suspend fun periods(accountId: String): PeriodsViewDto = authedGet("/accounts/$accountId/periods").body()

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

    /** Recurring bills / income expectations with their due state for the open period. */
    suspend fun recurring(accountId: String, period: Int? = null): RecurringViewDto = authedGet("/accounts/$accountId/recurring${periodQ(period)}").body()

    /** Confirm a due bill/income (posts a real expense/income with the actual amount). Returns a refreshed view. */
    suspend fun confirmRecurring(accountId: String, recurringId: String, req: ConfirmRecurringRequest): RecurringMutationDto =
        authedPost("/accounts/$accountId/recurring/$recurringId/confirm", req).body()

    /** Skip a due item for this period (marks it handled, posts nothing). Returns a refreshed view. */
    suspend fun skipRecurring(accountId: String, recurringId: String): RecurringMutationDto =
        authedPostEmpty("/accounts/$accountId/recurring/$recurringId/skip").body()

    /** Recent manual-expense history the add-expense modal derives its "most-used" chips + default funds from. */
    suspend fun expenseEntry(accountId: String): ExpenseEntryDto = authedGet("/accounts/$accountId/expense-entry").body()

    /** Log a manual expense in the open period. Returns the new row + recomputed overview to reconcile without a re-fetch. */
    suspend fun addExpense(accountId: String, req: AddExpenseRequest): ExpenseMutationDto =
        authedPost("/accounts/$accountId/expenses", req).body()

    /** Edit an existing expense (used by the FAB's "Edit last"). Reuses the add-expense body shape; returns the edited
     *  row + recomputed overview so the client reconciles without a re-fetch. */
    suspend fun editExpense(accountId: String, expenseId: String, req: AddExpenseRequest): ExpenseMutationDto =
        authedPut("/accounts/$accountId/expenses/$expenseId", req).body()

    /** The signed-in user (identity for the profile sheet). */
    suspend fun me(): UserDto = authedGet("/me").body()

    /** Change the signed-in user's password. Returns 204 on success. */
    suspend fun changePassword(req: ChangePasswordRequest) {
        authedPost("/auth/password", req)
    }

    /** Rename an account. Returns 204 on success. */
    suspend fun renameAccount(accountId: String, name: String) {
        authedPut("/accounts/$accountId/name", RenameAccountRequest(name))
    }

    /** Leave a shared account (non-owner). Owner-leave would need a new-owner id, which the UI doesn't offer. */
    suspend fun leaveAccount(accountId: String) {
        authedPost("/accounts/$accountId/leave", LeaveAccountRequest())
    }

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
