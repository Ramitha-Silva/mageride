package lk.mageride.driver.menu

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.profile.DriverProfileStore
import lk.mageride.driver.profile.ProfileRepository
import lk.mageride.driver.ui.component.DriverHeaderState

/**
 * SCR-DA-036's header (Δ MCS-24).
 *
 * The drawer used to render the app's name — *"MageRide Driver"* — where the wireframe draws the
 * driver. It needed no state to do that, which is why it had none; the header it actually specifies
 * is *"avatar, name, level badge, driver id and rating"*, and every one of those is a read.
 *
 * **Three reads, and none of them blocks the menu.** The rows are the point of this screen and they
 * are static, so the header fills in behind them and a failure leaves it on its defaults rather
 * than putting an error over a list of links that all still work.
 */
internal class MenuViewModel(
    private val identity: DriverIdentity,
    private val profiles: ProfileRepository,
    private val store: DriverProfileStore,
) : ViewModel() {

    private val mutableState = MutableStateFlow(DriverHeaderState())

    val header: StateFlow<DriverHeaderState> = mutableState.asStateFlow()

    init {
        // Δ MCS-27 — the cache FIRST, and synchronously enough that the drawer's first frame has a
        // name in it. Everything below is a refresh behind something already on screen.
        paintFromCache()
        refresh()
        refreshPhoto()
    }

    /**
     * What the handset already knows, drawn before a single call is made (§3.16).
     *
     * The drawer used to open on *"Your name"* over a placeholder glyph and fill in a second later,
     * every time, on whatever connection the driver happened to have. This is that second.
     */
    private fun paintFromCache() {
        viewModelScope.launch {
            val driverId = identity.driverId ?: return@launch
            val cached = runCatching { store.cached(driverId) }.getOrNull() ?: return@launch

            // Δ MCS-32/33 — an EMPTY cache resolves NOTHING. `isResolved` means "something has
            // answered", and a handset that has never cached this driver has answered nothing: the
            // header holds its blank until a read does. Resolving here instead told every driver on
            // their first launch after an install that they had no name, which is `profile_unnamed`
            // — the copy for a driver who really has not set one.
            if (cached.isEmpty) return@launch

            if (cached.isEmpty) return@launch

            // `update` rather than `value =`: a network read that has already answered outranks the
            // cache, and on a fast connection it genuinely can arrive first.
            mutableState.update {
                it.copy(
                    name = it.name ?: cached.name,
                    level = it.level ?: cached.level,
                    registration = it.registration ?: cached.registration,
                    photo = it.photo ?: cached.photoBytes,
                    isResolved = true,
                )
            }
        }
    }

    /**
     * The avatar, on its own coroutine (Δ MCS-25).
     *
     * **Deliberately not part of [refresh].** The rows are the point of this screen and the header
     * fills in behind them; folding a fourth read into the same block would let a slow avatar hold
     * the name and the plate back with it, which is the opposite of what the header is for.
     */
    private fun refreshPhoto() {
        viewModelScope.launch {
            val driverId = identity.driverId ?: return@launch
            val photo = runCatching { store.photo(driverId) }.getOrNull() ?: return@launch

            mutableState.update { it.copy(photo = photo) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    fun refresh() {
        viewModelScope.launch {
            try {
                val profile = profiles.profile()
                val driverId = identity.driverId
                val standing = driverId?.let { id -> profiles.standing(id) }
                val live = identity.liveVehicle().live

                // Δ MCS-27 — so the next open has it. Only with an id to file it under: a
                // signed-out driver has no row, and inventing a key for one would leave a
                // stranger's name on this handset.
                driverId?.let { id ->
                    store.cacheIdentity(
                        driverId = id,
                        name = profile.firstName,
                        level = standing?.standing?.level,
                        registration = live?.registrationNumber,
                    )
                }

                // Δ MCS-29 — a degraded read must not ERASE what the cache just drew. Each of
                // these three comes from its own call: `profiles.standing` swallows its failures by
                // design and answers an empty standing, and a driver with no live vehicle has no
                // plate at all. Assigning those nulls straight in meant the drawer painted the
                // driver's name and level off disk and then blanked them a moment later, which is
                // worse than never having painted them.
                mutableState.update {
                    it.copy(
                        name = profile.firstName ?: it.name,
                        level = standing?.standing?.level ?: it.level,
                        registration = live?.registrationNumber ?: it.registration,
                        // No app-facing read carries a driver's own star average. See
                        // `DriverHeaderState` for the four places it is not.
                        rating = null,
                        // Δ MCS-32 — a server answered, so the header may draw a conclusion.
                        // Unconditional, unlike the cache above: a read that came back with no
                        // first name IS a driver who has not set one, and `profile_unnamed` is
                        // what the wireframe draws for that.
                        isResolved = true,
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // Deliberately silent. A header that could not load is a header with no name in it;
                // an error banner over eight working links would be worse than the blank.
            }
        }
    }
}
