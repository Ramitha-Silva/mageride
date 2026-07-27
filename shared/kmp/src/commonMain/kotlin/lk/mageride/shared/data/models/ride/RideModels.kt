package lk.mageride.shared.data.models.ride

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.PhoneMasked
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType

// ride-svc — the Mode C ride aggregate (SOLE writer).
// Source: backend/contracts/ride.yaml (D3' "ride-svc — Mode C Ride Aggregate", ADD Appendix C
// v2.1/v2.2, Δ 2026-06-28 AL-36, Δ 2026-07-05 #2 AL-48).
//
// EVERY MUTATION CARRIES `Idempotency-Key` AND EVERY RESPONSE CARRIES `version`. Clients echo the
// version back on the next mutation, so a stale client sees 409 version-conflict instead of
// silently overwriting (R-14). See RideVersion and VersionedCommand.
//
// Three booking kinds share ONE aggregate and traverse the SAME states (ADD Appendix B.2
// invariant 6): `passenger`, `proxy` (booker ≠ rider, rider may be unregistered, P-01) and
// `package` (no rider at all; two 4-digit OTPs gate the handoffs, P-06/P-07/P-10).
//
// Offer acceptance is a single-winner conditional UPDATE inside a 15-second TTL (R-02, §11.11):
// the loser gets 409 offer-already-accepted, a late arrival gets 410 offer-expired.

/**
 * Which of the three booking kinds a ride is (`rides.rides.kind`, C004 — stored `0|1|2`, carried
 * on the wire as these names).
 *
 * The state machine is kind-agnostic; only the invariants differ (ADD Appendix B.2 invariant 6).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class RideKind(public val wire: String) {
    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("proxy")
    PROXY("proxy"),

    @SerialName("package")
    PACKAGE("package"),
}

/**
 * The payment method chosen **at booking time** (`rides.rides.payment_method` CHECK, AL-22).
 *
 * Deliberately narrower than the settlement-time method: `cod` is a booking-time choice and
 * `scan_driver_qr` is a settlement-time one, so the two columns have genuinely different domains
 * (C004 note (f)). `cod` is **package-only**.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class RidePaymentMethod(public val wire: String) {
    @SerialName("cash")
    CASH("cash"),

    @SerialName("lankaqr")
    LANKAQR("lankaqr"),

    @SerialName("onepay")
    ONEPAY("onepay"),

    @SerialName("cod")
    COD("cod"),
}

/**
 * How far a package has got (`ride.yaml#/components/schemas/RideDetail.packageStatus`).
 *
 * Runs beside [RideState] rather than inside it — the ride states are kind-agnostic, so the
 * package's own handoff progress needs its own field.
 */
@Serializable
public enum class PackageStatus {
    PickupPending,
    PickedUp,
    InTransit,
    Delivered,
}

/**
 * Why a ride was cancelled from the app (`ride.yaml` `POST /v1/rides/{rideId}/cancel`).
 *
 * The **effect** of a cancel follows the §11.12 matrix and the ride's current state, not this
 * reason: after acceptance the Rs 50 cross-trip penalty accrues regardless (D-05, §11.7).
 */
@Serializable
public enum class RideCancelReason {
    RIDER_CHANGED_MIND,
    DRIVER_TOO_FAR,
    EMERGENCY,
    OTHER,
}

/**
 * Why the platform cancelled a ride without a user asking
 * (`POST /v1/internal/rides/{rideId}/system-cancel`, R-15/R-16).
 *
 * **No passenger penalty accrues on a system cancel.**
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SystemCancelReason(public val wire: String) {
    @SerialName("driver_offline_grace_expired")
    DRIVER_OFFLINE_GRACE_EXPIRED("driver_offline_grace_expired"),

    @SerialName("fraud_lock")
    FRAUD_LOCK("fraud_lock"),

    @SerialName("no_driver_found")
    NO_DRIVER_FOUND("no_driver_found"),

    @SerialName("admin_intervention")
    ADMIN_INTERVENTION("admin_intervention"),
}

/**
 * When an accrued cancellation debt is collected.
 *
 * The contract pins this to `const: next-trip`: the Rs 50 **outlives the ride** and settles on the
 * passenger's next trip, keyed `penaltyId:rideId` in `billing.journal_entries.idempotency_key`
 * (D5' §7.1). It is an enum with one value because that is what the contract declares.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class PenaltySettlement(public val wire: String) {
    @SerialName("next-trip")
    NEXT_TRIP("next-trip"),
}

/**
 * Where a proxy booking's "where are you?" round-trip has got to
 * (`ride.yaml#/components/schemas/LocationRequestState`, `rides.location_requests.state`).
 *
 * [RiderNotRegistered] means the SMS `pickup_confirm` web path was taken instead of the in-app
 * one (AL-45) — the request is still live, just resolving through public-bff.
 */
