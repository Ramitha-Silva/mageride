import Combine
import Foundation

/// One notification as notification-svc sends it.
///
/// The shape is C051's `data` dictionary, not a guess: the service writes `kind`, `deeplink` and
/// `notificationId` on every push it produces, plus per-kind extras (`rideId`, `fare`, `distance`
/// on an offer; `requestId`, `ttl` on a location request).
///
/// Everything is a `String`: an APNs payload is JSON, but notification-svc's `data` block is the
/// same dictionary FCM carries as `Map<String, String>` on Android, and coercing a type here would
/// throw inside a background delivery handler — the one place a crash is invisible.
struct PushMessage: Equatable {

    /// `data.kind`. Lower-case for the ones the service special-cases, the SCREAMING type name
    /// otherwise.
    let kind: String?

    /// `data.deeplink` — a `mageride://…` URI, absent on kinds that open nothing.
    let deeplink: String?

    /// `data.notificationId`, the id `POST /v1/notify/ack` takes.
    let notificationId: String?

    /// The whole dictionary, for a screen that needs a per-kind extra.
    let data: [String: String]

    /// Reads one off an APNs `userInfo`.
    ///
    /// The keys are read from the top level, which is where notification-svc puts them: APNs has no
    /// `data` envelope of its own, so a custom key sits beside `aps` rather than inside it. Values
    /// that are not strings are dropped rather than described — a numeric `fare` arriving as a JSON
    /// number is a contract question, not something to stringify here and lose.
    static func from(userInfo: [AnyHashable: Any]) -> PushMessage {
        var data: [String: String] = [:]
        for (key, value) in userInfo {
            guard let key = key as? String, let value = value as? String else { continue }
            data[key] = value
        }
        return PushMessage(
            kind: data[Keys.kind],
            deeplink: data[Keys.deeplink],
            notificationId: data[Keys.notificationId],
            data: data
        )
    }

    enum Keys {
        static let kind = "kind"
        static let deeplink = "deeplink"
        static let notificationId = "notificationId"
    }

    /// E-01's 15 s atomic offer. The only kind that takes over the screen.
    static let kindRideOffer = "ride_offer"

    /// US-6A.15's 30-minute reminder before a scheduled pickup (D5' §14.4).
    ///
    /// The SCREAMING form, because notification-svc's catalogue spells it that way
    /// (`NotificationCatalogue.ScheduledReminder`) and `kind` carries the type name for every push
    /// the service does not special-case.
    static let kindScheduledReminder = "SCHEDULED_REMINDER"
}

/// Turns a push into a destination, and hands it to whoever is showing the UI.
///
/// **A deep link is resolved, never trusted.** `deeplink` arrives over the network; mapping it to a
/// known ``DriverRoute`` rather than handing the raw URI to the navigator means an unrecognised or
/// hostile value opens nothing instead of whatever a future `mageride://` handler might accept.
///
/// A process singleton, because both entry points feed it: the notification delegate while the app
/// is backgrounded, and the launch options when a notification tap starts the app cold. ``pending``
/// keeps its last value for the same reason the Android router replays one — a push that arrives
/// before the shell is on screen is still delivered, and the alternative is a tapped notification
/// that silently does nothing on a cold start.
@MainActor
final class PushRouter: ObservableObject {

    /// The destination a push asked for, if it has not been consumed yet. Read once, by the shell.
    ///
    /// Not `private(set)`: the shell subscribes to `$pending`, and a projected value behind a
    /// private setter is the kind of access-control subtlety that changes between Swift releases.
    /// Nothing else writes it — ``offer(_:)`` and ``consume()`` are the whole surface.
    @Published var pending: DriverRoute?

    /// Offers [message]'s destination. No-op when the push opens nothing.
    func offer(_ message: PushMessage) {
        if let route = PushRouter.route(for: message) { pending = route }
    }

    /// Offers a raw `mageride://…` URI — the notification-tap path, where only the link survives.
    func offer(uri: String?) {
        if let route = PushRouter.resolve(uri) { pending = route }
    }

    /// Forgets the pending destination once the shell has navigated, so a scene change does not
    /// navigate a second time.
    func consume() {
        pending = nil
    }

    // MARK: - The tables
    //
    // Both are `static` and pure so `PushRouterTests` can assert them without a running app,
    // which is where the Android side puts them too.

    /// The destination [message] should open.
    static func route(for message: PushMessage) -> DriverRoute? {
        switch message.kind {
        // The offer sheet is not a deep link — it is a takeover the dashboard owns (C088), and it
        // must open even though `offer.created` carries a *ride* deeplink minted for the passenger
        // side. Routing it to Home is what puts the driver where the sheet shows.
        case PushMessage.kindRideOffer:
            return .home

        // US-6A.15 — D2' §SCR-DI-018: "30-min reminder push deep-links here". It carries no
        // deeplink to follow: `DeepLinks` in Notification.Api mints four URIs and none of them
        // names a scheduled ride, so the routing is on the **type**, which D5' §14.4 and
        // notification-svc's catalogue both fix. If a `mageride://scheduled` host is ever minted,
        // ``resolve(_:)`` is where it belongs.
        case PushMessage.kindScheduledReminder:
            return .scheduledRides

        default:
            return resolve(message.deeplink)
        }
    }

    /// `mageride://ride/{id}` → the active-ride screen, and so on for the four links
    /// notification-svc mints (`DeepLinks` in its `EventHandlers.cs`; C051 note (n)).
    ///
    /// Anything else — a scheme that is not ours, a host with no screen, a malformed URI — is
    /// `nil`. Parsed by hand rather than with `URLComponents` so the table is asserted on exactly
    /// the strings the server sends, and so a percent-encoding quirk cannot change what a link
    /// opens.
    static func resolve(_ uri: String?) -> DriverRoute? {
        let value = uri?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard value.hasPrefix(scheme) else { return nil }

        let path = String(value.dropFirst(scheme.count))
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !path.isEmpty else { return nil }

        let segments = path.split(separator: "/").map(String.init)
        let id = segments.count > 1 && !segments[1].isEmpty ? segments[1] : nil

        switch segments[0] {
        case hostRide, hostPackage:
            // A package delivery IS a ride (R-01 keeps one aggregate), so both links land on the
            // same screen. The distinction the passenger app makes — SCR-PA-021 — has no
            // driver-side counterpart.
            return id.map { DriverRoute.activeRide(rideId: $0) }
        case hostWallet:
            return .wallet
        case hostDocuments:
            return .documents
        default:
            return nil
        }
    }

    /// D6' I-23.3's scheme.
    static let scheme = "mageride://"

    private static let hostRide = "ride"
    private static let hostPackage = "package"
    private static let hostWallet = "wallet"
    private static let hostDocuments = "documents"
}
