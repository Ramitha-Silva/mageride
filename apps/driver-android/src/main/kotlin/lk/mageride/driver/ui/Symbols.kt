package lk.mageride.driver.ui

/**
 * The characters the wireframes use as punctuation, and why none of them is copy.
 *
 * A symbol is the **same string in Sinhala, Tamil and English**, so putting one in the three
 * `strings.xml` files means three identical values — which `StringResourceTest` reads, correctly, as
 * a key that was copied into si and ta and never translated. `Rs`, `+94`, the language endonyms and
 * `ScheduleLabels.ROUTE_ARROW` are all here for the same reason (C068's rule); these three were
 * spelled separately in three files until C074 collected them.
 */
internal object Symbols {

    /** The middle dot two facts are separated by — `Three-wheeler · ABC-1234`. */
    const val DOT: String = "·"

    /**
     * What is drawn where a value is not known — a read in flight, one that failed, or a number
     * the platform does not serve at all.
     *
     * An em dash rather than a zero: zero is a value a driver can legitimately have, and being told
     * they have it when nothing was read is worse than being told nothing.
     */
    const val UNKNOWN: String = "—"

    /** A filled star. SCR-DA-029's overall rating and SCR-DA-030's per-trip ones. */
    const val STAR_FILLED: String = "★"

    /** Its outline, for the unearned half of a five-star row. */
    const val STAR_EMPTY: String = "☆"
}
