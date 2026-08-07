package lk.mageride.passenger.location

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.onEach
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place

/**
 * The last position any screen saw, for the screens that need one and have no business subscribing.
 *
 * **Why this exists rather than a second subscription.** [PassengerLocationSource] is a
 * `callbackFlow`: the fused provider starts on the first collector and stops with the last. A place
 * picker that wanted to bias a geocoder, or a booking that wanted a default pickup, would each be
 * another collector holding it open — so instead [RecordingPassengerLocationSource] records every
 * fix **as it passes through the seam**, and everybody else reads.
 *
 * It is therefore *last known* rather than *current*, and that is the honest name: on a cold start
 * before the map has drawn, it is `null`, and every reader already has an answer for that.
 *
 * ### The defect it was written for (Δ C097)
 *
 * **[BookingDraft.begin] takes an optional pickup and no production call site was passing one.** All
 * three `draft.begin(…)` calls in `PassengerNavHost` omitted it, so a booking begun from the home
 * sheet or from SCR-PA-008 had `pickup == null` — and `RideBookingViewModel.refresh()` returns early
 * on exactly that, which meant **SCR-PA-009 showed no bus routes and no tiers at all**.
 * `RideBookingViewModelTest` did not catch it because its own setup passed a pickup that nothing in
 * the app passed. Found from the iOS side while porting C079 as C097.
 *
 * The fix is *inside* [BookingDraft] rather than at the three call sites, so a fourth one cannot
 * reintroduce it: `begin` falls back to [asPlace] whenever it is given no pickup.
 *
 * `@Volatile` and nothing more. It is written from whichever collector is running and read from a
 * composition; the annotation is for the collectors that are not on the main thread.
 */
internal class LastKnownFix {

    @Volatile
    var point: GeoPoint? = null
        private set

    /** A fix on its way past — see [RecordingPassengerLocationSource]. */
    fun record(fix: PassengerFix) {
        point = fix.asPoint()
    }

    /**
     * The fix as a booking's pickup, with no address.
     *
     * A reverse-geocode for a label the passenger is about to overwrite is a call nobody asked for;
     * SCR-PA-009's summary prints *"Current location"* where the address is absent.
     */
    fun asPlace(): Place? = point?.let { Place(lat = it.lat, lng = it.lng) }
}

/**
 * The fix source, recording what passes through it.
 *
 * **A decorator rather than a parameter on the map's view model** (Δ C097). Writing to [LastKnownFix]
 * from SCR-PA-010 alone would work — it is the one screen that collects for its whole life — but it
 * would also be a rule living in one of five collectors, and the other four (SCR-PA-011's pin,
 * SCR-PA-026's address capture, SCR-PA-029's alarm, and whatever comes next) know the passenger's
 * position just as well. Recording at the seam means the last known fix is the last one *anybody*
 * saw, costs no extra collector, and takes a constructor parameter off a view model that was already
 * at detekt's ceiling.
 *
 * `onEach` rather than a `SharedFlow`: the delegate is a cold `callbackFlow` and this must not change
 * that. The fused provider still starts on the first collector and stops with the last.
 */
internal class RecordingPassengerLocationSource(
    private val delegate: PassengerLocationSource,
    private val lastFix: LastKnownFix,
) : PassengerLocationSource {

    override val fixes: Flow<PassengerFix> get() = delegate.fixes.onEach(lastFix::record)
}
