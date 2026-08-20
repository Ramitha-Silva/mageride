package lk.mageride.driver.home

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.asPoint
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.domain.geo.bearingDegrees
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * One destination the driver can pick — a geocoder hit, a saved address, or ★ Home.
 *
 * @property label What the row reads. Driver-owned text or a place name, never platform copy.
 * @property point Where it is.
 * @property isHome Whether this is the ★ Home shortcut the wireframe pins to the top.
 */
internal data class DirectionalDestination(val label: String, val point: GeoPoint, val isHome: Boolean = false)

/**
 * SCR-DA-013's state.
 *
 * @property filter The server's own view of the activation (DT-08).
 * @property destination What the driver has chosen but not yet set.
 * @property suggestions Geocoder hits and saved addresses for the destination field.
 * @property maxDuration The configured ceiling **once this app has been told one**. See the class
 *   KDoc on [DirectionalViewModel] — it is not readable before the first activation.
 * @property query What is typed in the destination field.
 * @property tickAt The clock the `1:42 left` countdown renders against.
 * @property busy A set or a clear is in flight.
 * @property error Resolved copy for the last failure.
 */
@OptIn(ExperimentalTime::class)
internal data class DirectionalState(
    val filter: DirectionalFilterState? = null,
    val destination: DirectionalDestination? = null,
    val suggestions: List<DirectionalDestination> = emptyList(),
    val maxDuration: Duration? = null,
    val query: String = "",
    val tickAt: Timestamp? = null,
    val position: Fix? = null,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** Whether a filter is live — the wireframe's *"when active"* half. */
    val isActive: Boolean get() = filter?.active == true

    /** Activations left today, in Asia/Colombo (DT-03, D-38). */
    val usesRemaining: Int get() = filter?.usesRemaining ?: 0

    /**
     * Whether **Set Direction** is live.
     *
     * Three conditions the client can actually evaluate: a destination has been chosen, no filter
     * is already running, and the day's budget is not spent — the wireframe's *"uses exhausted →
     * Set disabled"*.
     *
     * **Being online is the fourth condition and is the server's to enforce.** `dispatch.yaml`
     * carries no presence read — `POST /v1/standby/online` answers a `PresenceState` and nothing
     * reads one back — so a screen reached by deep link or after a process death cannot know. The
     * honest behaviour is to let the driver tap and to render `403 not-online` as copy, rather than
     * to grey out a control on a guess. Recorded as a spec gap in the C070 handoff.
     */
    val canSet: Boolean get() = !isActive && usesRemaining > 0 && destination != null

    /** What is left of the activation, floored at zero (DT-04). */
    val timeRemaining: Duration
        get() {
            val deadline = filter?.expiresAt
                ?: return (filter?.timeRemainingSec ?: 0).seconds.coerceAtLeast(Duration.ZERO)
            val now = tickAt ?: return (filter.timeRemainingSec).seconds
            val left = deadline - now
            return if (left.isNegative()) Duration.ZERO else left
        }

    /**
     * Whether the ten-minute warning is due (DT-08, US-10.14).
     *
     * The same threshold notify-svc's `directional.expiring` push uses, so the banner and the
     * notification agree about when "expiring soon" starts.
     */
    val isExpiringSoon: Boolean
        get() = isActive && timeRemaining <= PRE_EXPIRY_REMINDER && timeRemaining > Duration.ZERO

    /** MAP-06's ➤ rotation — the bearing from the driver to where they are heading. */
    val headingDeg: Double?
        get() {
            val from = position?.asPoint() ?: return null
            val to = destination?.point ?: filter?.destination ?: return null
            return bearingDegrees(from, to)
        }

    private companion object {

        /** `DirectionalStanding.PRE_EXPIRY_REMINDER` — ten minutes (DT-08, US-10.14). */
        val PRE_EXPIRY_REMINDER: Duration = 10.minutes
    }
}

/**
 * **SCR-DA-013 · Directional Travel** (US-6A.17–23, DT-01..DT-08).
 *
 * **A use is spent on activation and turning the filter off does not give it back** (DT-03,
 * US-6A.19). `DELETE /v1/standby/directional` answers with the same `usesRemaining` it had before,
 * and this model shows exactly that number: without the rule a driver could flick the filter on for
 * the one offer they wanted and off again, all day, on two uses.
 *
 * **Directional Travel is not in the ride state machine** (ADD Appendix B.2 invariant 7). It is a
 * dispatch-svc candidate filter applied before an offer exists, which is why nothing here touches
 * ride-svc and why the only visible effect on SCR-DA-014 is a badge.
 *
 * ### The one number this screen cannot read
 *
 * The wireframe prints *"Uses left today: 1 of 2"* and *"Max duration: 2h"*. `dispatch.yaml`
 * carries `DirectionalConfig` on a **`PUT /v1/admin/dispatch/directional-config`** and on no read
 * at all, so an app can learn `maxDurationSec` only from a `DirectionalFilterCreated` it has just
 * received, and `maxUsesPerDay` never. Baking D5' §12.1's defaults would be the exact failure
 * `DirectionalPredicate`'s KDoc warns about — *"a build that hardcoded them would silently disagree
 * with dispatch the first time an operator tuned one"* — so this screen renders the remaining count
 * the server sent and shows the ceiling only once it has been told one. Recorded as a spec gap in
 * the C070 handoff.
 */
