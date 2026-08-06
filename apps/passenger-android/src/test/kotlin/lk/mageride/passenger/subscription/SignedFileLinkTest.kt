package lk.mageride.passenger.subscription

import lk.mageride.shared.data.models.subscription.ModeBFileKind
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * AL-49's signed link, taken apart — and the shapes that must answer nothing.
 *
 * The link is the credential (`security: []` on `GET /v1/mode-b/files/{kind}/{id}`), so what
 * matters is that a malformed or foreign one produces a `null` the pay sheet can draw around
 * rather than an exception that reads as a failed payment.
 */
class SignedFileLinkTest {

    @Test
    fun a_lankaqr_link_gives_back_the_four_values_the_client_needs() {
        val link = SignedFileLink.parse(
            "https://api.mageride.lk/v1/mode-b/files/lankaqr/$PROFILE?expires=1780000000&signature=abc123",
        )

        assertEquals(
            SignedFileLink(
                kind = ModeBFileKind.LANKAQR,
                id = PROFILE,
                expires = 1_780_000_000L,
                signature = "abc123",
            ),
            link,
        )
    }

    @Test
    fun a_slip_link_parses_too_and_keeps_its_kind() {
        // The other half of the route. The pay sheet never fetches one, but the kind is what
        // `ApiSubscriptionRepository.ownerLankaQr` filters on — so a slip link handed to it by a
        // mis-shaped `payTo` must be distinguishable rather than fetched as a QR.
        val link = SignedFileLink.parse("/v1/mode-b/files/slips/$PROFILE?expires=1&signature=z")

        assertEquals(ModeBFileKind.SLIPS, link?.kind)
    }

    @Test
    fun the_origin_is_discarded_and_a_relative_link_works() {
        // The call is re-issued against the app's own configured gateway, so the host in the link
        // is never followed — which is what stops a minted URL redirecting this app anywhere.
        val absolute = SignedFileLink.parse(
            "https://internal.example/v1/mode-b/files/lankaqr/$PROFILE?expires=9&signature=s",
        )

        assertEquals(SignedFileLink.parse("/v1/mode-b/files/lankaqr/$PROFILE?expires=9&signature=s"), absolute)
    }

    @Test
    fun anything_that_is_not_a_signed_mode_b_file_link_is_null() {
        assertNull(SignedFileLink.parse(null), "no link at all")
        assertNull(SignedFileLink.parse(""), "empty")
        assertNull(SignedFileLink.parse("https://example.com/logo.png"), "some other URL")
        assertNull(
            SignedFileLink.parse("/v1/mode-b/files/passport/$PROFILE?expires=1&signature=s"),
            "a kind this build does not know",
        )
        assertNull(SignedFileLink.parse("/v1/mode-b/files/lankaqr/$PROFILE?signature=s"), "no expiry")
        assertNull(SignedFileLink.parse("/v1/mode-b/files/lankaqr/$PROFILE?expires=1"), "no signature")
        assertNull(SignedFileLink.parse("/v1/mode-b/files/lankaqr/?expires=1&signature=s"), "no id")
        assertNull(
            SignedFileLink.parse("/v1/mode-b/files/lankaqr/$PROFILE?expires=soon&signature=s"),
            "an expiry that is not a number",
        )
    }

    private companion object {
        const val PROFILE = "01JPRF00000000000000000001"
    }
}
