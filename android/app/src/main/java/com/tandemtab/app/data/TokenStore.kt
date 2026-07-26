package com.tandemtab.app.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.firstOrNull

/** The persisted session — enough to resume straight to Home and refresh in the background. */
data class SavedSession(
    val accessToken: String,
    val refreshToken: String?,
    val expiresAt: String,
    val userId: String,
    val username: String,
    val email: String,
)

// One process-wide DataStore for the auth session; the delegate must be declared at file scope.
private val Context.authDataStore: DataStore<Preferences> by preferencesDataStore(name = "auth")

/**
 * DataStore-backed store for the signed-in session so the app survives a restart.
 * Tokens are the only sensitive bit; the identity fields let Home render before the first
 * network round-trip. Plain-preferences for now — an EncryptedDataStore is a later hardening.
 */
class TokenStore(private val context: Context) {
    private object Keys {
        val Access = stringPreferencesKey("access_token")
        val Refresh = stringPreferencesKey("refresh_token")
        val Expires = stringPreferencesKey("expires_at")
        val UserId = stringPreferencesKey("user_id")
        val Username = stringPreferencesKey("username")
        val Email = stringPreferencesKey("email")
    }

    /** The persisted session, or null if nobody is signed in. */
    suspend fun load(): SavedSession? {
        val p = context.authDataStore.data.firstOrNull() ?: return null
        val access = p[Keys.Access] ?: return null
        return SavedSession(
            accessToken = access,
            refreshToken = p[Keys.Refresh],
            expiresAt = p[Keys.Expires] ?: "",
            userId = p[Keys.UserId] ?: "",
            username = p[Keys.Username] ?: "",
            email = p[Keys.Email] ?: "",
        )
    }

    /** Persist (or overwrite) the session after a login, register, exchange or refresh. */
    suspend fun save(auth: AuthResponse) {
        context.authDataStore.edit { p ->
            p[Keys.Access] = auth.token
            // Refresh rotates on every use; keep the previous one only if the response omits it.
            auth.refreshToken?.let { p[Keys.Refresh] = it }
            p[Keys.Expires] = auth.expiresAt
            p[Keys.UserId] = auth.userId
            p[Keys.Username] = auth.username
            p[Keys.Email] = auth.email
        }
    }

    /** Forget the session (sign-out, or a refresh token the server has revoked). */
    suspend fun clear() {
        context.authDataStore.edit { it.clear() }
    }
}
