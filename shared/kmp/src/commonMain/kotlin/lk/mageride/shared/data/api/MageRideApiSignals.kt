package lk.mageride.shared.data.api

import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/**
 * The `426 Upgrade Required` payload (D-31), lifted out of the Problem body.
 *
 * The same three fields are what `GET /v1/version/check` returns at cold start
 * ([lk.mageride.shared.data.models.version.AppVersionCheck]) — one screen renders both.
 *
 * @property latestVersion The newest published build for this platform.
 * @property updateUrl Store link for the update.
 * @property isMandatory `true` blocks the app; `false` is a dismissible nudge.
 */
public data class UpgradeRequiredSignal(val latestVersion: String?, val updateUrl: String?, val isMandatory: Boolean)

/**
 * Call-independent events the HTTP layer raises, for the app shell rather than the caller.
 *
 * **Why a signal and not just the exception.** The version gate runs at the edge on *every*
 * route (D-31), so any of the 176 operations can answer `426`. Handling that at each call site
 * would mean 176 chances to forget. The typed error is still thrown — the caller's own flow must
 * not silently continue — but the app shell subscribes here once and puts up the update wall.
 *
 * [upgradeRequired] replays its last value, so a screen that subscribes after the failing call
 * still sees it.
 */
public class MageRideApiSignals {

    private val mutableUpgradeRequired = MutableSharedFlow<UpgradeRequiredSignal>(
        replay = 1,
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /** Emits whenever the gateway answers `426`, and replays the most recent one to new collectors. */
    public val upgradeRequired: SharedFlow<UpgradeRequiredSignal> = mutableUpgradeRequired.asSharedFlow()

    /** Publishes a `426`. Called by the request pipeline; also useful from a cold-start check. */
    public fun publishUpgradeRequired(signal: UpgradeRequiredSignal) {
        mutableUpgradeRequired.tryEmit(signal)
    }
}
