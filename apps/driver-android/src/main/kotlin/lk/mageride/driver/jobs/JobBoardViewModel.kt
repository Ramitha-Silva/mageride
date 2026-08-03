package lk.mageride.driver.jobs

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.domain.dispatch.DriverStanding
import lk.mageride.shared.domain.dispatch.JobBoard
import lk.mageride.shared.domain.dispatch.JobBoardRejection
import lk.mageride.shared.domain.dispatch.JobBoardVerdict
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * One row of SCR-DA-017.
 *
 * @property ride The board entry.
 * @property verdict Whether **Post intent** is live, and why not when it is not.
 * @property expired The T-30 window has passed. The card fades and then leaves the list; see
 *   [JobBoardViewModel.EXPIRY_FADE].
 * @property posting This row's intent call is in flight.
 */
@OptIn(ExperimentalTime::class)
internal data class JobBoardRow(
    val ride: ScheduledRide,
    val verdict: JobBoardVerdict,
    val expired: Boolean,
    val posting: Boolean = false,
) {

    /** The `dispatch.scheduled_rides` id — the row's identity and what the intent call takes. */
    val id: Ulid get() = ride.scheduledRideId

    /** Whether the card shows the *"Intent posted ✓"* pill rather than the **Post intent** link. */
    val posted: Boolean
        get() = (verdict as? JobBoardVerdict.Rejected)?.reason == JobBoardRejection.ALREADY_POSTED

    /** Whether the tap is live. */
    val canPost: Boolean get() = verdict.isAllowed && !posting
}

/**
 * SCR-DA-017's state.
 *
 * @property loading The first pass is in flight — the wireframe's shimmer.
 * @property gated **US-6A.8.** Level 1 has no Job Board; the screen shows *"Reach Level 2"* and no
 *   list. `null` until the level read answers, so the shimmer holds rather than the gate flashing.
 * @property minimumLevel The level the board opens at, from the server's own config when it sent
 *   one (US-14.12).
 * @property rows The board, soonest pickup first.
 * @property error Resolved copy for the last failure.
 */
internal data class JobBoardState(
    val loading: Boolean = true,
    val gated: Boolean? = null,
    val minimumLevel: Int = 0,
    val rows: List<JobBoardRow> = emptyList(),
    @param:StringRes val error: Int? = null,
) {

    /**
     * The board could not be read **at all**, because the level read did not answer.
     *
     * Distinct from both the gate and the empty board on purpose: the gate is a fact about the
     * driver and the empty board is a fact about the city, and neither is true when reputation is
     * down. Showing *"Reach Level 2"* to a Level-3 driver whose level read timed out would be the
     * one failure US-6A.8 must never produce.
     */
    val isUnavailable: Boolean get() = !loading && gated == null

    /** The wireframe's *"No jobs within 30 km"* — an answered, ungated, empty board, nothing wrong. */
    val isEmpty: Boolean get() = !loading && gated == false && rows.isEmpty() && error == null
}

/**
 * **SCR-DA-017 · the Job Board** (US-6A.5, US-6A.8, D-06, D5' §3.7).
 *
 * ### The board is post-intent only
 *
 * There is no accept here and there is no route to make one with: dispatch-svc's board group has
 * `GET /job-board` and `POST /job-board/{id}/intent` and nothing else. At **T-30 min** the booking
 * is materialised into a ride and offered to the closest intent-poster, breaking ties on the higher
 * Level, and that offer is accepted on SCR-DA-014 through ride-svc like every other one. An accept
 * on the board would reserve a driver half an hour early and would have to be unwound for anyone
 * who was mid-ride when the window came.
 *
 * ### What "ranked by Driver Level" is, and is not
 *
 * D5' §3.7 ranks **drivers** at T-30 — *"the closest intent-submitting driver by Level"* — not the
 * rows on one driver's board. A device knows neither the other bidders nor their levels, which is
 * why `JobBoard` deliberately carries no ranking function. This list is ordered by **pickup time,
 * soonest first**, which is the order the wireframe prints and the order the upcoming list already
 * comes back in.
 *
 * ### Intent posted, and where that fact lives
 *
 * `ScheduledRide` carries `intentCount` — how many drivers have bid — and nothing that says whether
 * **this** driver is one of them, so the *"Intent posted ✓"* pill is backed by a set held here. It
 * does not survive the process, and a repeat tap after a restart is a harmless replay: the server
 * treats a second intent from the same driver as idempotent. dispatch-svc already computes the fact
 * (`ScheduledRideResponse.HasIntent`) and `dispatch.yaml` does not declare it — recorded as a spec
 * gap in the C072 handoff.
 */
