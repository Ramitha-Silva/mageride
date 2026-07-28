package lk.mageride.passenger

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.iam.VerifyOtpRequest
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.domain.ride.RideProjection
import lk.mageride.shared.domain.ride.RideSnapshot
import java.util.UUID

/** Which of the five throwaway screens is showing. */
internal enum class Screen { SignIn, Otp, Map, Booking, InRide }

/** Everything the shell renders. One immutable snapshot, so a recomposition cannot half-apply. */
internal data class UiState(
    val screen: Screen = Screen.SignIn,
    val phone: String = "+9477",
    val otp: String = "",
    val authId: String? = null,
    val rideId: String? = null,
    val rideState: RideState? = null,
    val cells: List<String> = emptyList(),
    val vehicles: List<VehicleMarker> = emptyList(),
    val busy: Boolean = false,
    val error: String? = null,
)

/** One vehicle on the "map". A row in a list here; C077 makes it a marker on MapLibre. */
internal data class VehicleMarker(val vehicleId: String, val lat: Double, val lng: Double, val type: String?)

/**
 * The passenger book flow, walking-skeleton depth (D1' §A.3, SCR-PA-009).
 *
 * Sign in with a phone OTP, open the live map, book a ride, watch it move. Every call goes through
 * `:shared`'s typed clients — this class contains no HTTP, no JSON and no URL, which is the point
 * of the exercise: if the module is right, an app is this thin.
 *
 * **The ride state comes from the server, always.** [RideProjection] is C015's machine and it moves
 * only through `onServerState` — ride-svc is the sole writer (R-01), and a client that advanced its
 * own copy would show a passenger a ride that had already ended.
 */
internal class MainViewModel : ViewModel() {

    private val mutable = MutableStateFlow(UiState())
    val state: StateFlow<UiState> = mutable.asStateFlow()

    private val api = SkeletonClient.api()
    private var liveMap: PassengerLiveMap? = null
    private var projection: RideProjection? = null

    fun onPhoneChanged(value: String) = mutable.update { it.copy(phone = value, error = null) }

    fun onOtpChanged(value: String) = mutable.update { it.copy(otp = value, error = null) }

    /** `POST /v1/auth/otp/request`. The code arrives by SMS — or, in dev, in iam-svc's log. */
    fun requestOtp() = run {
        val response = api.iam.requestOtp(
            RequestOtpRequest(phone = mutable.value.phone, deviceId = DEVICE_ID, role = AppSurface.PASSENGER),
        )
        mutable.update { it.copy(screen = Screen.Otp, authId = response.authId) }
    }

    /** `POST /v1/auth/otp/verify`. iam-svc creates the account on the first success. */
    fun verifyOtp() = run {
        val authId = mutable.value.authId ?: error("Ask for a code first.")
        val session = api.iam.verifyOtp(
            VerifyOtpRequest(authId = authId, otp = mutable.value.otp, deviceId = DEVICE_ID),
        )

        SkeletonClient.signedIn(session.user.userId, session.accessToken)
        mutable.update { it.copy(screen = Screen.Map) }
        openLiveMap()
    }

    /**
     * Joins the 19 res-7 cells of the 3 km view and renders whatever arrives.
     *
     * The cells come from `:shared`'s `GeoCells` over the platform H3 grid, so they are the ids
     * position-processor-svc writes its streams under. Computing them any other way would join
     * groups nothing publishes to — an empty map, and no error anywhere.
     */
    private fun openLiveMap() {
        val token = SkeletonClient.accessToken ?: return

        liveMap = PassengerLiveMap(SkeletonClient.baseUrl, token) { frames ->
            mutable.update { current -> current.copy(vehicles = frames) }
        }

        viewModelScope.launch {
            runCatching {
                val joined = liveMap?.connectAround(COLOMBO_FORT).orEmpty()
                mutable.update { it.copy(cells = joined) }
            }.onFailure { failure -> mutable.update { it.copy(error = failure.message) } }
        }
    }

    /** Quote, then book. The token is what stops a client naming its own price. */
    fun book() = run {
        val quote = api.fare.estimateFare(
            fromLat = COLOMBO_FORT.lat,
            fromLng = COLOMBO_FORT.lng,
            toLat = DEHIWALA.lat,
            toLng = DEHIWALA.lng,
            vehicleType = RideVehicleType.THREE_WHEELER,
        )

        val booking = api.ride.requestRide(
            RideRequest(
                clientRequestId = UUID.randomUUID().toString(),
                pickup = Place(lat = COLOMBO_FORT.lat, lng = COLOMBO_FORT.lng, address = "Colombo Fort"),
                dropoff = Place(lat = DEHIWALA.lat, lng = DEHIWALA.lng, address = "Dehiwala"),
                vehicleType = RideVehicleType.THREE_WHEELER,
                fareEstimateToken = quote.fareEstimateToken,
                paymentMethod = RidePaymentMethod.CASH,
            ),
        )

        projection = RideProjection(
            RideSnapshot(
                rideId = booking.rideId,
                kind = RideKind.PASSENGER,
                state = booking.state,
                version = booking.version,
            ),
        )
        mutable.update {
            it.copy(screen = Screen.Booking, rideId = booking.rideId, rideState = booking.state)
        }
    }

    /**
     * Re-reads the ride's state.
     *
     * `RideStateChanged` on `/hubs/live` is C041's, so this is the fallback `signalr-hub.md` §1
     * already names — a button rather than a poll loop, because a shell should not hide how often
     * it is asking.
     */
    fun refreshRide() = run {
        val rideId = mutable.value.rideId ?: return@run
        val snapshot = api.ride.getRideState(rideId)

        projection?.onServerState(snapshot.state, snapshot.version)
        mutable.update {
            it.copy(
                rideState = snapshot.state,
                screen = if (snapshot.state.isDriverAssigned) Screen.InRide else it.screen,
            )
        }
    }

    override fun onCleared() {
        liveMap?.close()
        super.onCleared()
    }

    /** Runs [block], showing a spinner and surfacing whatever it throws. */
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
        /** AL-08 binds a session to a device; a shell has one. C077 uses the real installation id. */
        const val DEVICE_ID = "passenger-skeleton-device"

        val COLOMBO_FORT = GeoPoint(lat = 6.9344, lng = 79.8428)
        val DEHIWALA = GeoPoint(lat = 6.8514, lng = 79.8653)
    }
}