@Serializable
public enum class LocationRequestState {
    Pending,
    RiderNotRegistered,
    Confirmed,
    Declined,
    Expired,
}

// ---------------------------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------------------------

/**
 * The body every plain ride transition takes
 * (`ride.yaml#/components/schemas/VersionedCommand`).
 *
 * @property version The version the client last saw. A stale one is `409 version-conflict`.
 */
@Serializable
public data class VersionedCommand(val version: RideVersion)

/**
 * A four-digit OTP attempt (`ride.yaml#/components/schemas/OtpAttempt`).
 *
 * **At most five attempts** on either package handoff, after which the endpoint answers
 * `423 otp-locked` and the handoff needs support intervention (P-07).
 *
 * @property otp Four digits.
 */
@Serializable
public data class OtpAttempt(val otp: String)

/**
 * `POST /v1/rides/request` (`ride.yaml#/components/schemas/RideRequest`).
 *
 * Idempotent on **two** keys: the `Idempotency-Key` header and `(passengerId, clientRequestId)`
 * (R-18) — a retry after a dropped response returns the existing ride rather than booking a
 * second one.
 *
 * @property clientRequestId The idempotency partner of the header key. Unique per passenger.
 * @property kind Booking kind; defaults to a passenger booking server-side.
 * @property pickup Where the ride starts.
 * @property dropoff Where it ends.
 * @property vehicleType `truck` / `mini_truck` are delivery-only (AL-09).
 * @property fareEstimateToken Opaque token from `GET /v1/fare/estimate`; binds the quoted price.
 *   A stale or forged one is `400 invalid-fare-token`.
 * @property paymentMethod Booking-time method. [RidePaymentMethod.COD] is package-only.
 * @property scheduledAt `null` or absent means immediate dispatch.
 * @property isProxy Proxy booking (P-01). The rider need not be a registered user.
 * @property riderName Who is actually travelling, on a proxy booking.
 * @property riderPhone The rider's number, on a proxy booking. Resolved through iam-svc (P-03).
 * @property packageSize Package bookings only.
 * @property packageDescription Sender-written contents note, at most 500 characters.
 * @property recipientName Package recipient.
 * @property recipientPhone Package recipient's number.
 */
@Serializable
public data class RideRequest(
    val clientRequestId: Ulid,
    val kind: RideKind? = null,
    val pickup: Place,
    val dropoff: Place,
    val vehicleType: RideVehicleType,
    val fareEstimateToken: String,
    val paymentMethod: RidePaymentMethod,
    val scheduledAt: Timestamp? = null,
    val isProxy: Boolean? = null,
    val riderName: String? = null,
    val riderPhone: PhoneE164? = null,
    val packageSize: PackageSize? = null,
    val packageDescription: String? = null,
    val recipientName: String? = null,
    val recipientPhone: PhoneE164? = null,
)

/**
 * A quoted or final fare (`ride.yaml#/components/schemas/FareEstimate`).
 *
 * @property amountMinor Total in integer minor units.
 * @property currency Always LKR.
 * @property surchargeMinor Peak/night/gateway surcharge already included in [amountMinor].
 */