@OptIn(ExperimentalTime::class)
internal class JobBoardViewModel(
    private val identity: DriverIdentity,
    private val jobs: JobsRepository,
    private val location: DriverLocationSource,
    private val board: JobBoard = JobBoard(),
    private val clock: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(JobBoardState())
    private val posted = mutableSetOf<Ulid>()

    val state: StateFlow<JobBoardState> = mutableState.asStateFlow()

    init {
        refresh()
        tick()
    }

    /** Re-reads the level gate and, when it opens, the board. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val standing = jobs.standing(driverId)
            val access = standing.hasJobBoardAccess

            if (access != true) {
                // US-6A.8 is a gate, not an error. A Level-1 driver still takes immediate Mode C
                // rides; what they lose is this screen, and the copy says which level opens it.
                mutableState.update {
                    it.copy(
                        loading = false,
                        gated = access?.let { open -> !open },
                        minimumLevel = standing.rules.jobBoardMinLevel,
                        rows = emptyList(),
                    )
                }
                return@launchGuarded
            }

            // The board is anchored on where the driver is. `DriverLocationSource` is cold and
            // answers nothing until GNSS does, so the read waits for the first fix rather than
            // sending a (0, 0) the server would answer honestly and uselessly. It is bounded,
            // because a revoked permission or a switched-off receiver never emits at all — and a
            // board that spun for ever would look like an outage rather than like a setting.
            val here = withTimeoutOrNull(POSITION_WAIT) { location.fixes.first() }
            if (here == null) {
                mutableState.update {
                    it.copy(loading = false, gated = false, rows = emptyList(), error = R.string.job_board_no_position)
                }
                return@launchGuarded
            }

            val rides = jobs.board(
                lat = here.lat,
                lng = here.lng,
                radiusMetres = JobBoard.CATCHMENT_METRES,
            )

            mutableState.update {
                it.copy(
                    loading = false,
                    gated = false,
                    minimumLevel = standing.rules.jobBoardMinLevel,
                    rows = rows(rides, standing, clock()),
                )
            }
        }
    }

    /**
     * **Post intent** — the only action on this board (US-6A.5).
     *
     * The verdict is re-checked against the clock at the moment of the tap, not at the moment the
     * row was drawn: a card that sat on screen through T-30 must not send an intent no dispatch
     * round will read.
     */
    fun postIntent(row: JobBoardRow) {
        if (!row.canPost) return
        val driverId = identity.driverId ?: return

        mutableState.update { state -> state.copy(rows = state.rows.mark(row.id, posting = true)) }

        launchGuarded(onFailure = { mutableState.update { it.copy(rows = it.rows.mark(row.id, posting = false)) } }) {
            jobs.postIntent(row.id)
            posted += row.id

            val standing = jobs.standing(driverId)
            mutableState.update { state ->
                state.copy(rows = rows(state.rows.map(JobBoardRow::ride), standing, clock()))
            }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * Re-evaluates every row against the clock, once a second.
     *
     * This is what makes the wireframe's *"expired job → overlay fade"* and the DoD's *"rows
     * disappear once their T-30 window passes"* one behaviour rather than two: a row goes
     * [JobBoardRow.expired] the second the window closes, which is what the card animates on, and
     * leaves the list [EXPIRY_FADE] later, once that animation has been seen.
     */
    private fun tick() {
        viewModelScope.launch {
            while (isActive) {
                delay(TICK)
                val now = clock()
                mutableState.update { state ->
                    if (state.rows.isEmpty()) return@update state
                    state.copy(rows = state.rows.reappraise(now))
                }
            }
        }
    }

    /**
     * The board as rows, soonest first and **already past the fade filter**.
     *
     * A read that arrives holding rides whose window shut ten minutes ago must not print them and
     * then take them away a second later; the fade is for a row that expires while the driver is
     * looking at it, not for one that was dead when it landed.
     */
    private fun rows(rides: List<ScheduledRide>, standing: JobStanding, now: Timestamp): List<JobBoardRow> {
        val driver = standing.detailed ?: DriverStanding()
        return rides
            .sortedBy { it.pickupTime }
            .map { ride ->
                JobBoardRow(
                    ride = ride,
                    verdict = board.canPostIntent(driver = driver, ride = ride, now = now, postedRideIds = posted),
                    expired = board.timeToGoLive(ride, now) == Duration.ZERO,
                )
            }
            .filter { it.isVisibleAt(now) }
    }

    private fun List<JobBoardRow>.reappraise(now: Timestamp): List<JobBoardRow> = filter { it.isVisibleAt(now) }
        .map { it.copy(expired = board.timeToGoLive(it.ride, now) == Duration.ZERO) }

    /** Whether [now] is still inside the row's own life — its window, plus [EXPIRY_FADE]. */
    private fun JobBoardRow.isVisibleAt(now: Timestamp): Boolean = now < board.goesLiveAt(ride) + EXPIRY_FADE

    private fun List<JobBoardRow>.mark(id: Ulid, posting: Boolean): List<JobBoardRow> =
        map { if (it.id == id) it.copy(posting = posting) else it }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
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

    internal companion object {

        /** How often the board re-reads the clock. The T-30 edge is a second, not a refresh. */
        val TICK: Duration = 1.seconds

        /**
         * How long an expired card stays up before it is dropped.
         *
         * D2' §SCR-DA-017's animation is *"card expire fade"* — a row that vanished the instant it
         * expired would look like a tap that lost the driver a job. Long enough to read, short
         * enough that the board is never a list of rides nobody can bid on.
         */
        val EXPIRY_FADE: Duration = 3.seconds

        /**
         * How long the board waits for the device's first fix before saying it has none.
         *
         * The fused provider answers its *last known* position immediately when it has one, so this
         * only bites on a handset that has never had a fix, has the receiver switched off, or has
         * had the permission revoked since SCR-DA-007 — all of which are settings, not outages.
         */
        val POSITION_WAIT: Duration = 8.seconds
    }
}
