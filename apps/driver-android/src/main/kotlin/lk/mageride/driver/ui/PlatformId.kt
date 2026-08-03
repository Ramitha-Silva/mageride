package lk.mageride.driver.ui

import lk.mageride.shared.data.models.Ulid

/**
 * What the platform means by an id, and what a field that takes one accepts.
 *
 * **There is no `DRV-22011` and no `PAX-90431`.** Both appear in the wireframes — the first on
 * SCR-DA-023/024, the second on SCR-DA-028 — and neither exists. Every route that takes a user is
 * declared `_shared.yaml#/components/schemas/Ulid`: 26–36 characters of the Crockford alphabet plus
 * the hyphen a canonical UUID carries. Neither `iam.users` nor `registry.driver_profiles` has a
 * human-readable code column, and no route on the platform resolves one form into the other.
 * Recorded as a spec gap by C073 for the driver id and again by C074 for the passenger id.
 *
 * Extracted here by C074 so the two screen groups that ask for an id ask the same question.
 * [lk.mageride.driver.wallet.WalletInput] is still the wallet cluster's door and delegates to this.
 *
 * The value is **trimmed and never rewritten**: `Primitives.kt` is explicit that a client which
 * silently reshaped a server-supplied identifier is a worse failure than one that passed it
 * through, and a ULID is upper-case while a UUID is lower-case — case-folding either breaks the
 * other. Trimming is safe and necessary: a paste out of a chat app carries a trailing newline, and
 * that is nobody's identity.
 */
internal object PlatformId {

    /** `_shared.yaml#/components/schemas/Ulid` — `minLength`. */
    const val MIN_LENGTH: Int = 26

    /** The same schema's `maxLength`; a UUID with its hyphens is 36. */
    const val MAX_LENGTH: Int = 36

    /** The contract's charset, anchored. Crockford base32 plus the hyphen. */
    private val PATTERN = Regex("^[0-9A-HJKMNP-TV-Za-hjkmnp-tv-z-]{$MIN_LENGTH,$MAX_LENGTH}$")

    /** The id as it will be sent — surrounding whitespace removed, nothing else touched. */
    fun of(raw: String): Ulid = raw.trim()

    /** Whether [raw] is a well-formed platform id, once trimmed. */
    fun isValid(raw: String): Boolean = PATTERN.matches(of(raw))
}
