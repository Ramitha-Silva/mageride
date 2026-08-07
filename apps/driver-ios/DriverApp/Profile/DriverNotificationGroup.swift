import Foundation

/// The notification switches SCR-DI-029 offers, grouped the way D2' §SCR-DI-034 groups the alerts
/// themselves — *"dispatch / fee / registration / sharing / directional"*.
///
/// **The keys are notification-svc's `notification_type` values, verbatim and case-sensitive.**
/// `NotificationPreferences`'s own KDoc says so: *"the keys are push-event names from D3' Part 3, not
/// an enum: the event list grows without a contract change"*. So they are Swift strings here rather
/// than a shared enum, transcribed from `NotificationCatalogue`.
///
/// **A group, not a switch per type.** Fifteen switches is a settings screen nobody reads; five is a
/// decision a driver can make. Toggling one writes `false` for **every** key in it, which is exactly
/// what *"turn off wallet alerts"* means. The server keeps the map, so a key this build has never heard
/// of survives a save untouched — see ``ProfileRepository/saveNotificationPreferences(_:)``.
///
/// **Nothing safety-critical is here.** `SOS_TRIGGERED`, `SOS_RESOLVED` and `RIDE_CANCELLED` are
/// `NotificationCatalogue.SafetyCritical`; iam-svc drops a mute for one on the way in and
/// notification-svc ignores it on the way out. Offering a switch the platform refuses to honour is
/// worse than offering none. `SCHEDULE_NOT_STARTED` is left out for the same reason — US-13.11 calls it
/// *"a ringing alarm"*, and an alarm with an off switch is a notification.
///
/// **There is no sharing group**, although §SCR-DI-034 names one: US-10.2 asks for a notification when
/// a passenger requests Mode B access and `NotificationCatalogue` declares no type for it, so nothing
/// raises one and a switch here would silence nothing. Recorded as a C074 spec gap and carried forward
/// — which is also why SCR-DI-028's queue is **read** on open and after every decision.
///
/// The same table as `apps/driver-android/.../profile/DriverNotificationGroup.kt`, key for key.
enum DriverNotificationGroup: String, CaseIterable, Identifiable {

    /// E-01's offer, and the T-30 reminder that a scheduled ride is about to become one.
    case dispatch

    /// D-13's daily fee and D5' §9.4's two balance warnings.
    case money

    /// E-03's expiry warnings and the registration verdicts.
    case registration

    /// DT-04 / US-6A.21 — the Destination Filter running out, and being cleared.
    case directional

    /// US-14.8's announcements.
    case announcements

    var id: String { rawValue }

    /// Trilingual copy for the row. The same keys as `values*/strings.xml`.
    var labelKey: String { "profile_notify_" + rawValue }

    /// The `notification_type` keys this row switches together.
    var types: [String] {
        switch self {
        case .dispatch: return ["RIDE_OFFER", "SCHEDULED_REMINDER"]
        case .money: return ["DAILY_FEE", "LOW_BALANCE", "TOP_UP_REQUIRED", "PAYMENT_CONFIRMED"]
        case .registration:
            return [
                "REGISTRATION_APPROVED",
                "REGISTRATION_REVIEW_REQUIRED",
                "DOCUMENT_EXPIRING",
                "DOCUMENT_EXPIRED",
            ]
        case .directional: return ["DIRECTIONAL_EXPIRING", "DIRECTIONAL_CLEARED"]
        case .announcements: return ["BROADCAST"]
        }
    }

    /// Whether this group is on, given the stored map.
    ///
    /// **A key that is absent is on.** `iam.users.notif_prefs` starts empty and every type is enabled by
    /// default (US-10.7 is an opt-*out*), so treating *"not stored"* as off would show a driver a screen
    /// of switches claiming they had muted everything the moment they first opened it.
    func isEnabled(in preferences: [String: Bool]) -> Bool {
        types.contains { preferences[$0] != false }
    }

    /// `preferences` with every key in this group set to `isEnabled`.
    func applied(to preferences: [String: Bool], isEnabled: Bool) -> [String: Bool] {
        var updated = preferences
        for type in types { updated[type] = isEnabled }
        return updated
    }
}
