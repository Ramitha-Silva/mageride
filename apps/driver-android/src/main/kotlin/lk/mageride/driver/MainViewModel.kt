package lk.mageride.driver

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.dispatch.GoOnlineRequest
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.iam.VerifyOtpRequest
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/** Which of the throwaway screens is showing. */
internal enum class Screen { SignIn, Otp, Standby, Offer, OnRide }

/** Everything the shell renders. */
internal data class UiState(
    val screen: Screen = Screen.SignIn,
    val phone: String = "+94770000001",
    val otp: String = "",
    val authId: String? = null,
    val online: Boolean = false,
    val publishing: Boolean = false,
    val rideId: String? = null,
    val rideState: RideState? = null,
    val offerId: String = "",
    val secondsLeft: Int? = null,
    val busy: Boolean = false,
    val error: String? = null,
)

/**
 * The driver accept flow, walking-skeleton depth (D1' §A.3, SCR-DA-014).
 *
 * Sign in, go on standby, publish position, take an offer, drive it to `PaymentPending`. Every call
 * goes through `:shared`'s typed clients and `:shared`'s MQTT contract; there is no HTTP, no JSON
 * and no topic string in this class.
 *
 * **The offer id has to be typed in, and that is a real gap rather than a shortcut.** A driver
 * accepts with `POST /v1/rides/{rideId}/offer/{driverId}/accept`, whose body requires the
 * `offerId` — and **no REST response returns one**: `RideDetail` carries `offerExpiresAt` but not
 * the id, and so does `RideStateSnapshot`. In the finished platform it arrives on the
 * `offer.created` push (dispatch outbox → `dispatch.events` → notification-svc C051 as FCM, and →
 * fanout-svc C041 as a socket event), neither of which exists yet. `e2e/walking-skeleton` reads the
 * Kafka topic those two will read; an app cannot, so this shell asks for the value the push would
 * have carried. Recorded as contract gap (a) in the C025 handoff.
 */
@OptIn(ExperimentalTime::class)
internal class MainViewModel : ViewModel() {

    private val mutable = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = mutable.asStateFlow()

    private val api = SkeletonClient.api()
    private var mqtt: DriverMqtt? = null
    private var publisher: Job? = null
    private var watcher: Job? = null

    fun onPhoneChanged(value: String) = mutable.update { it.copy(phone = value, error = null) }

    fun onOtpChanged(value: String) = mutable.update { it.copy(otp = value, error = null) }

    fun onOfferIdChanged(value: String) = mutable.update { it.copy(offerId = value, error = null) }

    fun requestOtp() = run {
        val response = api.iam.requestOtp(
            RequestOtpRequest(phone = mutable.value.phone, deviceId = DEVICE_ID, role = AppSurface.DRIVER),
        )
        mutable.update { it.copy(screen = Screen.Otp, authId = response.authId) }
    }

    /**
     * Verifies the code.
     *
     * Opening the Driver App does not confer the driver role (C020 decision 4) — the seeded account
     * has it because `db/seed/skeleton.sql` granted it. A passenger who signs in here reaches
     * standby and is refused by `/v1/standby/online`, which is the correct behaviour to see.
     */
    fun verifyOtp() = run {
        val authId = mutable.value.authId ?: error("Ask for a code first.")
        val session = api.iam.verifyOtp(
            VerifyOtpRequest(authId = authId, otp = mutable.value.otp, deviceId = DEVICE_ID),
        )

        SkeletonClient.signedIn(session.user.userId, session.accessToken)
        mutable.update { it.copy(screen = Screen.Standby) }
    }

    /** `POST /v1/standby/online` — enter the Mode C candidate pool (US-6A.1). */
    fun goOnline() = run {
        val presence = api.dispatch.goOnline(
            GoOnlineRequest(vehicleId = SkeletonClient.vehicleId, position = COLOMBO_FORT),
        )
        mutable.update { it.copy(online = presence.state.name == "AVAILABLE") }
        watchForOffer()
    }

