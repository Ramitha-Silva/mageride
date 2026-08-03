package lk.mageride.driver.tracker

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.location.PositionPublisher
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.VehicleSummary

/**
 * SCR-DA-027's state.
 *
 * @property vehicles Every vehicle the driver owns or has been assigned — the selector's options.
 * @property selectedVehicleId Which one the form is about. `null` only while the read is in flight
 *   or when the driver has no vehicle at all.
 * @property imei The digits typed or scanned.
 * @property binding The tracker already bound to the selected vehicle, as this handset knows it.
 * @property loading The vehicle read is in flight.
 * @property pairing The bind is in flight — D2's *"Pairing → spinner"*.
 * @property quarantined The last attempt met `409 imei-duplicate` (US-3.4, T-08).
 * @property scanning The device-QR viewfinder is up.
 * @property error Resolved copy for the last failure.
 */
internal data class TrackerPairingState(
    val vehicles: List<VehicleSummary> = emptyList(),
    val selectedVehicleId: Ulid? = null,
    val imei: String = "",
    val binding: TrackerBinding? = null,
    val loading: Boolean = true,
    val pairing: Boolean = false,
    val quarantined: Boolean = false,
    val scanning: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** The vehicle the form is about. */
    val selected: VehicleSummary? get() = vehicles.firstOrNull { it.vehicleId == selectedVehicleId }

    /** Whether the selected vehicle already publishes through hardware. */
    val isPaired: Boolean get() = binding != null

    /** Whether what has been typed is fifteen digits. Blank is "not yet", not "wrong". */
    val imeiValid: Boolean get() = TrackerImei.isValid(imei)

    /** Whether the field should be drawn in `error` — typed something, and it is not an IMEI. */
    val imeiRejected: Boolean get() = imei.isNotBlank() && !imeiValid

    /** Whether **Pair device** is live. A vehicle that is already tracked cannot be paired again. */
    val canPair: Boolean get() = !pairing && !isPaired && imeiValid && selectedVehicleId != null
}

/**
 * **SCR-DA-027 · GPS tracker pairing** (US-3.1/3.2, US-3.21–3.23, T-02/T-09).
 *
 * Binds an IMEI to one of the driver's vehicles through registry-svc's owner-facing wrapper, and —
 * the part that matters beyond this screen — **stops this handset publishing GPS for that vehicle
 * the moment the bind is accepted**. The gate itself lives in [TrackerPositionPublisher], which
 * every door onto the position service goes through; what happens here is the other half of it: a
 * driver who is online *right now* on the vehicle they have just paired has a publisher running,
 * and a gate that only refuses the next start would leave it running until they went offline.
 *
 * **The phone is stopped, the platform is not told.** `POST /v1/trackers/{imei}/switch-source` is
 * the route that would say so and it is provisioning-svc's, with no client in `:shared` — see
 * [TrackerRepository]. Stopping is still the right unilateral move: a phone that keeps publishing
 * beside a bound device interleaves two clocks on one topic (US-3.6), and of the two publishers the
 * device is the one the platform has just issued a credential to.
 */
internal class TrackerPairingViewModel(
    private val identity: DriverIdentity,
    private val trackers: TrackerRepository,
    private val publisher: PositionPublisher,
) : ViewModel() {

    private val mutableState = MutableStateFlow(TrackerPairingState())

    val state: StateFlow<TrackerPairingState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads the driver's vehicles and the binding on whichever is selected. */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val vehicles = identity.liveVehicle()
            // The live vehicle is the sensible default — it is the one this handset publishes for,
            // so it is the one a driver holding a new tracker is standing next to.
            val selected = mutableState.value.selectedVehicleId
                ?.takeIf { held -> vehicles.vehicles.any { it.vehicleId == held } }
                ?: vehicles.live?.vehicleId
                ?: vehicles.vehicles.firstOrNull()?.vehicleId

            mutableState.update {
                it.copy(
                    loading = false,
                    vehicles = vehicles.vehicles,
                    selectedVehicleId = selected,
                    binding = selected?.let(trackers::bindingFor),
                )
            }
        }
    }

    /** The wireframe's `Three-wheeler · ABC-1234 ▾` selector. Re-scopes the whole form. */
    fun selectVehicle(vehicleId: Ulid) {
        mutableState.update {
            it.copy(
                selectedVehicleId = vehicleId,
                binding = trackers.bindingFor(vehicleId),
                // The IMEI belongs to the vehicle it was typed for. Carrying it across the selector
                // is how a tracker gets bound to the wrong one.
                imei = "",
                quarantined = false,
                error = null,
            )
        }
    }

    /** The IMEI field. */
    fun onImeiChange(raw: String) {
        mutableState.update { it.copy(imei = TrackerImei.digits(raw), quarantined = false, error = null) }
    }

    /** **▣ Scan device QR** — raises the viewfinder. */
    fun startScan() {
        mutableState.update { it.copy(scanning = true, quarantined = false, error = null) }
    }

    /** The viewfinder was dismissed without a read. */
    fun cancelScan() {
        mutableState.update { it.copy(scanning = false) }
    }

    /**
     * A QR code was decoded.
     *
     * The payload is searched for an IMEI rather than parsed, because no spec says what a tracker
     * vendor prints in it — see [TrackerImei.imeiIn]. A payload with no single candidate leaves the
     * field alone and says so, which puts the driver back on the path that always works: typing.
     */
    fun onScanned(payload: String) {
        val imei = TrackerImei.imeiIn(payload)
        mutableState.update {
            if (imei == null) {
                it.copy(scanning = false, error = R.string.tracker_scan_unreadable)
            } else {
                it.copy(scanning = false, imei = imei, quarantined = false, error = null)
            }
        }
    }

    /**
     * **Pair device** — `POST /v1/vehicles/{vehicleId}/device`.
     *
     * On success the phone stops publishing for that vehicle. [PositionPublisher.stop] is
     * unconditional rather than conditional on this being the live vehicle: the service publishes
     * for one vehicle at a time and stopping one that was never started is a no-op, so the cheap
     * call is also the one that cannot leave a stream running.
     */
    fun pair() {
        val current = mutableState.value
        if (!current.canPair) return
        val vehicleId = current.selectedVehicleId ?: return

        mutableState.update { it.copy(pairing = true, quarantined = false, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(pairing = false) } }) {
            val binding = trackers.pair(vehicleId = vehicleId, imei = current.imei)
            publisher.stop()
            mutableState.update { it.copy(pairing = false, binding = binding, imei = "") }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null, quarantined = false) }
    }

    /**
     * Whether [cause] is T-08's anti-clone quarantine rather than an ordinary failure.
     *
     * `409 imei-duplicate` is lifted out of the generic path because it is not a failure to retry:
     * the serial is already active elsewhere on the platform, and the screen says so rather than
     * offering the same button again. A function rather than a test inside the `catch` because what
     * is being asked is about the problem **code** — a property of the answer, not of the exception
     * hierarchy — which is also why detekt refuses a type check there.
     */
    private fun isQuarantine(cause: Throwable): Boolean =
        (cause as? MageRideError.Api)?.code == ErrorCode.IMEI_DUPLICATE

    /** Runs [block] on the view-model scope, turning any failure into trilingual copy (D-26). */
    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update {
                    it.copy(quarantined = isQuarantine(cause), error = OnboardingErrors.messageFor(cause))
                }
            }
        }
    }
}