@Serializable
public data class FareEstimate(
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val surchargeMinor: Long? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/rides/request` — 202.
 *
 * @property rideId The created ride.
 * @property state Always [RideState.Requested] on creation.
 * @property version Echo this back on the next mutation.
 * @property pickupOtp **Package bookings only.** Shown once to the sender and never returned
 *   again (P-07); only its hash is stored.
 * @property estimatedFare The price the [RideRequest.fareEstimateToken] bound.
 */
@Serializable
public data class RequestRideResponse(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val pickupOtp: String? = null,
    val estimatedFare: FareEstimate,
)

/**
 * The 200 of every simple ride transition
 * (`ride.yaml#/components/schemas/RideStateChange`).
 *
 * @property rideId The ride.
 * @property state Where it is now.
 * @property version The new version. Echo it back on the next mutation.
 */
@Serializable
public data class RideStateChange(val rideId: Ulid, val state: RideState, val version: RideVersion)

/**
 * `GET /v1/rides/{rideId}/state` — the cheap poll the driver app runs while an offer is live.
 *
 * The normal transport for state changes is the SignalR `RideStateChanged` event; this is the
 * reconnect and fallback path.
 *
 * @property state Where the ride is.
 * @property version Current version.
 * @property offerExpiresAt What the 15-second countdown renders against.
 */
@Serializable
public data class RideStateSnapshot(
    val state: RideState,
    val version: RideVersion,
    val offerExpiresAt: Timestamp? = null,
)

/**
 * The driver attached to a ride (`ride.yaml#/components/schemas/RideDriver`).
 *
 * @property driverId The driver.
 * @property name Display name.
 * @property photoUrl Display photo.
 * @property vehicleType Canonical type of the vehicle en route.
 * @property registrationNumber Plate, so the passenger can identify the vehicle.
 * @property rating Rolling average, 0–5.
 * @property etaSeconds Seconds to pickup.
 */
@Serializable
public data class RideDriver(
    val driverId: Ulid,
    val name: String,
    val photoUrl: String? = null,
    val vehicleType: VehicleType? = null,
    val registrationNumber: String? = null,
    val rating: Double? = null,
    val etaSeconds: Int? = null,
)

/**
 * The full ride aggregate (`ride.yaml#/components/schemas/RideDetail`).
 *
 * **AL-48 — [counterpartyPhone].** From [RideState.Accepted] onward the detail carries the other
 * party's number in E.164: the passenger sees the driver's, the driver sees the **rider's**,
 * never the booker's (P-05). It is absent before acceptance and on rides cancelled before
 * assignment. This field is what makes the client-side `tel:` fallback possible — there is no
 * masking bridge, and `normal_masked` was removed with it.
 *
 * @property rideId The ride.
 * @property kind Booking kind.
 * @property state Where the ride is.
 * @property version Current version.
 * @property bookerId Who booked it.
 * @property riderId Who is travelling. Absent on a proxy booking for an unregistered rider (P-01).
 * @property riderName The rider's name, when the booker supplied one.
 * @property pickup Where the ride starts.
 * @property dropoff Where it ends.
 * @property vehicleType The booked type.
 * @property paymentMethod Booking-time method.
 * @property scheduledAt Future pickup time, on a scheduled ride.
 * @property offerExpiresAt 15-second offer TTL hint, while an offer is live.
 * @property driver The assigned driver, once there is one.
 * @property counterpartyPhone See the note above. Present only from `Accepted` onward.
 * @property fare The estimate, then the final fare.
 * @property packageSize Package bookings only.
 * @property packageDescription Package bookings only.
 * @property packageStatus Handoff progress, package bookings only.
 * @property createdAt When the ride was requested.
 */
@Serializable
public data class RideDetail(
    val rideId: Ulid,
    val kind: RideKind,
    val state: RideState,
    val version: RideVersion,
    val bookerId: Ulid? = null,
    val riderId: Ulid? = null,
    val riderName: String? = null,
    val pickup: Place,
    val dropoff: Place,
    val vehicleType: RideVehicleType,
    val paymentMethod: RidePaymentMethod,
    val scheduledAt: Timestamp? = null,
    val offerExpiresAt: Timestamp? = null,
    val driver: RideDriver? = null,
    val counterpartyPhone: PhoneE164? = null,
    val fare: FareEstimate? = null,
    val packageSize: PackageSize? = null,
    val packageDescription: String? = null,
    val packageStatus: PackageStatus? = null,
    val createdAt: Timestamp,
)

/**
 * `POST /v1/rides/{rideId}/offer/{driverId}/accept` (R-02, §11.11).
 *
 * @property offerId The live offer being accepted.
 * @property version The version the offer was seen at.
 */
@Serializable
public data class AcceptRideOfferRequest(val offerId: Ulid, val version: RideVersion)

/**
 * `POST /v1/rides/{rideId}/offer/{driverId}/accept` — 200. **This driver won.**
 *
 * Exactly one driver's conditional UPDATE touches a row; the others get `409` or `410`.
 *
 * @property rideId The ride.
 * @property state [RideState.Accepted].
 * @property version The new version.
 * @property ride The full aggregate, so the driver app needs no second read.
 */
@Serializable
public data class AcceptRideOfferResponse(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val ride: RideDetail,
)

/**
 * `POST /v1/rides/{rideId}/offer/{driverId}/decline`. No penalty (§11.12).
 *
 * @property offerId The offer being declined.
 */
@Serializable
public data class DeclineRideOfferRequest(val offerId: Ulid)

/**
 * `POST /v1/rides/{rideId}/start` — `allOf(VersionedCommand, { otp })`, flattened.
 *
 * A passenger or proxy ride requires the rider's start OTP; a package ride uses
 * `POST /v1/rides/{rideId}/package/pickup-otp` instead and omits [otp] here.
 *
 * @property version Current version.
 * @property otp Four-digit rider start OTP. Omitted for package rides.
 */
@Serializable
public data class StartRideRequest(val version: RideVersion, val otp: String? = null)

/**
 * `POST /v1/rides/{rideId}/complete` — 200, `allOf(RideStateChange, { fare })`, flattened.
 *
 * `InProgress → Completed → PaymentPending`; fare-svc then computes the final fare from the
 * **Kalman-filtered** distance (E-04). The driver's earning posts only when payment reaches a
 * terminal state (R-05).
 *
 * @property fare The final fare, once fare-svc has computed it.
 */
@Serializable
public data class CompleteRideResponse(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val fare: FareEstimate? = null,
)

/**
 * `POST /v1/rides/{rideId}/cancel` — `allOf(VersionedCommand, { reason })`, flattened.
 *
 * @property version Current version.
 * @property reason Why the caller is cancelling.
 */
@Serializable
public data class CancelRideRequest(val version: RideVersion, val reason: RideCancelReason)

/**
 * The Rs 50 cross-trip cancellation debt (D-05, §11.7).
 *
 * The debt **outlives the ride**: nothing is charged now, and it settles on the passenger's next
 * trip. Three continuous passenger cancels disable booking (US-6A.10b).
 *
 * @property amountMinor Penalty in minor units.
 * @property currency Always LKR.
 * @property settledOn Always [PenaltySettlement.NEXT_TRIP].
 */
@Serializable
public data class CancellationPenalty(
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val settledOn: PenaltySettlement,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/rides/{rideId}/cancel` — 200, `allOf(RideStateChange, { penalty })`, flattened.
 *
 * @property penalty Present only when a penalty accrued — i.e. a rider cancel after acceptance.
 */
@Serializable
public data class CancelRideResponse(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val penalty: CancellationPenalty? = null,
)

/**
 * `POST /v1/rides/{rideId}/dispute` (E-05).
 *
 * Opens a support ticket and moves the payment to [PaymentState.Disputed]. **This endpoint moves
 * no money** — refunds are Finance-only, through `POST /v1/admin/fare/refund`.
 *
 * @property reason Passenger-written, at most 2000 characters.
 */
@Serializable
public data class DisputeRideRequest(val reason: String)

/**
 * `POST /v1/rides/{rideId}/cod-collected` (P-08).
 *
 * Moves the payment to [PaymentState.CashOnDeliveryCollected], which is terminal — the driver's
 * earning posts from there (R-05). Only valid on a package ride booked
 * [RidePaymentMethod.COD].
 *
 * @property collectedMinor What the driver actually took, in minor units.
 */
@Serializable
public data class ConfirmCashOnDeliveryRequest(val collectedMinor: Long) : MoneyHolder {
    override val money: Money get() = Money.ofMinor(collectedMinor)
}

/**
 * `POST /v1/rides/{rideId}/package/proof-photo` — 201 (P-10).
 *
 * The fallback when no one is there to read out the delivery OTP. The receipt at
 * `GET /public/track/{token}/receipt` then reports `photo_proof` instead of `otp_verified`.
 *
 * @property artifactId The stored `rides.proof_artifacts(kind='delivery_photo')` row.
 */
@Serializable
public data class ProofArtifactResponse(val artifactId: Ulid)

// ---------------------------------------------------------------------------------------------
// History (AL-36, US-24.4)
// ---------------------------------------------------------------------------------------------

/**
 * The driver block on a history row (AL-36) — enough to render the post-trip Call action without
 * a second read.
 *
 * @property driverId The driver.
 * @property name Display name.
 * @property mobileMasked Role-masked MSISDN.
 * @property callTypesAvailable What the Call action may offer. **`normal_masked` was removed by
 *   AL-48 and can never appear here**; since then the only server-brokered type is `free_voip`,
 *   and `direct_dial` is a client-side `tel:` link.
 */
@Serializable
public data class RideHistoryDriver(
    val driverId: Ulid? = null,
    val name: String,
    val mobileMasked: PhoneMasked,
    val callTypesAvailable: List<CallType> = emptyList(),
)

/**
 * One row of `GET /v1/rides/history` (`ride.yaml#/components/schemas/RideHistoryRow`).
 *
 * Trip **detail** — polyline, fare breakdown — is query-svc's `GET /v1/trips/{userId}/{tripId}`.
 *
 * @property rideId The ride.
 * @property state Its terminal state.
 * @property pickup Where it started.
 * @property dropoff Where it ended.
 * @property fare What it cost.
 * @property completedAt When it finished.
 * @property driver Who drove it.
 */
@Serializable
public data class RideHistoryRow(
    val rideId: Ulid,
    val state: RideState,
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val fare: FareEstimate? = null,
    val completedAt: Timestamp,
    val driver: RideHistoryDriver? = null,
)

// ---------------------------------------------------------------------------------------------
// Location requests (P-02, P-12, P-13, §11.15)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/location-requests` (P-02/P-13).
 *
 * The phone number is looked up through iam-svc (P-03): a registered rider gets an FCM data
 * message and resolves in-app; an unregistered one gets an SMS carrying a `pickup_confirm` web
 * token and resolves through public-bff (AL-45). Rate limited per booker: 5/hour, 30/day (P-12).
 *
 * @property riderPhone Who to ask.
 * @property rideDraftId The in-progress booking this pickup point is for.
 */
@Serializable
public data class CreateLocationRequestRequest(val riderPhone: PhoneE164, val rideDraftId: Ulid? = null)

/**
 * `POST /v1/location-requests` — 202.
 *
 * @property requestId The created request; also the SignalR group suffix
 *   `booker:{bookerId}:loc-req:{requestId}`.
 * @property state Where the request starts.
 * @property ttl Seconds until expiry. The contract pins it to [TTL_SECONDS].
 */
@Serializable
public data class CreateLocationRequestResponse(val requestId: Ulid, val state: LocationRequestState, val ttl: Int) {
    public companion object {
        /** Five minutes — the durable expiry timer the contract declares `const: 300`. */
        public const val TTL_SECONDS: Int = 300
    }
}

/**
 * A proxy booking's location request (`ride.yaml#/components/schemas/LocationRequest`).
 *
 * Clients normally learn the outcome from the SignalR `LocationRequestResolved` event; the poll
 * exists for reconnect and support diagnosis.
 *
 * @property requestId The request.
 * @property state Where it is.
 * @property geo Present only when [state] is [LocationRequestState.Confirmed]. **Declining stores
 *   no coordinates at all** — a decline must not leak an approximate position.
 * @property expiresAt When the five-minute window closes.
 */
@Serializable
public data class LocationRequest(
    val requestId: Ulid,
    val state: LocationRequestState,
    val geo: GeoPointWithAccuracy? = null,
    val expiresAt: Timestamp,
)

// ---------------------------------------------------------------------------------------------
// Internal (mTLS)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/internal/rides/{rideId}/system-cancel` (R-15/R-16).
 *
 * @property reason Which system trigger fired.
 */
@Serializable
public data class SystemCancelRideRequest(val reason: SystemCancelReason)

/**
 * `POST /v1/internal/rides/{rideId}/payment-settled` (R-05).
 *
 * fare-svc reporting a terminal payment state. **Nothing else is allowed to move a ride into a
 * settled state.**
 *
 * @property paymentId The `fares.ride_payments` row.
 * @property paymentState Its terminal state.
 * @property settledMinor What was actually settled, in minor units.
 */
@Serializable
public data class NotifyPaymentSettledRequest(
    val paymentId: Ulid,
    val paymentState: PaymentState,
    val settledMinor: Long? = null,
)

/**
 * One entry of the ride's transition log (`rides.transitions`).
 *
 * @property from Where the ride was.
 * @property to Where it went.
 * @property at When the move happened.
 * @property actor Who or what caused it.
 */
@Serializable
public data class RideTransition(val from: RideState, val to: RideState, val at: Timestamp, val actor: String? = null)

/**
 * `GET /v1/internal/rides/{rideId}/saga-state` — ops diagnostics (ADD Appendix C).
 *
 * Exposes the aggregate's transition log and pending outbox rows so an operator can see why a
 * ride is stuck without querying Postgres directly.
 *
 * @property rideId The ride.
 * @property state Where it is.
 * @property version Current version.
 * @property transitions Every recorded move.
 * @property pendingOutbox Undispatched `rides.outbox` rows.
 */
@Serializable
public data class RideSagaState(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val transitions: List<RideTransition> = emptyList(),
    val pendingOutbox: Int? = null,
)
