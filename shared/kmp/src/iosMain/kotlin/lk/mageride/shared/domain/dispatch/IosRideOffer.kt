// Named after what it reads rather than after the one function, as every other `iosMain` helper in
// this module is.
@file:Suppress("MatchingDeclarationName")

package lk.mageride.shared.domain.dispatch

/**
 * Whether [offer] is a third-party booking (P-05) — SCR-DI-014's *"Third-party booking"* badge.
 *
 * **A wrapper because `isProxy` is `NSObject`'s.** This is the same trap `ticketDescription` exists
 * for, and the second instance of it in this module. Every Kotlin class reaches Objective-C as a
 * subclass of `KotlinBase`, which is an `NSObject`, and `NSObject` already declares
 * `- (BOOL)isProxy` — the `NSProxy` test. `RideOffer.isProxy` therefore collides with an inherited
 * selector, and a Swift call site written as `offer.isProxy` binds to the inherited **method**: it
 * reads as `() -> Bool`, not as this property.
 *
 * What makes this one worse than the `description` case is that the wrong answer is a *plausible*
 * one. `TicketDetail.description` misresolved prints `TicketDetail(ticketId=…)` at a driver, which
 * somebody notices. `NSObject.isProxy()` returns **`false`** for every object that is not an
 * `NSProxy` — which is every object here — so `offer.isProxy()` compiles, runs, and silently answers
 * "not a proxy booking" for a booking that is one. The badge simply never appears, and P-05's whole
 * point is that the driver is told before they accept.
 *
 * The Swift side cannot fix this by spelling: the property is unreachable under that name whatever
 * the call site does. Only the constructor parameter survives, because it becomes part of an
 * `initWith…:` selector and collides with nothing — which is why building a `RideOffer` from Swift
 * works and reading one back does not.
 *
 * Checked while writing this: `directionalMatched` and the rest of `RideOffer` are ordinary names.
 * The members worth checking before reaching for one from Swift are `NSObject`'s — `description`,
 * `hash`, `debugDescription`, `class`, `isProxy`, `superclass`, `self`.
 *
 * @param offer The offer the `ride_offer` push and `GET /v1/rides/{rideId}` produced.
 */
public fun rideOfferIsProxy(offer: RideOffer): Boolean = offer.isProxy
