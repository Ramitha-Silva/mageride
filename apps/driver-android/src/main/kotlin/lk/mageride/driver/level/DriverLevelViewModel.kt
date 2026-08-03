package lk.mageride.driver.level

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.jobs.JobStanding
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.domain.dispatch.DriverLevelRules

/**
 * SCR-DA-019's state.
 *
 * @property loading The read is in flight.
 * @property standing The level, the points and the counters.
 * @property error Resolved copy for the last failure.
 */
internal data class DriverLevelState(
    val loading: Boolean = true,
    val standing: JobStanding = JobStanding(),
    @param:StringRes val error: Int? = null,
) {

    /** The level itself, `1`–`3`; `null` until reputation has answered. */
    val level: Int? get() = standing.standing?.level

    /** Points banked toward the next level. */
    val points: Int get() = standing.detailed?.points ?: 0

    /** What a level costs, from the server's config when it sent one (US-14.12). */
    val threshold: Int get() = standing.rules.levelUpThreshold

    /** Whether the driver is already at [DriverLevelRules.MAX_LEVEL] — see [DriverLevelViewModel]. */
    val atTopLevel: Boolean get() = (level ?: 0) >= DriverLevelRules.MAX_LEVEL

    /** The next level a driver can reach, or `null` at the top. */
    val nextLevel: Int? get() = level?.plus(1)?.takeIf { it <= DriverLevelRules.MAX_LEVEL }

    /**
     * How full the bar is, `0f`–`1f`.
     *
     * Full at the top level: the points are still banked and still spent on a crossing
     * (D5' §4.2's `points -= 500` applies at the cap too), but there is no rung above to progress
     * toward, and a bar frozen at 20% would read as a driver who had stopped earning.
     */
    val progress: Float
        get() = when {
            level == null -> 0f
            atTopLevel -> 1f
            threshold <= 0 -> 0f
            else -> (points.toFloat() / threshold).coerceIn(0f, 1f)
        }

    /** US-6A.14's acceptance rate as whole percent, or `null` when the stats read did not answer. */
    val acceptancePercent: Int?
        get() = standing.acceptanceRate?.let { (it * PERCENT).toInt().coerceIn(0, PERCENT) }

    /** Scheduled-ride no-shows (US-6A.7). */
    val noShows: Int? get() = standing.noShows

    private companion object {
        const val PERCENT = 100
    }
}

/**
 * **SCR-DA-019 · Driver Level & stats** (US-6A.6, US-6A.14).
 *
 * Two reads, `GET /v1/drivers/{driverId}/level` and `…/stats`, both best-effort — see
 * [JobsRepository].
 *
 * ### Levels run 1–3, and the wireframe draws a fourth
 *
 * `driver_android.html` prints *"510 / 500 pts → Level 4"* above a bar at 88%. **D5' §4.2 gives
 * three levels** (`dispatch.driver_levels`, `level = min(level + 1, 3)`) and `:shared`'s
 * [DriverLevelRules.MAX_LEVEL] is 3, so there is no Level 4 to progress toward. The layout is the
 * wireframe's — badge, points line, bar, the two stat cards, the warning — and the *copy* on the
 * points line becomes "this is the top level" once a driver is at 3. Recorded as a wireframe/D5'
 * conflict in the C072 handoff; D5' wins, because a screen promising a rung the dispatcher cannot
 * award is worse than a screen that says the ladder ends here.
 *
 * ### The reports counter is not on the wire
 *
 * `GET …/stats` answers `acceptanceRate`, `noShows` and `points`. **Nothing app-facing carries the
 * passenger-report count** — `reputation.block_state` and its counters are C033's gRPC and portal
 * surface (C012 models no reputation DTO on purpose) — so the wireframe's *"⚠ 3 reports → level
 * drop + temporary delisting"* is rendered as the **rule**, which is exactly the sentence it prints,
 * and never as a live tally. A screen cannot warn a driver that they are on their second report,
 * and inventing a count would be worse than saying the rule.
 */
internal class DriverLevelViewModel(private val identity: DriverIdentity, private val jobs: JobsRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(DriverLevelState())

    val state: StateFlow<DriverLevelState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads the level and the counters. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val standing = jobs.standing(driverId)
            mutableState.update { it.copy(loading = false, standing = standing) }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

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
}
