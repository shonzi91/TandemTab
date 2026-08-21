package com.tandemtab.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The merchant matcher, which decides where an imported row is filed before anyone looks at it.
 *
 * ⚠️ Same argument as [BankFileParserTest]: a rule saved on the web and a statement imported on the phone are one
 * user's single expectation, so the two matchers have to agree. These pin the behaviours that are easy to get
 * subtly wrong — Cyrillic tokens, most-specific-wins, and the fund/category distinction.
 */
class MerchantRulesTest {

    private fun rule(key: String, kind: String = "category", target: String = "cat-1") =
        key to BankMappingDto(matchKey = key, kind = kind, targetId = target)

    @Test
    fun `a rule matches when all of its tokens are present`() {
        val rules = mapOf(rule("tesco"))
        assertEquals("cat-1", MerchantRules.categoryFor(rules, "TESCO,LONDON 4471"))
    }

    @Test
    fun `a rule whose tokens are not all present does not match`() {
        val rules = mapOf(rule("tesco express"))
        assertNull(MerchantRules.categoryFor(rules, "TESCO,LONDON 4471"))
    }

    @Test
    fun `the most specific matching rule wins`() {
        // The reason this matcher is token-subset rather than a stem: a broad rule must not capture a payee that a
        // precise one names. "transfer to ivan petrov" beats a bare "transfer".
        val rules = mapOf(
            rule("transfer", target = "cat-broad"),
            rule("transfer to ivan petrov", target = "cat-ivan"),
        )
        assertEquals("cat-ivan", MerchantRules.categoryFor(rules, "TRANSFER TO IVAN PETROV 12/06"))
        assertEquals("cat-broad", MerchantRules.categoryFor(rules, "TRANSFER TO MARIA"))
    }

    @Test
    fun `cyrillic merchant names survive tokenising`() {
        // An ASCII-only split would reduce this to nothing, and a rule with no tokens would either match nothing
        // or — worse, depending on how the loop is written — match everything.
        val rules = mapOf(rule("фантастико"))
        assertEquals("cat-1", MerchantRules.categoryFor(rules, "ФАНТАСТИКО 30 СОФИЯ"))
        assertEquals(listOf("фантастико", "30", "софия"), MerchantRules.tokens("ФАНТАСТИКО 30 СОФИЯ"))
    }

    @Test
    fun `a fund rule is not treated as a category`() {
        // A "fund" rule means the money came from one of your own wallets — a transfer, not spending. Filing it as
        // a category would book a transfer as income and inflate the month.
        val rules = mapOf(rule("salary", kind = "fund", target = "fund-1"))
        assertNull(MerchantRules.categoryFor(rules, "SALARY PAYMENT"))
        assertEquals("fund-1", MerchantRules.match(rules, "SALARY PAYMENT")?.targetId)
    }

    @Test
    fun `an empty description matches nothing`() {
        val rules = mapOf(rule("tesco"))
        assertNull(MerchantRules.categoryFor(rules, ""))
        assertNull(MerchantRules.categoryFor(rules, null))
        assertNull(MerchantRules.categoryFor(rules, "  -- "))
    }

    @Test
    fun `the match key is the normalized description the server stores`() {
        assertEquals("tesco stores london", MerchantRules.matchKey("  TESCO   Stores\tLONDON "))
    }

    @Test
    fun `punctuation separates tokens the same way as the web`() {
        assertEquals(listOf("tesco", "london", "4471"), MerchantRules.tokens("TESCO,LONDON 4471"))
    }
}
