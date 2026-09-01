package com.tandemtab.app.data

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

// One process-wide DataStore for the offline mirror; the delegate must be declared at file scope, like auth's.
private val Context.offlineDataStore: DataStore<Preferences> by preferencesDataStore(name = "offline")

/** A screen's last-good payload and the moment it was true. */
@Serializable
data class CachedView(val at: Long, val payload: String)

/**
 * One expense the user wrote with no signal, waiting for one.
 *
 * ★ [AddExpenseRequest.clientId] is what makes this safe to replay, and it is why T0 had to land before this
 * phase could. The server recognises a key it has already seen and answers with the original's result, so
 * "post exactly one row on reconnect" is a property of the request rather than of this queue's bookkeeping —
 * a flush interrupted halfway and retried cannot double-post.
 */
@Serializable
data class QueuedExpense(val accountId: String, val request: AddExpenseRequest, val queuedAt: Long)

/**
 * R4.5 Trip Mode: the device's own copy of what it last knew, plus the writes it has not been able to send.
 *
 * ⚠️⚠️ **This is account data at rest on the device, in plaintext.** Server snapshots are encrypted under KMS;
 * this mirror is not. That is a privacy-policy surface change owed to R5's legal re-read — recorded in
 * OPEN-BETA under R4.5, not implied here. [clear] on sign-out is the one mitigation that costs nothing.
 *
 * ⚠️ Every read swallows its failure and answers "nothing cached". A device that cannot store the mirror must
 * still run the app online: a caching layer that can break the app it exists to protect is worse than none.
 */
class OfflineStore(private val context: Context) {

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    private fun viewKey(accountId: String, period: Int?) =
        stringPreferencesKey("view:$accountId:${period ?: "current"}")

    private val outboxKey = stringPreferencesKey("outbox")

    private fun armedKey(accountId: String) = stringPreferencesKey("armed:$accountId")

    /**
     * ★ Trip Mode is **opt-in**, and this flag is the whole reason it can be. Nothing is written to the device
     * until the user turns it on for an account — so a person who never leaves home never has a plaintext copy
     * of their finances sitting in app storage, and the phase's privacy cost is one they chose to pay.
     *
     * ⚠️ Read on the write path as well as the read path. Gating only the *reads* would still leave the mirror
     * being written for everybody, which is the half that actually matters.
     */
    suspend fun setArmed(accountId: String, on: Boolean) {
        runCatching { context.offlineDataStore.edit { it[armedKey(accountId)] = if (on) "1" else "0" } }
    }

    suspend fun isArmed(accountId: String): Boolean = runCatching {
        context.offlineDataStore.data.firstOrNull()?.get(armedKey(accountId)) == "1"
    }.getOrNull() ?: false

    /** Forget the cached view for one account, leaving the outbox alone — see [clearViews]' note. */
    suspend fun clearView(accountId: String, period: Int?) {
        runCatching { context.offlineDataStore.edit { it.remove(viewKey(accountId, period)) } }
    }

    /** Keep what the server just said, so the next start has something true to show. */
    suspend fun putView(accountId: String, period: Int?, payload: String) {
        runCatching {
            val row = json.encodeToString(CachedView(System.currentTimeMillis(), payload))
            context.offlineDataStore.edit { it[viewKey(accountId, period)] = row }
        }
    }

    /**
     * The last payload stored for this screen, with the moment it was stored.
     *
     * ⚠️ The caller must NOT write it back after reading. Re-storing a payload that came from the device would
     * refresh its timestamp and make week-old figures look like this morning's — the one bug that would turn the
     * staleness banner into a lie.
     */
    suspend fun getView(accountId: String, period: Int?): CachedView? = runCatching {
        val raw = context.offlineDataStore.data.firstOrNull()?.get(viewKey(accountId, period)) ?: return null
        json.decodeFromString<CachedView>(raw)
    }.getOrNull()

    /** Add a write to the tail of the queue. Order is preserved so the ledger reads back the way it was typed. */
    suspend fun enqueue(item: QueuedExpense) {
        runCatching {
            context.offlineDataStore.edit { prefs ->
                val current = prefs[outboxKey]?.let { runCatching { json.decodeFromString<List<QueuedExpense>>(it) }.getOrNull() } ?: emptyList()
                prefs[outboxKey] = json.encodeToString(current + item)
            }
        }
    }

    suspend fun pending(): List<QueuedExpense> = runCatching {
        val raw = context.offlineDataStore.data.firstOrNull()?.get(outboxKey) ?: return emptyList()
        json.decodeFromString<List<QueuedExpense>>(raw)
    }.getOrNull() ?: emptyList()

    /**
     * Drop the rows that made it.
     *
     * ⚠️ By client key, never by index. A flush runs while the user can still be typing, so the queue can have
     * grown between reading it and writing it back — removing "the first three" would eat somebody's expense.
     */
    suspend fun removeSent(sentKeys: Set<String>) {
        if (sentKeys.isEmpty()) return
        runCatching {
            context.offlineDataStore.edit { prefs ->
                val current = prefs[outboxKey]?.let { runCatching { json.decodeFromString<List<QueuedExpense>>(it) }.getOrNull() } ?: emptyList()
                prefs[outboxKey] = json.encodeToString(current.filterNot { it.request.clientId in sentKeys })
            }
        }
    }

    /**
     * Everything this device is holding. Called on sign-out — see the class note on data at rest.
     *
     * ⚠️ This takes the OUTBOX with it, which is deliberate but is the one destructive thing in this class: rows
     * the user typed and has not sent are discarded along with the account they belong to. The pending count is
     * on screen so that choice is visible rather than silent.
     */
    suspend fun clear() {
        runCatching { context.offlineDataStore.edit { it.clear() } }
    }
}
