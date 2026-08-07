import Foundation
import MageRideShared

/// What SCR-DI-033 calls the values support-svc sends back.
///
/// **`category` is a free-text server key, so it cannot be an exhaustive table.**
/// `support.tickets.category` carries no CHECK and the FAQ surface publishes the list — *"an enum
/// here would have to be revised every time Support adds a topic"* (`SupportModels`). Two keys are
/// the app's own (``SupportCategories``) and have proper trilingual copy; anything else is rendered
/// from the key itself rather than collapsed into *"Support request"*, because a driver looking at
/// their own ticket list needs to tell two of them apart.
enum SupportLabels {

    /// The string key for a category this app knows, or `nil` for one the server added.
    static func categoryKey(_ key: String) -> String? {
        switch key {
        case SupportCategories.dailyFeeRefund: return "support_category_refund"
        case SupportCategories.general: return "support_category_general"
        default: return nil
        }
    }

    /// A category as a driver reads it.
    ///
    /// A key this build does not know becomes `fare_dispute` → *"Fare dispute"*: the underscores go
    /// and the first letter is capitalised. Not a translation, and it is not pretending to be one —
    /// it is the server's own topic name made legible, which is better than a row that says nothing.
    static func category(_ key: String) -> String {
        if let resolved = categoryKey(key) { return resolved.localised }
        let spaced = key.replacingOccurrences(of: "_", with: " ")
        guard let first = spaced.first else { return spaced }
        return String(first).uppercased() + spaced.dropFirst()
    }

    /// `TicketStatus` as the wireframe's chip.
    static func statusKey(_ status: TicketStatus) -> String {
        switch status {
        case TicketStatus.open: return "support_status_open"
        case TicketStatus.inProgress: return "support_status_in_progress"
        default: return "support_status_resolved"
        }
    }

    /// The tone the status chip wears — `Open` amber, `Being looked at` blue, `Resolved` green.
    ///
    /// One table for the list row and the thread header alike: the same ticket wearing two colours
    /// on two surfaces is the kind of thing a driver reads as two different tickets.
    static func tone(_ status: TicketStatus) -> StatusTone {
        switch status {
        case TicketStatus.open: return .pending
        case TicketStatus.inProgress: return .info
        default: return .done
        }
    }

    /// What one thread entry is (Δ C053's `TicketEvent`).
    ///
    /// `assigned` is in the enum and is **never returned to a user** — who is handling a complaint is
    /// not theirs — so it has no copy and the thread skips it rather than printing an empty row.
    static func eventKey(_ kind: TicketEventKind) -> String? {
        switch kind {
        case TicketEventKind.opened: return "support_event_opened"
        case TicketEventKind.responded: return "support_event_responded"
        case TicketEventKind.resolved: return "support_event_resolved"
        case TicketEventKind.reopened: return "support_event_reopened"
        default: return nil
        }
    }

    /// *"12 Jun · Galle Face → Nugegoda"* — one row of the **Related trip** picker.
    ///
    /// The wireframe prints `DRV-22011-0617`, a driver-and-date composite this platform does not mint
    /// (the same finding C092 recorded about `DRV-22011`), so the row is the **route and the day** —
    /// which is what a driver recognises a trip by and what the support agent will search on.
    ///
    /// The date is read in **Colombo** through C090's ``ScheduleLabels``, not in the handset's zone
    /// (D-38): a driver naming yesterday's trip to support must name the same day support sees.
    static func trip(_ trip: TripSummary) -> String {
        let places = [trip.pickup?.address, trip.dropoff?.address]
            .compactMap { $0 }
            .joined(separator: MageRideSymbols.routeArrow)
        let route = places.isEmpty ? "support_trip_unnamed".localised : places
        return ScheduleLabels.date(trip.startedAt) + MageRideSymbols.separator + route
    }
}
