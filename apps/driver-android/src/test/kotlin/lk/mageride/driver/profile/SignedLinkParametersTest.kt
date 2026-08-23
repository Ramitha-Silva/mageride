package lk.mageride.driver.profile

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * Taking a signed photo link apart (Δ MCS-27).
 *
 * `getDriverProfilePhoto` takes `v`, `expires` and `signature` as typed arguments and the only
 * place they exist is inside the URL `getDriverProfile` handed back, so something has to parse
 * them — and a parser that is only ever exercised against one well-formed string is a parser that
 * throws on the first deployment that formats a URL slightly differently.
 */
class SignedLinkParametersTest {

    private val link = "/v1/drivers/01J0/profile-photo?v=ab12cd34&expires=1893456000&signature=deadbeef"

    @Test
    fun `the three parameters the photo read needs come out`() {
        val query = signedLinkParameters(link)

        assertEquals("ab12cd34", query["v"])
        assertEquals("1893456000", query["expires"])
        assertEquals("deadbeef", query["signature"])
    }

    @Test
    fun `an absolute url parses the same as a relative one`() {
        assertEquals(
            signedLinkParameters(link),
            signedLinkParameters("https://api.mageride.lk$link"),
        )
    }

    /**
     * A link the server did not sign the way this build expects. The caller answers a missing key
     * by not fetching and drawing what is already on disk, which is why this returns a map with a
     * hole in it rather than throwing at a driver.
     */
    @Test
    fun `a link missing a parameter yields a map missing a key`() {
        val query = signedLinkParameters("/v1/drivers/01J0/profile-photo?v=ab12cd34")

        assertEquals("ab12cd34", query["v"])
        assertNull(query["expires"])
        assertNull(query["signature"])
    }

    @Test
    fun `a link with no query at all is empty rather than an error`() {
        assertEquals(emptyMap(), signedLinkParameters("/v1/drivers/01J0/profile-photo"))
        assertEquals(emptyMap(), signedLinkParameters(""))
    }

    /** A stray `&&` or a bare flag is dropped, not turned into a key with no value. */
    @Test
    fun `fragments that are not key-value pairs are ignored`() {
        val query = signedLinkParameters("/x?v=1&&flag&expires=2")

        assertEquals(mapOf("v" to "1", "expires" to "2"), query)
    }
}
