package lk.mageride.passenger.push

/**
 * The notification channel ids this app publishes on.
 *
 * Two, not one per notification type: Android's channel is the unit a *user* silences, and a
 * passenger who mutes package updates must not thereby mute the driver arriving. The split
 * follows notification-svc's own `priority` field — `high` for the two time-boxed messages
 * (P-02's 300 s location request, and the ride pushes a passenger is waiting on), `normal` for
 * everything else.
 *
 * The channels are created in `PassengerApplication.onCreate`.
 */
internal object PushChannels {

    /**
     * Ride state, and P-02's location request. `IMPORTANCE_HIGH` — a heads-up notification.
     *
     * The manifest names this one as `default_notification_channel_id`, so a push that arrives
     * with no channel of its own still reaches a passenger who is waiting for a driver.
     */
    const val RIDES: String = "rides"

    /** Packages, subscriptions, support replies, announcements. `IMPORTANCE_DEFAULT`. */
    const val GENERAL: String = "general"

    /** Which channel a `data.kind` belongs on. */
    fun channelFor(kind: String?): String = if (kind in RIDE_KINDS) RIDES else GENERAL

    /**
     * The kinds that go to [RIDES].
     *
     * From notification-svc's catalogue: the four ride pushes, plus P-02's silent data message —
     * which is `high` priority there precisely because *"the window is 300 s and a Dozing handset
     * would spend most of it asleep"*.
     */
    private val RIDE_KINDS = setOf(
        PushMessage.KIND_LOCATION_REQUEST,
        "DRIVER_ASSIGNED",
        "DRIVER_ARRIVED",
        "RIDE_CANCELLED",
        "PAYMENT_CONFIRMED",
    )
}
