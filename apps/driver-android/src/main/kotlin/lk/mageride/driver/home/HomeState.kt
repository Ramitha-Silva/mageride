package lk.mageride.driver.home

import androidx.annotation.StringRes
import lk.mageride.driver.location.Fix
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.wallet.WalletAlert
import kotlin.time.Duration
import kotlin.time.ExperimentalTime

/**
 * **SCR-DA-010 and SCR-DA-011 are one state, because they are one screen.**
 *
 * D2' re-tagged SCR-DA-012 `[MERGED → SCR-DA-010]` and made SCR-DA-011 *"the driver's home
 * dashboard whenever the active vehicle is Mode A or Mode B"*. Home is therefore not two
 * destinations with a chooser in front of them — it is one destination whose sheet is decided by
 * [LiveVehicle.isScheduledMode], and modelling it as two states would need a rule to keep them in
 * step about the map, the header, the vehicle and the offer overlay, all of which they share.
 *
 * @property loading The first read is in flight — the wireframe's shimmer stats.
 * @property busy A toggle or a journey command is in flight; the control stays put and spins.
 * @property vehicles What the driver has, and which one is live (US-9.6, D-03).
 * @property standing The status header and the standby sheet (SCR-DA-010).
 * @property journey The Mode A/B tracking session (SCR-DA-011).
 * @property online Whether `POST /v1/standby/online` has been accepted for the live vehicle.
 * @property position The handset's own last fix — the **only** vehicle the home map draws (AL-31).
 * @property journeyDistanceM Metres accumulated on this device since the journey started (US-5.6).
 * @property tickAt The clock the live timers are rendered against, advanced once a second.
 * @property autoEndAtDestination Whether the next journey arms the 100 m destination geofence
 *   (US-5.4).
 * @property activeRideId A ride already in hand, restored on a cold start (SCR-DA-001's router).
 * @property error Resolved copy for the last failure.
 */
@OptIn(ExperimentalTime::class)
internal data class HomeState(
    val loading: Boolean = true,
    val busy: Boolean = false,
    val vehicles: LiveVehicle = LiveVehicle(),
    val standing: DriverStanding = DriverStanding(),
    val journey: JourneyStanding = JourneyStanding(),
    val online: Boolean = false,
    val position: Fix? = null,
    val journeyDistanceM: Double = 0.0,
    val tickAt: Timestamp? = null,
    val autoEndAtDestination: Boolean = true,
    val activeRideId: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {

    /** Whether Home is SCR-DA-011's Start/End Journey dashboard rather than SCR-DA-010's map. */
    val isScheduledMode: Boolean get() = vehicles.isScheduledMode

    /**
     * **US-9.6.** Whether the go-online toggle is live at all.
     *
     * `false` is the wireframe's *"Add or get assigned a vehicle to go online"* → SCR-DA-026a. A
     * read still in flight is not the same as no vehicle, so the gate stays shut while loading and
     * the copy under it does not appear until the answer is in.
     */
    val canGoOnline: Boolean get() = vehicles.canGoOnline

    /** Whether the empty state routes to SCR-DA-026a rather than to My Vehicles' list. */
    val needsVehicle: Boolean get() = !loading && !vehicles.canGoOnline

    /** US-9.9's `< Rs 200` nudge on the standby sheet. */
    val walletAlert: WalletAlert get() = standing.walletAlert

    /**
     * How long the Mode A/B journey has been running (US-5.6).
     *
     * Measured from the session's own `startedAt` against a ticking clock, not from when this
     * screen was opened: a driver who backgrounds the app for an hour comes back to an hour on the
     * timer, which is what the fleet's record will say too.
     */
    val journeyElapsed: Duration
        get() {
            val startedAt = journey.session?.startedAt ?: return Duration.ZERO
            val now = tickAt ?: return Duration.ZERO
            val elapsed = now - startedAt
            return if (elapsed.isNegative()) Duration.ZERO else elapsed
        }
}
