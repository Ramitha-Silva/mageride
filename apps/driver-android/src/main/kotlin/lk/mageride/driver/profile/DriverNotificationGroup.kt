package lk.mageride.driver.profile

import androidx.annotation.StringRes
import lk.mageride.driver.R

/**
 * The notification switches SCR-DA-029 offers, grouped the way D2' §SCR-DA-034 groups the alerts
 * themselves — *"dispatch / fee / registration / sharing / directional"*.
 *
 * **The keys are notification-svc's `notification_type` values, verbatim and case-sensitive.**
 * `NotificationPreferences`'s own KDoc says so: *"the keys are push-event names from D3' Part 3, not
 * an enum: the event list grows without a contract change"*. So they are `const` strings here rather
 * than a shared enum, transcribed from `NotificationCatalogue` — which is also why the P-02 location
 * request is lower case and everything else is not.
 *
 * **A group, not a switch per type.** Fifteen switches is a settings screen nobody reads; five is a
 * decision a driver can make. Toggling one writes `false` for **every** key in it, which is exactly
 * what "turn off wallet alerts" means. The server keeps the map, so a key this build has never
 * heard of survives a save untouched — see [ProfileRepository.saveNotificationPreferences].
 *
 * **Nothing safety-critical is here.** `SOS_TRIGGERED`, `SOS_RESOLVED` and `RIDE_CANCELLED` are
 * `NotificationCatalogue.SafetyCritical`; iam-svc drops a mute for one on the way in and
 * notification-svc ignores it on the way out. Offering a switch the platform refuses to honour is
 * worse than offering none. `SCHEDULE_NOT_STARTED` is left out for the same reason — US-13.11 calls
 * it *"a ringing alarm"*, and an alarm with an off switch is a notification.
 *
 * **There is no sharing group**, although §SCR-DA-034 names one: US-10.2 asks for a notification
 * when a passenger requests Mode B access and `NotificationCatalogue` declares no type for it, so
 * nothing raises one and a switch here would silence nothing. Recorded as a C074 spec gap — the same
 * shape as the credit-transfer notification C073 found missing.
 *
 * @property label Trilingual copy for the row.
 * @property types The `notification_type` keys this row switches together.
 */
internal enum class DriverNotificationGroup(@param:StringRes val label: Int, val types: List<String>) {

    /** E-01's offer, and the T-30 reminder that a scheduled ride is about to become one. */
    Dispatch(R.string.profile_notify_dispatch, listOf("RIDE_OFFER", "SCHEDULED_REMINDER")),

    /** D-13's daily fee and D5' §9.4's two balance warnings. */
    Money(
        R.string.profile_notify_money,
        listOf("DAILY_FEE", "LOW_BALANCE", "TOP_UP_REQUIRED", "PAYMENT_CONFIRMED"),
    ),

    /** E-03's expiry warnings and the registration verdicts. */
    Registration(
        R.string.profile_notify_registration,
        listOf(
            "REGISTRATION_APPROVED",
            "REGISTRATION_REVIEW_REQUIRED",
            "DOCUMENT_EXPIRING",
            "DOCUMENT_EXPIRED",
        ),
    ),

    /** DT-04 / US-6A.21 — the Destination Filter running out, and being cleared. */
    Directional(R.string.profile_notify_directional, listOf("DIRECTIONAL_EXPIRING", "DIRECTIONAL_CLEARED")),

    /** US-14.8's announcements. */
    Announcements(R.string.profile_notify_announcements, listOf("BROADCAST")),
    ;

    /**
     * Whether this group is on, given the stored map.
     *
     * **A key that is absent is on.** `iam.users.notif_prefs` starts empty and every type is enabled
     * by default (US-10.7 is an opt-*out*), so treating "not stored" as off would show a driver a
     * screen of switches claiming they had muted everything the moment they first opened it.
     */
    fun isEnabled(preferences: Map<String, Boolean>): Boolean = types.any { preferences[it] != false }

    /** [preferences] with every key in this group set to [enabled]. */
    fun applied(preferences: Map<String, Boolean>, enabled: Boolean): Map<String, Boolean> =
        preferences + types.associateWith { enabled }
}