    /**
     * Starts publishing position over MQTT.
     *
     * The session JWT is typed in for the same reason the offer id is: `POST /v1/auth/mqtt-token`
     * (iam.yaml) is **not implemented** — C020 left it to C026 — so nothing can hand this app a
     * device credential yet. C076 calls that endpoint through `MqttSessionTokenManager`, which is
     * already in `:shared` waiting for it.
     */
    fun startPublishing(sessionJwt: String) = run {
        val client = DriverMqtt(SkeletonClient.mqttHost, SkeletonClient.mqttPort, SkeletonClient.vehicleId)
        client.connect(sessionJwt)
        mqtt = client

        mutable.update { it.copy(publishing = true) }

        publisher = viewModelScope.launch {
            while (isActive) {
                runCatching { client.publish(COLOMBO_FORT) }
                // A flat cadence. The real one is phase-aware (R-07, ADD §7.5.1) and lives in
                // `:shared`'s AdaptiveRateEngine, which C076 drives from the ride's state.
                delay(PUBLISH_INTERVAL_MS)
            }
        }
    }

    /**
     * Polls for a ride assigned to this driver and renders the 15 s countdown from
     * `offerExpiresAt`.
     *
     * A poll, because the offer push is C051/C041's. The deadline is **ride-svc's** instant, which
     * is what makes a countdown rendered from it agree with what the accept will allow (§11.11).
     */
    private fun watchForOffer() {
        watcher?.cancel()
        watcher = viewModelScope.launch {
            while (isActive) {
                runCatching {
                    val ride = api.ride.getActiveDriverRide(SkeletonClient.userId.orEmpty())

                    mutable.update { current ->
                        current.copy(
                            rideId = ride?.rideId ?: current.rideId,
                            rideState = ride?.state ?: current.rideState,
                            secondsLeft = ride?.offerExpiresAt?.let { deadline ->
                                ((deadline - Clock.System.now()).inWholeSeconds).toInt().coerceAtLeast(0)
                            },
                            screen = when {
                                ride == null -> current.screen
                                ride.state == RideState.Offered -> Screen.Offer
                                ride.state.isDriverAssigned -> Screen.OnRide
                                else -> current.screen
                            },
                        )
                    }
                }
                delay(POLL_INTERVAL_MS)
            }
        }
    }

    /** The atomic accept: exactly one driver wins (R-02, §11.11). */
    fun accept() = run {
        val rideId = mutable.value.rideId ?: error("No ride to accept.")
        val offerId = mutable.value.offerId.trim()
        require(offerId.isNotEmpty()) { "Paste the offerId from the offer.created push." }

        val snapshot = api.ride.getRideState(rideId)
        val accepted = api.ride.acceptRideOffer(
            rideId = rideId,
            driverId = SkeletonClient.userId.orEmpty(),
            request = AcceptRideOfferRequest(offerId = offerId, version = snapshot.version),
        )

        mutable.update { it.copy(screen = Screen.OnRide, rideState = accepted.state) }
    }

    fun arrive() = advance { rideId, version -> api.ride.markDriverArrived(rideId, VersionedCommand(version)).state }

    fun start() = advance { rideId, version ->
        api.ride.startRide(rideId, StartRideRequest(version = version)).state
    }

    fun complete() = advance { rideId, version ->
        api.ride.completeRide(rideId, VersionedCommand(version)).state
    }

    override fun onCleared() {
        publisher?.cancel()
        watcher?.cancel()
        mqtt?.disconnect()
        super.onCleared()
    }

    /** Re-reads the version, then applies one transition. Versions are never guessed (R-14). */
    private fun advance(transition: suspend (String, Int) -> RideState) = run {
        val rideId = mutable.value.rideId ?: error("No ride in progress.")
        val version = api.ride.getRideState(rideId).version
        val state = transition(rideId, version)
        mutable.update { it.copy(rideState = state) }
    }

    private fun run(block: suspend () -> Unit) {
        viewModelScope.launch {
            mutable.update { it.copy(busy = true, error = null) }
            runCatching { block() }
                .onFailure { failure -> mutable.update { it.copy(error = failure.message ?: failure.toString()) } }
            mutable.update { it.copy(busy = false) }
        }
    }

    private fun MutableStateFlow<UiState>.update(transform: (UiState) -> UiState) {
        value = transform(value)
    }

    private companion object {
        const val DEVICE_ID = "driver-skeleton-device"
        const val PUBLISH_INTERVAL_MS = 2_000L
        const val POLL_INTERVAL_MS = 1_000L

        val COLOMBO_FORT = GeoPoint(lat = 6.9344, lng = 79.8428)
    }
}
