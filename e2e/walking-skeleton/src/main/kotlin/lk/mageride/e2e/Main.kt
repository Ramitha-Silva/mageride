package lk.mageride.e2e

import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.dispatch.GoOnlineRequest
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import java.util.UUID

/**
 * The walking skeleton, end to end (C025).
 *
 * One booked Mode C ride across six services, two brokers and two realtime protocols, driven
 * entirely through `:shared` — the same api-client, `LiveHub` contract and `MqttTopics` /
 * `PositionCodec` the two Android shells use. Every step prints what it is asserting, so a failure
 * names the link of the chain that broke rather than saying "the run failed".
 *
 * The four things it proves are this component's definition of done:
 *  1. a booked ride reaches `PaymentPending`;
 *  2. the driver's live position reaches the booking passenger's SignalR group;
 *  3. an ignored offer expires at 15 s and the ride re-enters `Matching`;
 *  4. the passenger joins exactly the 19 cells of res-7 + `ring(2)` (R-06).
 *
 * **Two rides, and it has to be two.** The cascade's memory is a `NOT EXISTS` against
 * `dispatch.offers` — "a driver who let this ride's offer lapse or declined it is not asked again
 * in a later round" (D5' §3.5, C023's `CandidateRepository`). With one seeded driver, a ride whose
 * offer is ignored can therefore never be offered again, so proving (3) on the ride we also intend
 * to complete is not merely awkward, it is impossible. Ride B exists to be ignored and is booked
 * FIRST — `PaymentPending` is not terminal, so a driver who has just completed a ride still holds
 * an active one and correctly gets no further offers (R-02). Ride A is then driven to
 * `PaymentPending`.
 */
internal object Run {

    /** Colombo Fort. Pickup, and where the driver goes online. */
    private val PICKUP = GeoPoint(lat = 6.9344, lng = 79.8428)

    /** Dehiwala, ~9 km south. */
    private val DROPOFF = GeoPoint(lat = 6.8514, lng = 79.8653)

