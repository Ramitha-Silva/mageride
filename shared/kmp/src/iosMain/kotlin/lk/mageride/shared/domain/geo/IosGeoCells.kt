package lk.mageride.shared.domain.geo

// Building a GeoCellSubscription from Swift (C094).
//
// WHY THIS EXISTS. `GeoCellSubscription`'s third parameter is the 30 s boundary hysteresis and its
// type is `kotlin.time.Duration` — an inline value class the Objective-C export flattens to an
// opaque `Long` whose encoding is a packed nanos/millis pair with a tag bit, not a nanosecond count
// (the trap C090 recorded). Kotlin default arguments do not survive the export either, so a Swift
// call site could not simply omit it: every parameter becomes required, and the app would be
// passing a raw integer for a value ADD §7.4 step 6 has already fixed.
//
// So the spec's numbers stay where they are documented and Swift asks for the *view* it wants. This
// is the same shape as `IosMqttPlan` and `colomboBusinessDateNow` — a factory on the Kotlin side of
// the bridge for a value whose defaults are the specification.

/**
 * R-06's passenger live-map subscription: H3 res-7 + `ring(2)` = 19 cells, with the ADD §7.4 step 6
 * thirty-second boundary hysteresis.
 *
 * @param grid The app's H3 engine — see [H3Grid], and `shared/swiftpm/MageRideH3` for what iOS
 *   binds. Cell ids must be bit-identical to the ones `position-processor-svc` computes.
 */
public fun passengerCellSubscription(grid: H3Grid): GeoCellSubscription =
    GeoCellSubscription(grid, GeoView.PASSENGER_3KM)

/**
 * The cell tokens of [update]'s three sets, as the wire form `JoinGeocells(cells: string[])` takes.
 *
 * A convenience on the Kotlin side rather than a `map` in Swift for one reason: `Set<H3Cell>` crosses
 * as an `NSSet` of exported Kotlin objects, and `H3Cell.token` — the canonical lowercase-hex spelling
 * the group name is built from — is a computed property. Doing the projection here means the Swift
 * transport handles arrays of `String` and never an `NSSet` of anything.
 */
public fun cellTokens(cells: Set<H3Cell>): List<String> = cells.map(H3Cell::token)
