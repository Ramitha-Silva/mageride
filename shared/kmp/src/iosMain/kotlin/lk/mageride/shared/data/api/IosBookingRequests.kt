// Named after what it builds rather than after any one of the types, as every other `iosMain`
// helper in this module is.
@file:Suppress("MatchingDeclarationName")

package lk.mageride.shared.data.api

import lk.mageride.shared.data.api.transit.TransitApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.transit.TransitLeg
import lk.mageride.shared.data.models.transit.TransitRoute

// Cluster 3's bridge: the shapes it builds, and the two reads whose Swift spelling is not this
// codebase's to choose (C097).
//
// ### Why
//
// Every one of these has an **optional primitive** in it, and an optional primitive is the one thing
// this bridge makes genuinely awkward: `Double?` crosses as a boxed `KotlinDouble?` and `Boolean?` as
// a `KotlinBoolean?`, so a Swift call site has to construct a box whose initialiser spelling is a
// property of the compiler and of Foundation's `NSNumber` API notes rather than of this codebase —
// `apps/driver-ios` and `apps/passenger-ios` currently disagree about it in files neither host has
// compiled. A `GeoPoint?` or a `Place?` is a **class**, so there is nothing to box and nothing to
// spell. It is the same reason `IosAppConfig` exists rather than a Swift-built `ApiConfig`, and the
// same reason `searchPlacesNear` takes a point.
//
// The second reason is the one every `iosMain` helper here gives: **a Kotlin default argument does
// not survive the export**, so a Swift caller would be passing all fifteen of `RideRequest`'s fields
// at every call site, most of them `nil`.
//
// The two ride factories are deliberately **two**, not one with more optionals. A passenger booking
// and a parcel differ in what they carry — `isProxy` versus the four package fields — and two
// signatures say which is which, where one signature with nine nullable parameters says nothing.

/**
 * `GET /v1/transit/routes/{routeId}`, biased toward a point.
 *
 * @param around Where the passenger is, so the answer's `nearestStops` are theirs. `null` asks for
 *   the route alone.
 */
public suspend fun transitRouteNear(transit: TransitApi, routeId: String, around: GeoPoint?): TransitRoute =
    transit.getTransitRoute(routeId = routeId, lat = around?.lat, lng = around?.lng)

/**
 * A leg's *"via High Level Rd"* line — SCR-*-009's second row on a public option.
 *
 * **A wrapper because `description` is `NSObject`'s**, which is the trap C093 hit with
 * `TicketDetail.description` and wrote up in `IosTicket.kt`: every Kotlin class reaches Objective-C
 * as a `KotlinBase` subclass, `NSObject` already declares `- (NSString *)description`, and the
 * exporter resolves the collision by mangling the Kotlin name. A Swift call site reaching for
 * `leg.description` therefore gets either nothing or `NSObject`'s own debug string — and the second
 * outcome puts `TransitLeg(routeId=…)` under a bus route on a passenger's screen.
 */
public fun transitLegDescription(leg: TransitLeg): String? = leg.description

/**
 * A shared position, with its accuracy when there is one (P-02, SCR-*-011).
 *
 * @param accuracyMetres The fix's horizontal accuracy. **Anything at or below zero is `null`**,
 *   which is both platforms' own convention for "not reported" — Core Location's sentinel is a
 *   negative number, and a zero-radius circle is not a measurement.
 */
public fun geoPointWithAccuracy(lat: Double, lng: Double, accuracyMetres: Double): GeoPointWithAccuracy =
    GeoPointWithAccuracy(lat = lat, lng = lng, accuracy = accuracyMetres.takeIf { it > 0 })

/**
 * `POST /v1/rides/schedule`'s body (US-6A.4).
 *
 * @param pickup Nullable in the contract, and legitimately so: a scheduled ride with no pickup is
 *   one that starts wherever the passenger is at the time.
 */
public fun scheduleRideRequestFor(
    pickup: Place?,
    dropoff: Place,
    pickupTime: Timestamp,
    vehicleType: RideVehicleType,
): ScheduleRideRequest = ScheduleRideRequest(
    pickupLat = pickup?.lat,
    pickupLng = pickup?.lng,
    destLat = dropoff.lat,
    destLng = dropoff.lng,
    pickupTime = pickupTime,
    vehicleType = vehicleType,
)

/**
 * `POST /v1/rides/request`'s body for a **person** — the passenger's own ride, or a proxy one
 * (P-01).
 *
 * `isProxy` is derived from [kind] rather than taken, because the two are one fact and a caller that
 * could disagree with itself is a caller that will. A blank rider name or number is sent as absent:
 * the contract's fields are optional and an empty string is not a name.
 */
public fun rideRequestFor(
    clientRequestId: String,
    kind: RideKind,
    pickup: Place,
    dropoff: Place,
    vehicleType: RideVehicleType,
    fareEstimateToken: String,
    paymentMethod: RidePaymentMethod,
    riderName: String?,
    riderPhone: String?,
): RideRequest = RideRequest(
    clientRequestId = clientRequestId,
    kind = kind,
    pickup = pickup,
    dropoff = dropoff,
    vehicleType = vehicleType,
    fareEstimateToken = fareEstimateToken,
    paymentMethod = paymentMethod,
    isProxy = kind == RideKind.PROXY,
    riderName = riderName?.takeIf { it.isNotBlank() },
    riderPhone = riderPhone?.takeIf { it.isNotBlank() },
)

/**
 * `POST /v1/rides/request`'s body for a **parcel** (US-20.1, P-06).
 *
 * Same operation and the same fare as a person's ride (US-20.9); what differs is the cargo. `isProxy`
 * is deliberately absent rather than `false` — `RideKind.PACKAGE` already says who this is for, and
 * a parcel booked on somebody's behalf is still a parcel.
 */
public fun packageRideRequestFor(
    clientRequestId: String,
    pickup: Place,
    dropoff: Place,
    vehicleType: RideVehicleType,
    fareEstimateToken: String,
    paymentMethod: RidePaymentMethod,
    packageSize: PackageSize,
    packageDescription: String,
    recipientName: String,
    recipientPhone: String,
): RideRequest = RideRequest(
    clientRequestId = clientRequestId,
    kind = RideKind.PACKAGE,
    pickup = pickup,
    dropoff = dropoff,
    vehicleType = vehicleType,
    fareEstimateToken = fareEstimateToken,
    paymentMethod = paymentMethod,
    packageSize = packageSize,
    packageDescription = packageDescription,
    recipientName = recipientName,
    recipientPhone = recipientPhone,
)
