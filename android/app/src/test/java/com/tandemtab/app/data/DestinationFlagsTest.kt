package com.tandemtab.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The flag guesser. A wrong guess only costs a tap, so these are not about correctness of taste — they pin the two
 * rules that would silently produce a *plausible but wrong* flag, which is the kind nobody reports.
 */
class DestinationFlagsTest {

    @Test
    fun `a country or a city both resolve`() {
        assertEquals("🇮🇹", DestinationFlags.guess("Rome, Italy"))
        assertEquals("🇮🇹", DestinationFlags.guess("Rome"))
        assertEquals("🇮🇹", DestinationFlags.guess("Italy"))
    }

    @Test
    fun `the longest matching key wins`() {
        // The rule that stops table order deciding. "new zealand" contains "new york"'s first word and
        // "south africa" would lose to a bare "africa" if the table were scanned in order.
        assertEquals("🇳🇿", DestinationFlags.guess("New Zealand"))
        assertEquals("🇺🇸", DestinationFlags.guess("New York"))
        assertEquals("🇿🇦", DestinationFlags.guess("South Africa"))
    }

    @Test
    fun `the destination is preferred over the trip name`() {
        // Both name a place; the destination is the field that means one.
        assertEquals("🇫🇷", DestinationFlags.guess("Paris", "Tokyo reunion"))
    }

    @Test
    fun `the trip name is the fallback when there is no destination`() {
        assertEquals("🇯🇵", DestinationFlags.guess(null, "Tokyo reunion"))
        assertEquals("🇯🇵", DestinationFlags.guess("  ", "Tokyo reunion"))
    }

    @Test
    fun `nothing matching gives null rather than a guess`() {
        assertNull(DestinationFlags.guess("Grandma's house"))
        assertNull(DestinationFlags.guess(null, null))
    }

    @Test
    fun `a flag is a regional indicator pair`() {
        // Two code points, each outside the BMP — so four UTF-16 chars. A test that asserted length 2 would pass
        // on a broken implementation that returned the ASCII letters.
        val flag = DestinationFlags.guess("Bulgaria")!!
        assertEquals(4, flag.length)
        assertEquals(2, flag.codePointCount(0, flag.length))
        assertEquals("🇧🇬", flag)
    }

    @Test
    fun `the generic suggestions are always available`() {
        // There has to be something to tap when nothing matches, or the chip row is empty for a beach week.
        assert(DestinationFlags.generic.isNotEmpty())
        assert(DestinationFlags.generic.contains("plane"))
    }
}
