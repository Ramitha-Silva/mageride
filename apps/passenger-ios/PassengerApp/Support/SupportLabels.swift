import Foundation
import MageRideShared
// For ``StatusPill/Tone`` alone, the same reason ``TripLabels`` takes it: the status table answers a
// chip's tone as well as its key, so the two stay one function rather than two that can disagree.
import SwiftUI

/// What SCR-PI-030 calls the values support-svc sends back.
///
/// The shape ``RideStateLabel`` and ``SubscriptionLabels`` established: one place a wire enum becomes
/// copy, so a status that reached the default arm is a test failure rather than an English word on a
/// Sinhala screen.
///
/// **`category` is a free-text server key, so it cannot be an exhaustive table.**
/// `support.tickets.category` carries no CHECK and the FAQ surface publishes the list — *"an enum
/// here would have to be revised every time Support adds a topic"* (`SupportModels`). The one key
/// this app raises is its own (``SupportCategories/general``) and has proper trilingual copy;
/// anything else — a ticket fare-svc opened for an AL-47 driver-QR dispute, say — is rendered from
/// the key itself rather than collapsed into *"Support request"*, because a passenger looking at
/// their own ticket list needs to tell two of them apart.
enum SupportLabels {

    /// A category as a passenger reads it.
    ///
    /// A key this build does not know becomes `driver_qr_dispute` → *"Driver qr dispute"*: the
    /// underscores go and the first letter is capitalised. Not a translation, and it is not
    /// pretending to be one — it is the server's own topic name made legible, which is better than a
    /// row that says nothing.
    static func category(_ key: String) -> String {
        switch key {
        case SupportCategories.general: return "support_category_general".localised
        default: return humanise(key)
        }
    }

    /// `TicketStatus` as the wireframe's chip.
    static func statusKey(_ status: TicketStatus) -> String {
        switch status {
        case TicketStatus.open: return "support_status_open"
        case TicketStatus.inProgress: return "support_status_in_progress"
        default: return "support_status_resolved"
        }
    }

    /// The chip's colour — `Open` amber, `In progress` blue, `Resolved` green.
    ///
    /// One table for the card and the thread header alike: the same ticket wearing two colours on
    /// two surfaces is the kind of thing a passenger reads as two different tickets.
    static func tone(_ status: TicketStatus) -> StatusPill.Tone {
        switch status {
        case TicketStatus.open: return .warning
        case TicketStatus.inProgress: return .info
        default: return .ok
        }
    }

    /// What one thread entry is (Δ C053's `TicketEvent`).
    ///
    /// `assigned` is in the enum and is **never returned to a user** — who inside MageRide is
    /// handling a complaint is not the complainant's business — so it has no copy here and the
    /// thread skips it rather than printing an empty row.
    static func eventKey(_ kind: TicketEventKind) -> String? {
        switch kind {
        case TicketEventKind.opened: return "support_event_opened"
        case TicketEventKind.responded: return "support_event_responded"
        case TicketEventKind.resolved: return "support_event_resolved"
        case TicketEventKind.reopened: return "support_event_reopened"
        default: return nil
        }
    }

    /// SCR-PI-030a's **Related trip** row — *"12 Jun · Nugegoda → Galle Face"*.
    ///
    /// The wireframe prints `PAX-90431-0617 · Nugegoda → Galle Face`. **This platform mints no such
    /// identifier**: a ride id is a ULID and no contract carries a human-readable passenger or trip
    /// number (the finding C083, C084 and C101 each recorded about `PAX-90431`). So the row is the
    /// **day and the route**, which is what a passenger recognises a trip by and what a support
    /// agent will search on.
    ///
    /// The date is read in **Colombo** through ``TripLabels`` (D-38): a passenger naming yesterday's
    /// trip to support must name the same day support sees.
    static func trip(_ trip: RideHistoryRow) -> String {
        let route = TripLabels.route(pickup: trip.pickup?.address, dropoff: trip.dropoff?.address)
        return TripLabels.date(trip.completedAt) + MageRideSymbols.separator + route
    }

    /// The accordion's `＋` and `−`.
    ///
    /// Glyphs the wireframe prints, not sentences: they are the same two characters in Sinhala,
    /// Tamil and English, and three identical values in the three `Localizable.strings` files is
    /// what `LocalizationTests` reads as a key nobody translated. Same rule as ``SosLabels/sos``.
    ///
    /// **Nothing draws them today**, and that is the cell's own `Δ iOS` clause: *"`List` +
    /// `DisclosureGroup` FAQ"*, and a `DisclosureGroup`'s chevron is the platform's expression of
    /// the same affordance. They are here because they are the wireframe's characters and because
    /// the day this app draws its own disclosure control it must not invent a third pair — see
    /// ``FaqAccordion``.
    static let expand = "＋"
    static let collapse = "−"

    /// `driver_qr_dispute` → `Driver qr dispute`.
    private static func humanise(_ key: String) -> String {
        let spaced = key.replacingOccurrences(of: "_", with: " ")
        guard let first = spaced.first else { return spaced }
        return String(first).uppercased() + spaced.dropFirst()
    }
}