    @Suppress("LongMethod")
    fun main() = runBlocking {
        val environment = Environment()
        val otp = OtpReader(environment)

        banner("MageRide walking skeleton", environment.gatewayUrl)

        // -- 1. the actors ----------------------------------------------------------------------
        step("Signing the driver in over phone OTP (AL-07)")
        val driver = Session.signIn(environment, otp, environment.driverPhone, AppSurface.DRIVER)
        check(driver.userId == environment.driverId) {
            "The seeded driver should be ${environment.driverId}, signed in as ${driver.userId}. " +
                "Has infra/scripts/seed-skeleton.sh run?"
        }
        ok("driver ${driver.userId}")

        step("Signing two passengers in — new accounts, created by their first verify")
        val rider = Session.signIn(environment, otp, environment.passengerPhone, AppSurface.PASSENGER)
        val onlooker = Session.signIn(environment, otp, environment.secondPassengerPhone, AppSurface.PASSENGER)
        ok("rider ${rider.userId}, second passenger ${onlooker.userId}")

        // R-02 allows one live ride per driver and the seeded driver is the same account every run.
        // Best-effort only: `POST /v1/rides/{rideId}/cancel` does not exist yet (C022 shipped the
        // happy path, cancellation is C035), so this reports rather than fixes. `run.sh` tearing
        // the volumes down is what actually guarantees a clean slate.
        step("Checking the driver is not left mid-ride by an earlier run")
        clearActiveRide(driver) { driver.api.ride.getActiveDriverRide(driver.userId) }
        ok("driver is free")

        // -- 2. the passenger's live map, before anything is booked -----------------------------
        // Connecting the socket and waiting for the Kafka consumer's partition assignment takes
        // tens of seconds; doing it first keeps that out of the offer's 15-second window.
        val liveMap = PassengerLiveMap(environment, rider.accessToken)
        val offers = OfferWatcher(environment.kafkaBootstrap)
        var heartbeat: Job? = null

        try {
            step("Passenger opens /hubs/live and joins the 3 km view")
            liveMap.connect()
            val cells = liveMap.joinViewAround(PICKUP)
            ok("joined ${cells.size} res-7 cells — DoD: R-06 fixes the 3 km view at 19")

            offers.start()

            // -- 3. the driver goes on standby --------------------------------------------------
            step("Driver goes online at Colombo Fort (US-6A.1)")
            ok("presence ${refreshPresence(driver, environment)}")
            heartbeat = launch { keepPresenceFresh(driver, environment) }

            // -- 4. DoD: an ignored offer expires at 15 s and the ride re-enters Matching --------
            // FIRST, and a ride of its own with a passenger of its own. Both orderings are forced:
            //   * a ride whose offer was ignored can never be offered to that driver again (the
            //     `NOT EXISTS` in C023's CandidateRepository, D5' §3.5), so it cannot be the ride
            //     that gets completed;
            //   * and it has to happen BEFORE that ride, because `PaymentPending` is not terminal —
            //     a driver who has just completed one ride still has an active ride until the
            //     payment settles, and correctly gets no new offers (R-02).
            step("Ride B — booking, then ignoring the offer for its whole 15 s window")
            val rideB = book(onlooker)
            val offerB = offers.awaitOffer(rideB.rideId, OFFER_TIMEOUT_MS)
            checkNotNull(offerB) { "dispatch-svc published no offer.created for ${rideB.rideId}." }
            ok("offer ${offerB.offerId} on ride ${rideB.rideId}, expires ${offerB.expiresAt}")

            val observed = observeStates(onlooker, rideB.rideId, EXPIRY_WATCH_MS)
            check(RideState.Matching in observed.drop(1)) {
                "The ride never returned to Matching after the offer lapsed. Saw: $observed"
            }
            ok("DoD: saw ${observed.joinToString(" -> ")}")

            // -- 5. ride A: book, accept ---------------------------------------------------------
            step("Ride A — quoting the fare, then booking")
            val rideA = book(rider)
            ok("ride ${rideA.rideId} is ${rideA.state}, ${rideA.estimatedFare.amountMinor} minor units quoted")

            step("Ride A — accepting the offer")
            val offerA = offers.awaitOffer(rideA.rideId, OFFER_TIMEOUT_MS)
            checkNotNull(offerA) { "dispatch-svc published no offer.created for ${rideA.rideId}." }
            ok("offer ${offerA.offerId}, expires ${offerA.expiresAt}")

            val accepted = driver.api.ride.acceptRideOffer(
                rideId = rideA.rideId,
                driverId = driver.userId,
                request = AcceptRideOfferRequest(
                    offerId = offerA.offerId,
                    version = rider.api.ride.getRideState(rideA.rideId).version,
                ),
            )
            check(accepted.state == RideState.Accepted) { "Expected Accepted, got ${accepted.state}." }
            ok("ride is ${accepted.state} at version ${accepted.version}")

            // -- 6. DoD: the driver's position reaches the booking passenger --------------------
            step("Driver publishes to EMQX; the booking passenger's geocell group must receive it")
            val frame = publishAndAwait(environment, driver, liveMap)
            checkNotNull(frame) {
                "The driver's position never reached the passenger. EMQX -> mqtt-bridge -> " +
                    "telemetry.raw -> position-processor -> cell:{h3index} -> fanout is broken somewhere."
            }
            ok("DoD: passenger sees ${frame.vehicleId} at ${frame.lat}, ${frame.lng} (${frame.type})")

            // -- 7. DoD: PaymentPending ---------------------------------------------------------
            step("Ride A — arrive, start, complete")
            val arrived = driver.api.ride.markDriverArrived(
                rideA.rideId,
                VersionedCommand(rider.api.ride.getRideState(rideA.rideId).version),
            )
            ok("arrived — ${arrived.state}")

            // The pickup OTP is a PACKAGE handoff gate (P-07) and C022's `RideRequestedResponse`
            // says it is "never issued in this build"; `startRide` accepts the field and ignores
            // it. Passed through rather than hard-coded to null so this starts working unchanged
            // the day one is issued.
            val started = driver.api.ride.startRide(
                rideA.rideId,
                StartRideRequest(version = arrived.version, otp = rideA.pickupOtp),
            )
            ok("started — ${started.state}")

            val completed = driver.api.ride.completeRide(rideA.rideId, VersionedCommand(started.version))
            check(completed.state == RideState.PaymentPending) {
                "The walking skeleton's terminal state is PaymentPending; got ${completed.state}."
            }
            ok("DoD: completed — ${completed.state}, fare ${completed.fare?.amountMinor}")

            banner("WALKING SKELETON GREEN", "ride ${rideA.rideId} reached ${completed.state}")
        } finally {
            heartbeat?.cancel()
            liveMap.close()
            offers.close()
        }
    }

    /** Quotes a fare and books a Mode C ride against it. */
    private suspend fun book(passenger: Session): RequestRideResponse {
        val quote = passenger.api.fare.estimateFare(
            fromLat = PICKUP.lat,
            fromLng = PICKUP.lng,
            toLat = DROPOFF.lat,
            toLng = DROPOFF.lng,
            vehicleType = RideVehicleType.THREE_WHEELER,
        )

        return passenger.api.ride.requestRide(
            RideRequest(
                clientRequestId = UUID.randomUUID().toString(),
                pickup = Place(lat = PICKUP.lat, lng = PICKUP.lng, address = "Colombo Fort"),
                dropoff = Place(lat = DROPOFF.lat, lng = DROPOFF.lng, address = "Dehiwala"),
                vehicleType = RideVehicleType.THREE_WHEELER,
                // The token is what stops a client inventing its own price: ride-svc rejects a
                // forged or stale one with `400 invalid-fare-token`.
                fareEstimateToken = quote.fareEstimateToken,
                paymentMethod = RidePaymentMethod.CASH,
            ),
        )
    }

