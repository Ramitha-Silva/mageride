// Named after what it builds rather than after any one of the functions, as every other `iosMain`
// helper in this module is.
@file:Suppress("MatchingDeclarationName")

package lk.mageride.shared.data.models.iam

// SCR-PI-026/026a's bridge: the one shape it builds and the two flags it reads (C101).
//
// ### Why
//
// `SavedAddress.isHome` / `.isWork` and `SavedAddressInput`'s pair are **optional primitives**, and an
// optional primitive is the one thing this bridge makes genuinely awkward: a `Boolean?` crosses as a
// boxed `KotlinBoolean?`, so a Swift call site has to construct a box whose initialiser spelling is a
// property of the compiler and of Foundation's `NSNumber` API notes rather than of this codebase —
// `apps/driver-ios` and `apps/passenger-ios` currently disagree about it in files neither host has
// compiled (the C096 finding). `IosBookingRequests.kt` gives the same argument at length and takes the
// same way out.
//
// It matters more here than it usually does, because the flag pair is not decoration: it is the whole
// of US-22.1. An `isHome` that crossed as a box the Swift side spelled wrongly would be a passenger
// whose Home address silently became an unlabelled one.
//
// The second reason is the one every `iosMain` helper here gives: **a Kotlin default argument does not
// survive the export**, so a Swift caller building a `SavedAddressInput` passes all eight fields at
// every call site whatever the contract declares.

/**
 * `POST` / `PUT /v1/me/saved-addresses`' body (US-22.2/22.3, AL-26).
 *
 * @param line2 Area or suburb. Blank is sent as absent — the contract types it optional, and a line
 *   the passenger left empty is a line they do not have, not a line that is empty.
 * @param line3 City or district, same rule.
 * @param isHome Whether this row is the Home shortcut. **Always written, including `false`**, because
 *   the `PUT` is a full replacement: omitting the flag on an edit would clear a Home the passenger
 *   still has. At most one Home and one Work per user — the C003 partial unique indexes refuse a
 *   second, and moving the flag clears it from whichever address held it.
 */
public fun savedAddressInputOf(
    label: String,
    line1: String,
    line2: String?,
    line3: String?,
    lat: Double,
    lng: Double,
    isHome: Boolean,
    isWork: Boolean,
): SavedAddressInput = SavedAddressInput(
    label = label,
    line1 = line1,
    line2 = line2?.takeIf { it.isNotBlank() },
    line3 = line3?.takeIf { it.isNotBlank() },
    lat = lat,
    lng = lng,
    isHome = isHome,
    isWork = isWork,
)

/**
 * [address] with its shortcut flags replaced.
 *
 * What SCR-PI-026 does to the **other** rows after a save: the server clears the Home or Work flag
 * from whichever address held it, and the screen applies the same rule locally rather than paying a
 * round trip to learn it — and rather than showing two Home rows until the answer arrives.
 *
 * A Kotlin `copy` because an exported data class does not carry one across the bridge: a Swift caller
 * would have to re-list all nine fields, which is nine chances to drop one.
 */
public fun savedAddressWithShortcuts(address: SavedAddress, isHome: Boolean, isWork: Boolean): SavedAddress =
    address.copy(isHome = isHome, isWork = isWork)

/** Whether [address] is the Home shortcut. An absent flag is `false`, never a third state. */
public fun savedAddressIsHome(address: SavedAddress): Boolean = address.isHome == true

/** Whether [address] is the Work shortcut. */
public fun savedAddressIsWork(address: SavedAddress): Boolean = address.isWork == true

/**
 * Whether [input] asks for the Home shortcut.
 *
 * The read side of [savedAddressInputOf], and it exists for the tests: asserting *"the `PUT` cleared
 * the flag"* means reading the body the screen built, and that body's two flags are boxed for the
 * same reason everything else in this file is.
 */
public fun savedAddressInputIsHome(input: SavedAddressInput): Boolean = input.isHome == true

/** Whether [input] asks for the Work shortcut. */
public fun savedAddressInputIsWork(input: SavedAddressInput): Boolean = input.isWork == true
