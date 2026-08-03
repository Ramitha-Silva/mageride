package lk.mageride.driver.notifications

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.nav.DriverRoute
import lk.mageride.driver.push.PushRouter

/**
 * SCR-DA-034's state.
 *
 * @property alerts Every stored push, newest first.
 * @property loading The first read is in flight — the wireframe's shimmer.
 * @property opening A destination one row asked for, consumed by the screen.
 */
internal data class NotificationsState(
    val alerts: List<DriverAlert> = emptyList(),
    val loading: Boolean = true,
    val opening: DriverRoute? = null,
) {

    /** Whether the list is genuinely empty rather than not yet read. */
    val isEmpty: Boolean get() = !loading && alerts.isEmpty()
}

/**
 * **SCR-DA-034 · alerts** (Epic 10, D5' §14.4).
 *
 * **Read from the device, not from the platform.** There is no *"list my notifications"* operation
 * on the app-facing surface — see [NotificationInbox] — so this is `mobile_db_schema.md` §1.6, which
 * is also why the screen works with no connection at all.
 *
 * **A row's deep link is resolved, never trusted** (the shell's rule). The stored `deeplink` came
 * over the network inside an FCM payload; `PushRouter.resolve` maps it onto a known [DriverRoute]
 * and an unrecognised one opens nothing rather than being handed to the navigator.
 */
internal class NotificationsViewModel(private val inbox: NotificationInbox) : ViewModel() {

    private val mutableState = MutableStateFlow(NotificationsState())

    val state: StateFlow<NotificationsState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads §1.6. */
    fun refresh() {
        mutableState.update { it.copy(loading = true) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val alerts = inbox.all()
            mutableState.update { it.copy(loading = false, alerts = alerts) }
        }
    }

    /**
     * Opens one alert.
     *
     * Marked read **locally and immediately** — the row is the device's own and there is nothing to
     * confirm — and then routed if its link resolves to a screen. An alert that opens nothing is
     * still marked read: the driver has looked at it, which is the only thing `read` claims.
     */
    fun open(alert: DriverAlert) {
        mutableState.update { current ->
            current.copy(
                alerts = current.alerts.map { if (it.id == alert.id) it.copy(read = true) else it },
                opening = PushRouter.resolve(alert.deeplink),
            )
        }
        launchGuarded { inbox.markRead(alert.id) }
    }

    /** Clears the pending navigation once the screen has acted on it. */
    fun consumeOpening() {
        mutableState.update { it.copy(opening = null) }
    }

    /** Marks the whole list read. */
    fun markAllRead() {
        mutableState.update { current -> current.copy(alerts = current.alerts.map { it.copy(read = true) }) }
        launchGuarded { inbox.markAllRead() }
    }

    @Suppress("TooGenericExceptionCaught")
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // A local read or write failed. There is no server call here and nothing a driver
                // could do about a SQLite error, so the list simply stops loading — the same
                // reasoning `ProofUploadQueue` applies to its own storage failures.
                onFailure()
            }
        }
    }
}
