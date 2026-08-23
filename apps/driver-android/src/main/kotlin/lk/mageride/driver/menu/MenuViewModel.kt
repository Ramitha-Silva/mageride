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
internal class MenuViewModel(private val identity: DriverIdentity, private val profiles: ProfileRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(DriverHeaderState())

    val header: StateFlow<DriverHeaderState> = mutableState.asStateFlow()

    init {
        refresh()
        refreshPhoto()
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
            val photoUrl = runCatching { profiles.driverPhotoUrl() }.getOrNull() ?: return@launch

            mutableState.update { it.copy(photoUrl = photoUrl) }
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
