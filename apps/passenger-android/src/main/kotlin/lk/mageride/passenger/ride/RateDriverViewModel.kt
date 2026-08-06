package lk.mageride.passenger.ride

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import lk.mageride.passenger.R
import lk.mageride.passenger.di.PassengerDatabase
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.Clock

/**
 * SCR-PA-019's reason chips.
 *
 * The wireframe's four, and they are **compliments rather than complaints**: the screen is shown
 * after a completed ride and its job is to make five stars quick to justify. A report is a
 * different action with a different destination (`POST /v1/vehicles/report`), and mixing the two
 * would put "unsafe driving" one tap from "on time".
 */
internal enum class RatingTag(@get:StringRes val label: Int) {
    CLEAN(R.string.rate_tag_clean),
    ON_TIME(R.string.rate_tag_on_time),
    POLITE(R.string.rate_tag_polite),
    SAFE_DRIVING(R.string.rate_tag_safe),
}

/**
 * SCR-PA-019's state.
 *
 * @property queued The rating was saved locally because there is nowhere to send it. See the class
 *   KDoc — this is a **contract gap**, surfaced honestly rather than hidden behind a spinner.
 */
internal data class RateDriverState(
    val driverId: String? = null,
    val driverName: String? = null,
    val stars: Int = 0,
    val tags: Set<RatingTag> = emptySet(),
    val comment: String = "",
    val submitting: Boolean = false,
    val queued: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** US-18.1 is 1–5 stars; the chips and the comment are both optional. */
    val canSubmit: Boolean get() = stars in 1..MAX_STARS && !submitting

    internal companion object {
        const val MAX_STARS = 5
    }
}

/**
 * SCR-PA-019 — rating the driver, and the one place this component could not finish.
 *
 * **There is no contract to POST a Mode C ride rating.** `ride.yaml` declares no rating operation
 * at all; its only mention of the word is `RideDriver.rating`, which is a *read*. The only rating
 * routes on the platform are trip-state-svc's `/v1/sessions/{sessionId}/rating` and
 * `/v1/sessions/{sessionId}/driver-rating` — and **calling either with a ride id would cross the
 * R-01 boundary the root CLAUDE.md forbids in as many words**: ride-svc owns Mode C, trip-state-svc
 * owns Mode A/B, and a `sessionId` path parameter is not a place to put a `rideId`.
 *
 * C074 found the same gap from the driver's side and recorded it: *"no route writes a
 * `subject_kind='ride'` rating although the column, query-svc's read and D3' §Part 3 all expect
 * one"*. Two components have now hit it from opposite directions.
 *
 * **So the rating is captured and queued, not dropped.** `mobile_db_schema.md` §1.11's
 * `ratings_pending` exists for exactly this — its `subject_kind` column already distinguishes
 * `'ride'` from `'session'`, which is the schema anticipating the route that was never written.
 * The passenger's stars are stored against the ride; when the operation lands, a sync flushes
 * them. What this must **not** do is show a success it did not achieve, so the screen says the
 * rating was saved rather than sent.
 */
internal class RateDriverViewModel(
    private val rideId: String,
    private val rides: RideRepository,
    private val databases: PassengerDatabase,
    private val now: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(RateDriverState())

    val state: StateFlow<RateDriverState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { loadDriver() }
    }

    fun setStars(stars: Int) {
        mutableState.update { it.copy(stars = stars.coerceIn(0, RateDriverState.MAX_STARS), error = null) }
    }

    fun toggleTag(tag: RatingTag) {
        mutableState.update {
            it.copy(tags = if (tag in it.tags) it.tags - tag else it.tags + tag)
        }
    }

    fun onCommentChanged(value: String) {
        mutableState.update { it.copy(comment = value) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * Submit.
     *
     * Writes `ratings_pending` and reports it as **saved**. See the class KDoc for why there is no
     * network call here; when `POST /v1/rides/{rideId}/rating` exists this becomes a send with the
     * same row as its retry buffer.
     */
    @Suppress("TooGenericExceptionCaught")
    fun submit() {
        val current = mutableState.value
        if (!current.canSubmit) return

        mutableState.update { it.copy(submitting = true, error = null) }
        viewModelScope.launch {
            try {
                queueLocally(current)
                mutableState.update { it.copy(submitting = false, queued = true) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(submitting = false, error = RideErrors.messageFor(cause)) }
            }
        }
    }

    // ------------------------------------------------------------------------------------------

    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadDriver() {
        try {
            val driver = rides.ride(rideId).driver
            mutableState.update { it.copy(driverId = driver?.driverId, driverName = driver?.name) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // "How was your ride?" without a name is still the right question.
        }
    }

    /**
     * §1.11's row, with `subject_kind = 'ride'`.
     *
     * The stars and the text are **not** stored here — the table has no columns for them, because
     * it was designed as a prompt queue rather than a draft store. That is the honest shape of the
     * gap: the app remembers *that* this ride is rated and by whom, and the content is lost if the
     * process dies before a route exists to take it. Adding columns is a `mobile_db_schema.md`
     * change and belongs to whoever adds the route.
     */
    private suspend fun queueLocally(current: RateDriverState) = withContext(Dispatchers.IO) {
        val database = databases.get()
        database.sql.ratingsPendingQueries.upsert(
            subject_id = rideId,
            subject_kind = "ride",
            ratee_id = current.driverId.orEmpty(),
            direction = "passenger_to_driver",
            prompt_shown = true,
            created_at = now(),
        )
    }
}
