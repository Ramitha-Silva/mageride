package lk.mageride.driver.profile

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * Joining the gateway origin to the path registry-svc hands back (Δ MCS-25).
 *
 * Slashes are the kind of thing that works against one deployment and breaks against the next —
 * `API_BASE_URL` carries a trailing one in some build types and not others — so the rule is written
 * down rather than left to whichever string the emulator happened to be pointed at.
 */
class AbsoluteUrlTest {

    private val signed = "/v1/drivers/01J0/profile-photo?expires=1893456000&signature=abc"

    @Test
    fun `an origin with no trailing slash joins cleanly`() {
        assertEquals(
            "https://api.mageride.lk$signed",
            absoluteUrl("https://api.mageride.lk", signed),
        )
    }

    @Test
    fun `an origin with a trailing slash does not double it`() {
        assertEquals(
            "https://api.mageride.lk$signed",
            absoluteUrl("https://api.mageride.lk/", signed),
        )
    }

    @Test
    fun `a path with no leading slash is still joined with one`() {
        assertEquals(
            "https://api.mageride.lk/v1/drivers/01J0/profile-photo",
            absoluteUrl("https://api.mageride.lk/", "v1/drivers/01J0/profile-photo"),
        )
    }

    /**
     * D-36 lets `getDriverProfilePhoto` redirect to a presigned bucket URL, and a future read could
     * carry one directly. Prefixing the gateway origin onto an absolute URL would turn a working
     * link into a 404 against the wrong host.
     */
    @Test
    fun `an absolute url is returned untouched`() {
        val presigned = "https://objects.mageride.lk/docs/abc?X-Amz-Signature=def"

        assertEquals(presigned, absoluteUrl("https://api.mageride.lk", presigned))
        assertEquals("http://localhost:9000/x", absoluteUrl("https://api.mageride.lk", "http://localhost:9000/x"))
    }

    /**
     * A driver with no photograph, which is what PDPA erasure leaves behind and what every driver
     * reads as until the profile call answers. `null` is what draws the glyph.
     */
    @Test
    fun `nothing to load stays null`() {
        assertNull(absoluteUrl("https://api.mageride.lk", null))
        assertNull(absoluteUrl("https://api.mageride.lk", ""))
        assertNull(absoluteUrl("https://api.mageride.lk", "   "))
    }
}
