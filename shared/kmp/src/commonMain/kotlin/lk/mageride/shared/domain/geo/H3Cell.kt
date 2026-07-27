package lk.mageride.shared.domain.geo

// H3 geocells — the addressing scheme the whole fan-out plane is built on (ADD §7.4, D5' §5.4).
//
// A cell id is a 64-bit integer with a fixed bit layout, and the platform passes it around in its
// canonical lowercase-hex spelling: the SignalR group is `cell:{h3index}`, the Redpanda topic is
// `cell.{h3index}` and the driver index key is `geo:drivers:available:{type}:{res5cell}`. Getting
// that spelling wrong is not a rendering bug — it is a client subscribing to a group nothing
// publishes to, which looks exactly like "no vehicles nearby".

/** Bit layout of an H3 index (h3 v4 `h3Index` documentation). */
private const val MODE_SHIFT = 59
private const val MODE_MASK = 0xFL
private const val CELL_MODE = 1L
private const val RESERVED_SHIFT = 56
private const val RESERVED_MASK = 0x7L
private const val RESOLUTION_SHIFT = 52
private const val RESOLUTION_MASK = 0xFL
private const val BASE_CELL_SHIFT = 45
private const val BASE_CELL_MASK = 0x7FL
private const val DIGIT_BITS = 3
private const val DIGIT_MASK = 0x7L

/** A digit slot above the cell's own resolution is all-ones (`7`) — "unused". */
private const val UNUSED_DIGIT = 7L

/** Digits `0..6` address the seven children; `7` is the unused marker, never a real digit. */
private const val MAX_REAL_DIGIT = 6L

internal const val H3_MAX_RESOLUTION = 15
internal const val H3_BASE_CELL_COUNT = 122
private const val HEX_RADIX = 16

/**
 * One H3 cell, as its 64-bit index.
 *
 * Wrapping the `Long` costs nothing and buys two things a bare number does not: a
 * [token] that is always the canonical spelling the server expects, and a type that cannot be
 * confused with a vehicle id, a sequence number or a resolution. Both apps and both platforms
 * exchange these with `fanout-svc` (C041) and `query-svc` (C042), so the spelling is a contract.
 *
 * The index layout is part of the H3 specification and is stable across versions, which is why
 * [resolution] and [baseCell] are read here rather than asked of the platform [H3Grid]:
 *
 * ```
 * bit 63      reserved, always 0
 * bits 59-62  mode — 1 for a cell index
 * bits 56-58  mode-dependent, 0 for a cell index
 * bits 52-55  resolution, 0-15
 * bits 45-51  base cell, 0-121
 * bits 0-44   fifteen 3-bit digits, unused ones set to 7
 * ```
 *
 * @property index The raw H3 index.
 */
public data class H3Cell(public val index: Long) {

    /** The canonical lowercase-hex spelling — what `cell:{h3index}` and `cell.{h3index}` carry. */
    public val token: String get() = index.toULong().toString(HEX_RADIX)

    /** The cell's resolution, `0..15`. Res 7 is the passenger view, res 5 the dispatch index. */
    public val resolution: Int get() = ((index ushr RESOLUTION_SHIFT) and RESOLUTION_MASK).toInt()

    /** Which of the 122 base cells this one descends from. */
    public val baseCell: Int get() = ((index ushr BASE_CELL_SHIFT) and BASE_CELL_MASK).toInt()

    /**
     * Whether the index has the shape of a cell index.
     *
     * This is a **structural** check — mode, reserved bits, resolution range, and every digit
     * either a real `0..6` inside the resolution or the `7` marker outside it. It deliberately
     * stops short of H3's own `isValidCell`, which additionally rejects the deleted subsequence of
     * a pentagon; that needs the base-cell tables, which live in the platform library. Everything
     * this returns `false` for is invalid; a `true` is "well-formed", not "certainly resolvable".
     */
    public val isWellFormed: Boolean
        get() {
            if (index < 0) return false // bit 63 reserved
            if ((index ushr MODE_SHIFT) and MODE_MASK != CELL_MODE) return false
            if ((index ushr RESERVED_SHIFT) and RESERVED_MASK != 0L) return false
            if (baseCell >= H3_BASE_CELL_COUNT) return false
            val res = resolution
            for (digit in 1..H3_MAX_RESOLUTION) {
                val shift = (H3_MAX_RESOLUTION - digit) * DIGIT_BITS
                val value = (index ushr shift) and DIGIT_MASK
                if (digit <= res && value > MAX_REAL_DIGIT) return false
                if (digit > res && value != UNUSED_DIGIT) return false
            }
            return true
        }

    /** The [token], so a cell interpolated into a log line or a group name reads correctly. */
    override fun toString(): String = token

    public companion object {

        /**
         * Reads the canonical hex spelling.
         *
         * @throws IllegalArgumentException when [token] is not hexadecimal or is not a
         *   well-formed cell index — a group name built from a malformed cell would silently
         *   subscribe to nothing.
         */
        public fun parse(token: String): H3Cell = requireNotNull(parseOrNull(token)) { "Not an H3 cell index: $token" }

        /** As [parse], but answers `null` instead of throwing — for untrusted input. */
        public fun parseOrNull(token: String): H3Cell? {
            val value = token.toULongOrNull(HEX_RADIX) ?: return null
            return H3Cell(value.toLong()).takeIf { it.isWellFormed }
        }
    }
}
