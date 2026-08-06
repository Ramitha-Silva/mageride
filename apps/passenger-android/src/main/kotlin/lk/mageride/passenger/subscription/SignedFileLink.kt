package lk.mageride.passenger.subscription

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.ModeBFileKind

/**
 * A `GET /v1/mode-b/files/{kind}/{id}?expires=&signature=` link, taken apart.
 *
 * **This app has no image loader, and adding one to render a single QR would be the wrong trade.**
 * The contract's own words are *"the URL goes to an image loader, which carries no bearer"*, and
 * `security: []` is on that route precisely so a `<img src>` works. What this module has instead
 * is C013's typed client, which already knows the gateway origin, the retry curve, the circuit
 * breaker and the RFC 7807 mapping — so the link is split back into the four values
 * `SubscriptionApi.getModeBFile` takes and fetched through the same stack as everything else.
 *
 * **The origin in the link is discarded on purpose.** The call is re-issued against
 * `PassengerEnvironment`'s configured gateway, so a signed URL minted with an internal or stale
 * host cannot redirect this app anywhere — the only things taken from it are the file's identity
 * and the signature, which is the credential.
 *
 * Parsed by hand rather than with `android.net.Uri`: that class is a stub in a local unit test and
 * every member throws, and this table is asserted on the JVM. Same call `PushRouter` made.
 *
 * @property kind Which document — the owner's LankaQR, or a transfer slip.
 * @property id The payout profile (`lankaqr`) or the payment (`slips`).
 * @property expires Unix seconds the signature is good until; the server, not the client, enforces it.
 * @property signature The HMAC over `(kind, id, expires)`.
 */
internal data class SignedFileLink(val kind: ModeBFileKind, val id: Ulid, val expires: Long, val signature: String) {

    internal companion object {

        /** The path prefix every Mode B document link carries. */
        const val PATH: String = "/v1/mode-b/files/"

        /**
         * Reads [link], or answers `null` when it is not a Mode B file link this build understands.
         *
         * `null` rather than an exception for every failure — a malformed link is a server or a
         * version problem, and the screen's answer to both is the same: draw the transfer details
         * it does have and say the QR is unavailable. A thrown parse error would turn a missing
         * image into a failed payment.
         */
        @Suppress("ReturnCount") // One guard per malformed-input case; nesting them reads worse.
        fun parse(link: String?): SignedFileLink? {
            val value = link?.trim().orEmpty()
            val start = value.indexOf(PATH)
            if (start < 0) return null

            val remainder = value.substring(start + PATH.length)
            val path = remainder.substringBefore('?')
            val query = remainder.substringAfter('?', missingDelimiterValue = "")

            val segments = path.split('/')
            if (segments.size < SEGMENTS) return null

            val kind = ModeBFileKind.entries.firstOrNull { it.wire == segments[0] } ?: return null
            val id = segments[1].takeIf(String::isNotBlank) ?: return null

            val parameters = query.split('&')
                .mapNotNull { pair ->
                    val name = pair.substringBefore('=', missingDelimiterValue = "")
                    val raw = pair.substringAfter('=', missingDelimiterValue = "")
                    if (name.isBlank() || raw.isBlank()) null else name to raw
                }
                .toMap()

            val expires = parameters[PARAM_EXPIRES]?.toLongOrNull() ?: return null
            val signature = parameters[PARAM_SIGNATURE] ?: return null

            return SignedFileLink(kind = kind, id = id, expires = expires, signature = signature)
        }

        /** `{kind}` and `{id}` — the two path segments after the prefix. */
        private const val SEGMENTS = 2
        private const val PARAM_EXPIRES = "expires"
        private const val PARAM_SIGNATURE = "signature"
    }
}
