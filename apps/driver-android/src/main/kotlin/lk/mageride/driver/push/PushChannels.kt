package lk.mageride.driver.push

/**
 * The notification channel ids this app publishes on.
 *
 * Two, not one per notification type: Android's channel is the unit a *user* silences, and a
 * driver who mutes "wallet reminders" must not thereby mute a ride offer. The split follows
 * notification-svc's own `priority` field — `high` for E-01's 15-second offer, `normal` for
 * everything else.
 *
 * The channels are created in `DriverApplication.onCreate`; the foreground service's own channel
 * is declared beside it and lives on `PositionForegroundService.CHANNEL_ID`.
 */
internal object PushChannels {

    /** E-01 ride offers and ride-state changes. `IMPORTANCE_HIGH` — a heads-up notification. */
    const val RIDE_OFFERS: String = "ride_offers"

    /** Wallet, daily fee, documents, announcements. `IMPORTANCE_DEFAULT`. */
    const val GENERAL: String = "general"

    /** Which channel a `data.kind` belongs on. */
    fun channelFor(kind: String?): String = if (kind == PushMessage.KIND_RIDE_OFFER) RIDE_OFFERS else GENERAL
}
