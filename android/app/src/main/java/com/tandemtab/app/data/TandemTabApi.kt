package com.tandemtab.app.data

import com.tandemtab.app.BuildConfig
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.defaultRequest
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
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

    /** Exchange the one-time code from the external-sign-in deep link for real session tokens.
     *  /auth/exchange returns a LoginResponse (it can also 2FA-gate), same envelope as /auth/login. */
    suspend fun exchangeCode(code: String): AuthResponse {
        val resp = client.post("/auth/exchange") { setBody(ExchangeCodeRequest(code)) }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, "Sign-in didn't complete. Please try again.")
        }
        val result: LoginResponse = resp.body()
        val auth = result.auth ?: throw ApiException(
            resp.status.value,
            if (result.twoFactorRequired) "This account has two-factor sign-in, which the app doesn't support yet."
            else "Sign-in didn't complete. Please try again.",
        )
        adoptSession(auth)
        return auth
    }

    suspend fun listAccounts(): List<AccountSummaryDto> = authedGet("/accounts").body()

    suspend fun spending(accountId: String): SpendingViewDto = authedGet("/accounts/$accountId/spending").body()

    suspend fun overview(accountId: String): AccountOverviewDto = authedGet("/accounts/$accountId/overview").body()

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
    private suspend fun authedGet(path: String): HttpResponse {
        ensureFreshToken()
        var resp = client.get(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        if (resp.status == HttpStatusCode.Unauthorized && tryRefresh()) {
            resp = client.get(path) { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        }
        ensureOk(resp.status, resp.bodyAsText())
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
