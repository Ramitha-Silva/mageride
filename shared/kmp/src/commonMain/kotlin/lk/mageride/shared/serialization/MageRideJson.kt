package lk.mageride.shared.serialization

import kotlinx.serialization.json.Json

/**
 * The one [Json] the whole client uses — REST bodies (D3') and MQTT payloads (ADD Appendix A)
 * are the same wire format and must not drift between two configurations.
 *
 * - `ignoreUnknownKeys` — the gateway is versioned but additive: a field added server-side
 *   must never crash an older build (D3' §0 version gate only blocks *breaking* changes).
 * - `explicitNulls = false` — the backend serialises with `WhenWritingNull` (C002), so an
 *   absent field and a null field mean the same thing; sending both shapes would double the
 *   payload matrix for no gain.
 * - `encodeDefaults = false` — keeps request bodies to what the caller actually set, which is
 *   what the idempotency-key replay comparison (R-14) sees.
 *
 * NOT lenient and NOT `coerceInputValues`: a malformed number or an unknown enum value is a
 * contract violation and must surface as an error, not as a silent default.
 */
public val MageRideJson: Json = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = false
    isLenient = false
}
