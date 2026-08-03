package lk.mageride.driver.tracker

/**
 * What SCR-DA-027 accepts in its IMEI field, and what a device QR code means.
 *
 * Pure Kotlin and testable on this host, for the same reason `CropQuad` (C069) and `WalletInput`
 * (C073) are: the two questions this screen asks — *is that an IMEI?* and *which fifteen digits in
 * that QR payload are one?* — decide what gets bound to a vehicle, and neither should be answered
 * inside a composable or inside a class that needs a camera.
 *
 * ### The pattern is the contract's, not a guess
 *
 * `provisioning.yaml#/components/schemas/Imei` and `registry.yaml`'s copy of it are both
 * `^\d{15}$` — fifteen digits, nothing else. There is deliberately **no Luhn check** here even
 * though the fifteenth digit of a real IMEI is one: neither contract asks for it, `prov.trackers`
 * does not enforce it, and a client that refused a check-digit the server would have accepted
 * would make a perfectly good tracker unpairable at the roadside with no way round it.
 */
internal object TrackerImei {

    /** The contract's length. Fifteen digits, and the field stops there. */
    const val LENGTH: Int = 15

    /** How many digits are printed per group in [grouped] — the wireframe's `8612 3456 7890 123`. */
    private const val GROUP = 4

    /** `^\d{15}$`, once the field has been reduced to digits. */
    private val IMEI = Regex("^\\d{$LENGTH}$")

    /**
     * Fifteen digits that are not part of a longer run.
     *
     * The lookarounds are what stop a payload carrying a 20-digit ICCID from yielding its first
     * fifteen characters as an "IMEI" — a serial that would bind, mint a credential and then never
     * connect.
     */
    private val IMEI_IN_TEXT = Regex("(?<!\\d)\\d{$LENGTH}(?!\\d)")

    /**
     * What the field keeps of a keystroke — digits only, capped at [LENGTH].
     *
     * Separators are dropped rather than rejected so an IMEI copied off a sticker as
     * `8612 3456 7890 123` or `861234-56-789012-3` behaves; the screen renders the grouping itself
     * through [grouped].
     */
    fun digits(raw: String): String = raw.filter(Char::isDigit).take(LENGTH)

    /** Whether [raw] is a well-formed IMEI once reduced to digits. */
    fun isValid(raw: String): Boolean = IMEI.matches(digits(raw))

    /** `861234567890123` → `8612 3456 7890 123`, for reading a paired device's id back. */
    fun grouped(imei: String): String = digits(imei).chunked(GROUP).joinToString(" ")

    /**
     * The IMEI inside a scanned device QR code, or `null` when there is not exactly one.
     *
     * **No format is specified anywhere.** D2' §SCR-DA-027 says the QR carries the *"device QR"*
     * and provisioning-svc's `method` enum has a `qr` value, but no spec, contract or DB column
     * says what the tracker vendor prints in it. In the field it is one of three things: the bare
     * IMEI, a `key:value` line (`IMEI:861234567890123`), or a provisioning URL with the IMEI in a
     * query parameter — so this looks for the number rather than for a shape, and refuses a payload
     * carrying **two** different candidates instead of picking the first. A scan that cannot be
     * read leaves the driver typing, which is the path that always works.
     */
    fun imeiIn(payload: String): String? = IMEI_IN_TEXT
        .findAll(payload)
        .map(MatchResult::value)
        .distinct()
        .toList()
        .singleOrNull()
}
