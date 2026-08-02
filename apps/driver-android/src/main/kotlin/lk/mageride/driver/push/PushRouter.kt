package lk.mageride.driver.push

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import lk.mageride.driver.nav.DriverRoute

/**
 * One notification as notification-svc sends it.
 *
 * The shape is C051's `data` dictionary, not a guess: the service writes `kind`, `deeplink` and
 * `notificationId` on every push it produces, plus per-kind extras (`rideId`, `fare`, `distance`
 * on an offer; `requestId`, `ttl` on a location request). The C051 handoff spells out the client
 * half — *"the `data.kind` switch (`ride_offer`, `location_request`, and the type name for
 * everything else), and `data.deeplink`"*.
 *
 * Everything is a `String`: FCM's data payload is `Map<String, String>` on the wire and coercing
 * here would throw inside a broadcast receiver, which is the one place a crash is invisible.
 *
 * @property kind `data.kind`. Lower-case for the three the service special-cases, the SCREAMING
 *   type name otherwise.
 * @property deeplink `data.deeplink` — a `mageride://…` URI, absent on kinds that open nothing.
 * @property notificationId `data.notificationId`, the id `POST /v1/notify/ack` takes.
 * @property data The whole dictionary, for a screen that needs a per-kind extra.
 */
internal data class PushMessage(
    val kind: String?,
    val deeplink: String?,
    val notificationId: String?,
    val data: Map<String, String>,
) {
    internal companion object {
        /** Reads one off an FCM `RemoteMessage.getData()`. */
        fun from(data: Map<String, String>): PushMessage = PushMessage(
            kind = data[KEY_KIND],
            deeplink = data[KEY_DEEPLINK],
            notificationId = data[KEY_NOTIFICATION_ID],
            data = data,
        )

        const val KEY_KIND: String = "kind"
        const val KEY_DEEPLINK: String = "deeplink"
        const val KEY_NOTIFICATION_ID: String = "notificationId"

        /** E-01's 15 s atomic offer. The only kind that takes over the screen. */
        const val KIND_RIDE_OFFER: String = "ride_offer"
    }
}

/**
 * Turns a push into a destination, and hands it to whoever is showing the UI.
 *
 * **A deep link is resolved, never trusted.** `deeplink` arrives over the network; mapping it to a
 * known [DriverRoute] rather than handing the raw URI to the navigator means an unrecognised or
 * hostile value opens Home instead of whatever a future `mageride://` handler might accept.
 *
 * The router is a process singleton in the Koin graph because both entry points feed it: the FCM
 * service while the app is backgrounded, and `MainActivity` when a notification tap starts the
 * app with an intent. Its flow replays one value, so a push that arrives before the shell is
 * composed is still delivered — the alternative is a tapped notification that silently does
 * nothing on a cold start.
 */
internal class PushRouter {

    private val mutablePending = MutableSharedFlow<DriverRoute>(
        replay = 1,
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /** Destinations a push asked for. Collected once, by the shell. */
    val pending: SharedFlow<DriverRoute> = mutablePending.asSharedFlow()

    /** Offers [message]'s destination to the shell. No-op when the push opens nothing. */
    fun offer(message: PushMessage) {
        routeFor(message)?.let(mutablePending::tryEmit)
    }

    /** Offers a raw `mageride://…` URI — the notification-tap path, where only the intent survives. */
    fun offer(uri: String?) {
        resolve(uri)?.let(mutablePending::tryEmit)
    }

    /**
     * Forgets the replayed value once the shell has navigated, so a rotation does not re-navigate.
     *
     * `resetReplayCache` is the one API that expresses "this was consumed" on a replaying
     * `SharedFlow`; the alternative is a nullable wrapper every collector has to unwrap.
     */
    @OptIn(ExperimentalCoroutinesApi::class)
    fun consume() {
        mutablePending.resetReplayCache()
    }

    internal companion object {

        /** The destination [message] should open. */
        fun routeFor(message: PushMessage): DriverRoute? = when (message.kind) {
            // The offer sheet is not a deep link — it is a takeover the dashboard owns (C070),
            // and it must open even though `offer.created` carries a ride deeplink for the
            // passenger side. Routing it to Home is what puts the driver where the sheet shows.
            PushMessage.KIND_RIDE_OFFER -> DriverRoute.Home

            else -> resolve(message.deeplink)
        }

        /**
         * `mageride://ride/{id}` → the active-ride screen, and so on for the four links
         * notification-svc mints (`DeepLinks` in its `EventHandlers.cs`; C051 note (n)).
         *
         * Anything else — a scheme that is not ours, a host with no screen, a malformed URI —
         * is `null`. Parsing by hand rather than with `android.net.Uri` keeps this testable on
         * the JVM, which is where the table is asserted.
         */
        @Suppress("ReturnCount") // One guard per malformed-input case; nesting them reads worse.
        fun resolve(uri: String?): DriverRoute? {
            val value = uri?.trim().orEmpty()
            if (!value.startsWith(SCHEME)) return null

            val path = value.removePrefix(SCHEME).trim('/')
            if (path.isEmpty()) return null

            val segments = path.split('/')
            val id = segments.getOrNull(1)?.takeIf(String::isNotBlank)
            return when (segments[0]) {
                HOST_RIDE -> id?.let(DriverRoute::ActiveRide)
                HOST_PACKAGE -> id?.let(DriverRoute::ActiveRide)
                HOST_WALLET -> DriverRoute.Wallet
                HOST_DOCUMENTS -> DriverRoute.Documents
                else -> null
            }
        }

        /** D6' I-23.3's scheme. */
        const val SCHEME: String = "mageride://"

        private const val HOST_RIDE = "ride"

        // A package delivery IS a ride (R-01 keeps one aggregate), so both links land on the
        // same screen. The distinction the passenger app makes — SCR-PA-021 — has no driver-side
        // counterpart: a driver carrying a parcel is on the ordinary active-ride screen.
        private const val HOST_PACKAGE = "package"
        private const val HOST_WALLET = "wallet"
        private const val HOST_DOCUMENTS = "documents"
    }
}
