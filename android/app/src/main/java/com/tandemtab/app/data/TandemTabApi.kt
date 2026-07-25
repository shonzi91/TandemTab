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
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import io.ktor.http.contentType
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.json.Json

/** Raised for a non-2xx response so the UI can show a message instead of crashing. */
class ApiException(val status: Int, override val message: String) : Exception(message)

/**
 * Thin HTTP client over the TandemTab server API. Holds the bearer token in memory for now
 * (a DataStore-backed store comes with the "stay signed in" slice). One instance per app.
 */
class TandemTabApi(
    private val baseUrl: String = BuildConfig.API_BASE_URL,
) {
    @Volatile
    var accessToken: String? = null
        private set

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

    suspend fun login(usernameOrEmail: String, password: String): LoginResponse {
        val resp = client.post("/auth/login") {
            setBody(LoginRequest(usernameOrEmail.trim(), password))
        }
        if (resp.status.value !in 200..299) {
            throw ApiException(resp.status.value, loginError(resp.status))
        }
        val result: LoginResponse = resp.body()
        result.auth?.token?.let { accessToken = it }
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
        accessToken = result.token
        return result
    }

    /** Request a password-reset link. Always succeeds (never reveals whether the identifier matched). */
    suspend fun forgotPassword(identifier: String) {
        val resp = client.post("/auth/password/forgot") { setBody(ForgotPasswordRequest(identifier.trim())) }
        if (resp.status.value !in 200..299 && resp.status.value != 204) {
            throw ApiException(resp.status.value, "Couldn't send the reset link. Try again.")
        }
    }

    suspend fun listAccounts(): List<AccountSummaryDto> {
        val resp = client.get("/accounts") { header(HttpHeaders.Authorization, "Bearer ${requireToken()}") }
        ensureOk(resp.status, resp.bodyAsText())
        return resp.body()
    }

    suspend fun overview(accountId: String): AccountOverviewDto {
        val resp = client.get("/accounts/$accountId/overview") {
            header(HttpHeaders.Authorization, "Bearer ${requireToken()}")
        }
        ensureOk(resp.status, resp.bodyAsText())
        return resp.body()
    }

    fun signOut() {
        accessToken = null
    }

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