@OptIn(ExperimentalTime::class)
internal class DirectionalViewModel(
    private val standby: StandbyRepository,
    private val query: QueryApi,
    private val iam: IamApi,
    private val location: DriverLocationSource,
) : ViewModel() {

    private val mutableState = MutableStateFlow(DirectionalState())

    val state: StateFlow<DirectionalState> = mutableState.asStateFlow()

    init {
        refresh()
        observeDevice()
    }

    /**
     * Re-reads the filter and the ★ Home shortcut.
     *
     * Both reads are resolved **before** `update` is called. It is a compare-and-set loop whose
     * lambda kotlinx may evaluate *"multiple times, if value is being concurrently updated"*, and
     * [observeDevice] updates `tickAt` once a second — a suspending call inside it re-fires on
     * every tick the read outlives.
     */
    fun refresh() {
        launchGuarded {
            val filter = standby.directional()
            val shortcuts = homeShortcuts()
            mutableState.update { it.copy(filter = filter, suggestions = shortcuts) }
        }
    }

    /** The destination field. Geocoded through query-svc, biased toward where the driver is. */
    fun search(text: String) {
        mutableState.update { it.copy(query = text) }
        if (text.length < MIN_QUERY) return

        launchGuarded {
            val here = mutableState.value.position
            val hits = query.searchPlaces(query = text, lat = here?.lat, lng = here?.lng, limit = SEARCH_LIMIT)
            val places = hits.places.map { DirectionalDestination(label = it.displayName, point = it.point) }
            val shortcuts = homeShortcuts()
            mutableState.update { state -> state.copy(suggestions = shortcuts + places) }
        }
    }

    /** Picks a destination — a search hit, a saved address, or the map pin. */
    fun choose(destination: DirectionalDestination) {
        mutableState.update { it.copy(destination = destination, query = destination.label) }
    }

    /**
     * **Set Direction** — `POST /v1/standby/directional` (DT-01). Consumes one of the day's uses.
     *
     * `403 not-online` off standby and `409 directional-limit-reached` when the budget is gone;
     * both become copy rather than a thrown screen.
     */
    fun setDirection() {
        val destination = mutableState.value.destination ?: return
        mutableState.update { it.copy(busy = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(busy = false) } }) {
            val created = standby.setDirectional(
                destination = destination.point,
                // The driver's own shorthand, not platform copy — `SetDirectionalFilterRequest`
                // caps it at 60 characters and the server echoes it back on the card.
                label = destination.label.take(LABEL_MAX),
            )
            mutableState.update {
                it.copy(
                    busy = false,
                    maxDuration = created.maxDurationSec.seconds,
                    filter = DirectionalFilterState(
                        active = true,
                        destination = destination.point,
                        label = destination.label,
                        expiresAt = created.expiresAt,
                        timeRemainingSec = (created.expiresAt - Clock.System.now()).inWholeSeconds.toInt(),
                        usesRemaining = created.usesRemaining,
                    ),
                )
            }
        }
    }

    /** **Turn Off** — and the use is still spent (US-6A.19). */
    fun turnOff() {
        mutableState.update { it.copy(busy = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(busy = false) } }) {
            val cleared = standby.clearDirectional()
            mutableState.update {
                it.copy(
                    busy = false,
                    destination = null,
                    query = "",
                    filter = it.filter?.copy(
                        active = cleared.active,
                        destination = null,
                        expiresAt = null,
                        timeRemainingSec = 0,
                        usesRemaining = cleared.usesRemaining,
                    ),
                )
            }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /** ★ Home, from the driver's own saved addresses. Best-effort — the field still works without. */
    @Suppress("TooGenericExceptionCaught") // A missing shortcut is not a reason to fail the screen.
    private suspend fun homeShortcuts(): List<DirectionalDestination> = try {
        iam.listSavedAddresses().items
            .filter { it.isHome == true || it.isWork == true }
            .map {
                DirectionalDestination(
                    label = it.label,
                    point = GeoPoint(lat = it.lat, lng = it.lng),
                    isHome = it.isHome == true,
                )
            }
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        emptyList()
    }

    private fun observeDevice() {
        viewModelScope.launch {
            location.fixes.collect { fix -> mutableState.update { it.copy(position = fix) } }
        }
        viewModelScope.launch {
            while (isActive) {
                mutableState.update { it.copy(tickAt = Clock.System.now()) }
                delay(TICK)
            }
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the user raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    private companion object {
        val TICK: Duration = 1.seconds
        const val MIN_QUERY = 3
        const val SEARCH_LIMIT = 6

        /** `SetDirectionalFilterRequest.label` — *"at most 60 characters"*. */
        const val LABEL_MAX = 60
    }
}
