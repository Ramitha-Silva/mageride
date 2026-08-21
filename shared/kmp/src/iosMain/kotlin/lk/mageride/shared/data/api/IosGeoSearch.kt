// Named after the operation rather than after what it answers with, as every other `iosMain` helper
// in this module is.
@file:Suppress("MatchingDeclarationName")

package lk.mageride.shared.data.api

import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.query.GeocodedPlace

/**
 * `GET /v1/geo/search` as SCR-PI-008 makes it (C096).
 *
 * ### Why this is not a Swift call site
 *
 * The usual reason first: **a Kotlin default argument does not survive the Objective-C export**, so
 * a Swift caller has to pass all four parameters. Three of them are *optional primitives*, and that
 * is the part worth moving: `lat: Double?` crosses as a boxed `KotlinDouble?` and `limit: Int?` as a
 * `KotlinInt?`, so the call site has to construct two boxes whose Swift initialiser spelling is a
 * property of the compiler and of Foundation's `NSNumber` API notes rather than of this codebase —
 * `apps/driver-ios` and `apps/passenger-ios` currently disagree about it in files neither host has
 * compiled. Taking a **`GeoPoint?`** instead moves the optionality onto a class, where there is
 * nothing to box and nothing to spell.
 *
 * It is also the narrower truth. AL-17 makes SCR-PI-008 **geo only** — `QueryApi.getBusesOnRoute`
 * exists and that screen must never reach it — and a function that answers places is a function
 * nobody can quietly turn into one that answers routes.
 *
 * ### Why there is no `lang` here yet
 *
 * `QueryApi.searchPlaces` gained one (D-26 — destination search answers in Sinhala and Tamil), and
 * this helper deliberately did not follow. For the reason above: a fifth parameter changes the
 * exported selector from `…around:limit:completionHandler:` to `…around:limit:lang:completionHandler:`
 * and every Swift call site in `apps/driver-ios` and `apps/passenger-ios` stops compiling — on a
 * host that cannot compile Swift to find out. Adding it is one line here and one line at each call
 * site, and belongs in a session with a Mac. Until then both iOS apps get the untranslated answer,
 * which is what they already had.
 *
 * @param query query-svc's client, from `IosAppGraph.api`.
 * @param text What the passenger typed.
 * @param around Where to bias the results, or `null` before the first fix.
 * @param limit How many rows to ask for.
 */
public suspend fun searchPlacesNear(
    query: QueryApi,
    text: String,
    around: GeoPoint?,
    limit: Int,
): List<GeocodedPlace> = query.searchPlaces(
    query = text,
    lat = around?.lat,
    lng = around?.lng,
    limit = limit,
).places
