package lk.mageride.passenger.push

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import lk.mageride.passenger.nav.PassengerRoute

/**
 * One notification as notification-svc sends it.
 *
 * The shape is C051's `data` dictionary, not a guess: the service writes `kind` and (where the
 * push opens a screen) `deeplink` and `notificationId`, plus per-kind extras — `requestId` and
 * `ttl` on a location request, `rideId` and `deliveryOtp` on a package push.
 *
 * Everything is a `String`: FCM's data payload is `Map<String, String>` on the wire and coercing
 * here would throw inside a broadcast receiver, which is the one place a crash is invisible.
 *
 * @property kind `data.kind`. Lower case for the two the service special-cases (`ride_offer` is
 *   the driver's; `location_request` is this app's), the SCREAMING type name otherwise.
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

        /**
         * P-02's silent data message — `{kind:'location_request', requestId, bookerName, ttl:300}`.
         *
         * `notification-svc`'s `NotificationCatalogue.LocationRequest`, lower case because D5'
         * §14.4 spells it that way and it is a wire value.
         */
        const val KIND_LOCATION_REQUEST: String = "location_request"

        /** `data.requestId` on the above — `rides.location_requests.request_id`. */
        const val KEY_REQUEST_ID: String = "requestId"

        /** US-6A.15's reminder before a scheduled pickup (D5' §14.4). SCREAMING, per the catalogue. */
        const val KIND_SCHEDULED_REMINDER: String = "SCHEDULED_REMINDER"

        /** `data.rideId` on a package push — the ride both tracking screens are scoped to. */
        const val KEY_RIDE_ID: String = "rideId"

        /**
         * `data.deliveryOtp` on `package_picked_up` (US-20.5).
         *
         * **The only place this value ever appears.** `RideDetail` has no field for it and no read
         * returns it, so SCR-PA-021 can show it exactly when the push that carried it was seen by
         * this process. Recorded in the C081 handoff.
         */
        const val KEY_DELIVERY_OTP: String = "deliveryOtp"
    }
}

/**
 * Turns a push into a destination, and hands it to whoever is showing the UI.
 *
 * **A deep link is resolved, never trusted.** `deeplink` arrives over the network; mapping it to a
 * known [PassengerRoute] rather than handing the raw URI to the navigator means an unrecognised or
 * hostile value opens nothing instead of whatever a future `mageride://` handler might accept.
 *
 * The router is a process singleton in the Koin graph because both entry points feed it: the FCM
 * service while the app is backgrounded, and `MainActivity` when a notification tap starts the app
 * with an intent. Its flow replays one value, so a push that arrives before the shell is composed
 * is still delivered — the alternative is a tapped notification that silently does nothing on a
 * cold start.
 */
internal class PushRouter {

    private val mutablePending = MutableSharedFlow<PassengerRoute>(
        replay = 1,
        extraBufferCapacity = 1,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    /** Destinations a push asked for. Collected once, by the shell. */
    val pending: SharedFlow<PassengerRoute> = mutablePending.asSharedFlow()

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
        fun routeFor(message: PushMessage): PassengerRoute? = when (message.kind) {
            // P-02 / P-13. **The one push on this surface that carries no deeplink at all** —
            // `LocationRequestAsync` in notification-svc writes `{kind, requestId, ttl}` and
            // nothing else, because the screen is a *silent* data message the app renders itself.
            // The route is therefore built from `data.requestId`; there is no URI to resolve, and
            // inventing a `mageride://pickup-confirm/{id}` host would be a client claiming a link
            // the server does not mint.
            PushMessage.KIND_LOCATION_REQUEST ->
                message.data[PushMessage.KEY_REQUEST_ID]
                    ?.takeIf(String::isNotBlank)
                    ?.let(PassengerRoute::ConfirmPickup)

            // US-6A.15 / US-10.9 — "your ride is in 30 minutes". It carries no deeplink either:
            // `DeepLinks` in Notification.Api mints four URIs and none names a scheduled ride, so
            // the routing is on the TYPE, which D5' §14.4 and the catalogue both fix. SCR-PA-022's
            // "Scheduled" tab is where an upcoming ride lives on this side. Same gap the driver
            // app recorded in the C072 handoff; if a `mageride://scheduled` host is ever minted,
            // `resolve` is where it belongs.
            PushMessage.KIND_SCHEDULED_REMINDER -> PassengerRoute.Trips

            else -> resolve(message.deeplink)
        }

        /**
         * `mageride://ride/{id}` → the ride screen, and so on for the four links notification-svc
         * mints (`DeepLinks` in its `EventHandlers.cs`; D6' I-23.3 prints the package one).
         *
         * **Two of the four open nothing on this surface, and that is correct rather than a gap.**
         * `mageride://wallet` and `mageride://documents` are the driver's — a passenger has no
         * wallet screen and no documents to expire — and notification-svc never addresses either
         * to a passenger. Resolving them to "the nearest passenger screen" would open something
         * arbitrary the first time an operator mis-targeted a broadcast.
         *
         * Anything else — a scheme that is not ours, a host with no screen, a malformed URI — is
         * `null`. Parsing by hand rather than with `android.net.Uri` keeps this testable on the
         * JVM, which is where the table is asserted.
         */
        @Suppress("ReturnCount") // One guard per malformed-input case; nesting them reads worse.
        fun resolve(uri: String?): PassengerRoute? {
            val value = uri?.trim().orEmpty()
            if (!value.startsWith(SCHEME)) return null

            val path = value.removePrefix(SCHEME).trim('/')
            if (path.isEmpty()) return null

            val segments = path.split('/')
            val id = segments.getOrNull(1)?.takeIf(String::isNotBlank)
            return when (segments[0]) {
                HOST_RIDE -> id?.let(PassengerRoute::ActiveRide)

                // R-01 keeps one ride aggregate, but the passenger side has two screens for a
                // parcel (SCR-PA-020 sender, SCR-PA-021 recipient) where the driver side has one.
                // Which of the two to draw is a fact about the ride, so both audiences land on one
                // destination and C081 reads the ride to decide. D6' I-23.3 names SCR-PA-021 as
                // this link's target; the sender gets the same URI on `package_delivered`.
                HOST_PACKAGE -> id?.let(PassengerRoute::PackageTracking)

                else -> null
            }
        }

        /** D6' I-23.3's scheme. */
        const val SCHEME: String = "mageride://"

        private const val HOST_RIDE = "ride"
        private const val HOST_PACKAGE = "package"
    }
}
