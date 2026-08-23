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

            if (cached.isEmpty) return@launch

            // `update` rather than `value =`: a network read that has already answered outranks the
            // cache, and on a fast connection it genuinely can arrive first.
            mutableState.update {
                it.copy(
                    name = it.name ?: cached.name,
                    level = it.level ?: cached.level,
                    registration = it.registration ?: cached.registration,
                    photo = it.photo ?: cached.photoBytes,
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

                store.cacheIdentity(
                    driverId = driverId,
                    name = profile.firstName,
                    level = standing?.standing?.level,
                    registration = live?.registrationNumber,
                )

                mutableState.update {
                    it.copy(
                        name = profile.firstName,
                        level = standing?.standing?.level,
                        registration = live?.registrationNumber,
                        // No app-facing read carries a driver's own star average. See
                        // `DriverHeaderState` for the four places it is not.
                        rating = null,
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
