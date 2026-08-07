import Combine
import Foundation

/// One notification as notification-svc sends it.
///
/// The shape is C051's `data` dictionary, not a guess: the service writes `kind` and (where the push
/// opens a screen) `deeplink` and `notificationId`, plus per-kind extras — `requestId` and `ttl` on a
/// location request, `rideId` and `deliveryOtp` on a package push.
///
/// Everything is a `String`: an APNs payload is JSON, but notification-svc's `data` block is the
/// same dictionary FCM carries as `Map<String, String>` on Android, and coercing a type here would
/// throw inside a background delivery handler — the one place a crash is invisible.
struct PushMessage: Equatable {

    /// `data.kind`. Lower case for the ones the service special-cases, the SCREAMING type name
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
    /// that are not strings are dropped rather than described — a numeric field arriving as a JSON
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

        /// `data.requestId` on P-02's silent message — `rides.location_requests.request_id`.
        static let requestId = "requestId"

        /// `data.rideId` on a package push — the ride both tracking screens are scoped to.
        static let rideId = "rideId"

        /// `data.deliveryOtp` on `package_picked_up` (US-20.5).
        ///
        /// **The only place this value ever appears.** `RideDetail` has no field for it and no read
        /// returns it, so SCR-PI-021 can show it exactly when the push that carried it was seen by
        /// this process. C099 owns the holder; C081 recorded the same gap on Android.
        static let deliveryOtp = "deliveryOtp"
    }

    /// P-02's silent data message — `{kind:'location_request', requestId, bookerName, ttl:300}`.
    ///
    /// notification-svc's `NotificationCatalogue.LocationRequest`, lower case because D5' §14.4
    /// spells it that way and it is a wire value.
    static let kindLocationRequest = "location_request"

    /// US-6A.15 / US-10.9's reminder before a scheduled pickup (D5' §14.4). SCREAMING, per the
    /// catalogue.
    static let kindScheduledReminder = "SCHEDULED_REMINDER"
}

/// Turns a push into a destination, and hands it to whoever is showing the UI.
///
/// **A deep link is resolved, never trusted.** `deeplink` arrives over the network; mapping it to a
/// known ``PassengerRoute`` rather than handing the raw URI to the navigator means an unrecognised
/// or hostile value opens nothing instead of whatever a future `mageride://` handler might accept.
///
/// A process singleton, because both entry points feed it: the notification delegate while the app
/// is backgrounded, and the launch options when a notification tap starts the app cold. ``pending``
/// keeps its last value for the same reason the Android router replays one — a push that arrives
/// before the shell is on screen is still delivered, and the alternative is a tapped notification
/// that silently does nothing on a cold start.
@MainActor
final class PushRouter: ObservableObject {

    /// The destination a push asked for, if it has not been consumed yet. Read once, by the shell.
    @Published var pending: PassengerRoute?

    /// Offers `message`'s destination. No-op when the push opens nothing.
    func offer(_ message: PushMessage) {
        if let route = PushRouter.route(for: message) { pending = route }
    }

    /// Offers a raw `mageride://…` URI.
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
    // Both are `static` and pure so `PushRouterTests` can assert them without a running app, which
    // is where the Android side puts them too.

    /// The destination `message` should open.
    static func route(for message: PushMessage) -> PassengerRoute? {
        switch message.kind {
        // P-02 / P-13. **The one push on this surface that carries no deeplink at all** —
        // `LocationRequestAsync` in notification-svc writes `{kind, requestId, ttl}` and nothing
        // else, because the screen is a *silent* data message the app renders itself. The route is
        // therefore built from `data.requestId`; there is no URI to resolve, and inventing a
        // `mageride://pickup-confirm/{id}` host would be a client claiming a link the server does
        // not mint.
        case PushMessage.kindLocationRequest:
            guard let requestId = message.data[PushMessage.Keys.requestId], !requestId.isEmpty else { return nil }
            return .confirmPickup(requestId: requestId)

        // US-6A.15 / US-10.9 — "your ride is in 30 minutes". It carries no deeplink either:
        // `DeepLinks` in Notification.Api mints four URIs and none names a scheduled ride, so the
        // routing is on the TYPE, which D5' §14.4 and the catalogue both fix. SCR-PI-022's
        // "Scheduled" tab is where an upcoming ride lives on this side. Same gap the driver app
        // recorded in the C072 handoff; if a `mageride://scheduled` host is ever minted,
        // ``resolve(_:)`` is where it belongs.
        case PushMessage.kindScheduledReminder:
            return .trips

        default:
            return resolve(message.deeplink)
        }
    }

    /// `mageride://ride/{id}` → the ride screen, and so on for the four links notification-svc mints
    /// (`DeepLinks` in its `EventHandlers.cs`; D6' I-23.3 prints the package one).
    ///
    /// **Two of the four open nothing on this surface, and that is correct rather than a gap.**
    /// `mageride://wallet` and `mageride://documents` are the **driver's** — a passenger has no
    /// wallet screen and no documents to expire — and notification-svc never addresses either to a
    /// passenger. Resolving them to "the nearest passenger screen" would open something arbitrary
    /// the first time an operator mis-targeted a broadcast. `PushRouterTests` asserts both are
    /// `nil`.
    ///
    /// Anything else — a scheme that is not ours, a host with no screen, a malformed URI — is `nil`.
    /// Parsed by hand rather than with `URLComponents` so the table is asserted on exactly the
    /// strings the server sends, and so a percent-encoding quirk cannot change what a link opens.
    static func resolve(_ uri: String?) -> PassengerRoute? {
        let value = uri?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard value.hasPrefix(scheme) else { return nil }

        let path = String(value.dropFirst(scheme.count))
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !path.isEmpty else { return nil }

        let segments = path.split(separator: "/").map(String.init)
        let id = segments.count > 1 && !segments[1].isEmpty ? segments[1] : nil

        switch segments[0] {
        case hostRide:
            return id.map { PassengerRoute.activeRide(rideId: $0) }

        // R-01 keeps one ride aggregate, but this side has two screens for a parcel (SCR-PI-020
        // sender, SCR-PI-021 recipient) where the driver side has one. Which of the two to draw is a
        // fact about the ride, so both audiences land on one destination and C099 reads the ride to
        // decide. D6' I-23.3 names SCR-PI-021 as this link's target; the sender gets the same URI on
        // `package_delivered`.
        case hostPackage:
            return id.map { PassengerRoute.packageTracking(rideId: $0) }

        default:
            return nil
        }
    }

    /// D6' I-23.3's scheme.
    static let scheme = "mageride://"

    private static let hostRide = "ride"
    private static let hostPackage = "package"
}
