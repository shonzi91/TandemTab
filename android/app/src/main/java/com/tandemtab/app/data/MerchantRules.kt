package com.tandemtab.app.data

/**
 * "Always file this merchant here" — the saved rules, and how a statement line is matched against them.
 *
 * ⚠️ **A port of the web's `MappingFor` / `BankTokens`, and it decides where money is filed**, so the two must
 * agree for the same reason [BankFileParser] must: a rule saved on the web and a statement imported on the phone
 * are the same user's expectation, and a matcher that disagrees files the shopping under Travel without saying so.
 *
 * The rule is **token-subset matching, most-specific wins**. A rule applies when every one of its tokens appears in
 * the transaction's tokens — the user curates which tokens carry the merchant's identity — and among the rules that
 * apply, the one matching the most tokens is used. That is what lets a precise "transfer to ivan petrov" rule beat
 * a broad "netflix" one, and it is why distinct payees no longer collapse onto a shared first word the way an
 * earlier merchant-stem approach did.
 */
object MerchantRules {

    /**
     * Split a description (or a rule's match key) into lowercased word tokens, on any non-letter/non-digit
     * boundary — so "TESCO,LONDON 4471" becomes [tesco, london, 4471].
     *
     * ⚠️ The class is `\p{L}\p{N}` (any letter, any number) and not `a-z0-9`, so Cyrillic merchant names survive.
     * An ASCII-only split would reduce "ФАНТАСТИКО" to nothing and silently match every rule against every row.
     */
    fun tokens(s: String?): List<String> =
        Regex("[^\\p{L}\\p{N}]+").split((s ?: "").lowercase()).filter { it.isNotEmpty() }

    /** The normalized form the server stores a rule under — words, lowercased, single-spaced. */
    fun matchKey(description: String?): String =
        (description ?: "").lowercase().split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ")

    /**
     * The best rule for [description], or null. [rules] is keyed by match key, exactly as the server returns them.
     *
     * ⚠️ Ties do NOT win: a rule must match strictly more tokens than the current best to replace it, so when two
     * rules are equally specific the first one encountered stays. Same as the web — arbitrary, but *stably*
     * arbitrary, which is what stops the filing changing between two runs over the same file.
     */
    fun match(rules: Map<String, BankMappingDto>, description: String?): BankMappingDto? {
        val descTokens = tokens(description).toHashSet()
        if (descTokens.isEmpty()) return null
        var best: BankMappingDto? = null
        var bestCount = 0
        for ((key, value) in rules) {
            val ruleTokens = tokens(key)
            if (ruleTokens.isEmpty() || ruleTokens.size <= bestCount) continue
            if (ruleTokens.all { descTokens.contains(it) }) {
                best = value
                bestCount = ruleTokens.size
            }
        }
        return best
    }

    /**
     * The category a rule would file [description] into, or null when no rule applies or the rule targets something
     * else. ⚠️ Only "category" rules are consulted here: a "fund" rule means the money-in came from one of your own
     * wallets, which is a transfer rather than a category, and treating it as one would book a transfer as income.
     */
    fun categoryFor(rules: Map<String, BankMappingDto>, description: String?): String? =
        match(rules, description)?.takeIf { it.kind == "category" }?.targetId
}