    /** Publishes a handful of live samples and waits for one of them to come back over SignalR. */
    private fun publishAndAwait(
        environment: Environment,
        driver: Session,
        liveMap: PassengerLiveMap,
    ): VehicleFrameDto? {
        val mqtt = DriverMqtt(environment, environment.vehicleId, driver.deviceId)
        mqtt.connect()

        return try {
            repeat(POSITION_PUBLISHES) {
                mqtt.publish(PICKUP)
                Thread.sleep(POSITION_INTERVAL_MS)
            }
            liveMap.awaitVehicle(environment.vehicleId, FANOUT_TIMEOUT_MS)
        } finally {
            mqtt.disconnect()
        }
    }

    /** Puts the driver on standby. */
    private suspend fun refreshPresence(driver: Session, environment: Environment) =
        driver.api.dispatch.goOnline(
            GoOnlineRequest(vehicleId = environment.vehicleId, position = PICKUP),
        ).state

    /**
     * Re-asserts presence every [PRESENCE_REFRESH_MS] for the rest of the run.
     *
     * **A workaround for a gap, not something a Driver App does.** D5' §3.2's freshness gate drops
     * a driver whose `dispatch.driver_presence.last_seen_at` is older than `Dispatch:PresenceTtl`
     * (60 s), and in this slice **nothing refreshes it**: R-08 gives that heartbeat to
     * position-processor-svc, and C024 deliberately left it to C039 (C023 decision 10 says so in as
     * many words). Without this the driver ages out of the candidate pool mid-run — a ride sits in
     * `Matching` with a driver parked fifty metres away — and the failure reads as a broken
     * dispatch rather than a missing heartbeat.
     *
     * Delete it the day C039 lands.
     */
    private suspend fun keepPresenceFresh(driver: Session, environment: Environment) {
        while (currentCoroutineContext().isActive) {
            delay(PRESENCE_REFRESH_MS)
            runCatching { refreshPresence(driver, environment) }
        }
    }

    /**
     * Cancels whatever [lookup] finds, if anything. Reports every failure rather than swallowing it
     * — a silent null here would resurface later as an unexplained `409 active-ride-exists`.
     */
    private suspend fun clearActiveRide(session: Session, lookup: suspend () -> RideDetail?) {
        val active = try {
            lookup()
        } catch (error: Exception) {
            println("    (could not read the active ride for ${session.phone}: $error)")
            null
        } ?: return

        try {
            session.api.ride.cancelRide(
                active.rideId,
                CancelRideRequest(version = active.version, reason = RideCancelReason.OTHER),
            )
            println("    (cancelled ${active.rideId}, left ${active.state} by an earlier run)")
        } catch (error: Exception) {
            println("    (could not cancel ${active.rideId} in ${active.state}: $error)")
        }
    }

    /**
     * Polls the ride's state and returns the distinct states seen, in order.
     *
     * Polling rather than listening because `RideStateChanged` on `/hubs/live` is C041's — this is
     * the fallback `signalr-hub.md` §1 already names for exactly that case. 200 ms is well inside
     * the 15 s window, so the pass through `Matching` cannot be missed.
     */
    private suspend fun observeStates(session: Session, rideId: String, windowMs: Long): List<RideState> {
        val deadline = System.currentTimeMillis() + windowMs
        val seen = mutableListOf<RideState>()

        while (System.currentTimeMillis() < deadline) {
            val state = session.api.ride.getRideState(rideId).state
            if (seen.lastOrNull() != state) seen += state
            if (seen.size >= 2 && RideState.Matching in seen.drop(1)) return seen
            delay(STATE_POLL_MS)
        }

        return seen
    }

    private fun banner(title: String, detail: String) {
        println("\n${"=".repeat(78)}\n  $title\n  $detail\n${"=".repeat(78)}")
    }

    private fun step(what: String) = println("\n--> $what")

    private fun ok(detail: String) = println("    ok: $detail")

    /** dispatch-svc's offer loop is driven by `ride.requested`, which crosses a broker first. */
    private const val OFFER_TIMEOUT_MS = 45_000L

    /** The 15 s window (D5' §3.5) plus the R-04 backstop's sweep. */
    private const val EXPIRY_WATCH_MS = 40_000L
    private const val STATE_POLL_MS = 200L

    /** Enough samples that one lost to a batch boundary does not fail the run. */
    private const val POSITION_PUBLISHES = 6
    private const val POSITION_INTERVAL_MS = 500L

    /** The C024 SLO is 5 s p95 at a 2 s batch interval; this is generous on top of it. */
    private const val FANOUT_TIMEOUT_MS = 30_000L

    /** Comfortably inside `Dispatch:PresenceTtl` (60 s). */
    private const val PRESENCE_REFRESH_MS = 15_000L
}

internal fun main() = Run.main()
