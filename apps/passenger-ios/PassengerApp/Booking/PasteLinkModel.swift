import Foundation
import MageRideShared

/// SCR-PI-012a's four states, as one type.
///
/// D2' §SCR-PA-012a and D5' §BR-23.4 name them identically — *"Empty → Parsing ('Reading link…') →
/// Resolved → Error"* — and they are an enum rather than a set of booleans because the sheet shows
/// exactly one at a time and one of them carries data the others do not have.
enum PasteLinkState: Equatable {

    /// Nothing pasted yet. The `UIPasteControl` and the explanatory line.
    case empty

    /// *"Reading link…"* — a short link is being followed by transit-svc.
    case parsing

    /// A point, and what it is called.
    ///
    /// `address` is `nil` while the reverse-geocode is still out: the pin preview and the
    /// coordinates are shown the instant the point is known, because those are the answer and the
    /// address is a courtesy.
    case resolved(lat: Double, lng: Double, address: String?)

    /// *"Couldn't read that link — pick on map"*. Unparseable, or the resolve timed out.
    case failed

    /// What *"Use this location"* commits.
    var place: Place? {
        guard case .resolved(let lat, let lng, let address) = self else { return nil }
        return Place(lat: lat, lng: lng, address: address)
    }
}

/// SCR-PI-012a — a Google Maps link becomes a pin.
///
/// **The device does the parsing and the server only follows redirects** (AL-20, D6' §I-23.1). Which
/// of the two happens is ``MapsLink``'s answer, and it matters: a full URL resolves with no network
/// at all, which is the difference between a sheet that answers instantly and one that waits on a
/// roadside connection for a coordinate that was already in the string.
///
/// **Three seconds, one retry, then the map.** D5' §BR-23.4 pins both numbers. The budget is real: a
/// passenger has already left WhatsApp, copied a link and come back, and a sheet that spins for
/// fifteen seconds before failing has spent more of their time than picking on the map would have.
///
/// Opened from every Paste-link entry — SCR-PI-010b's proxy pickup and SCR-PI-012's package pickup
/// *and* drop-off — which is why it is a model with a `Place` result rather than logic inside any one
/// of those screens.
@MainActor
final class PasteLinkModel: ObservableObject {

    @Published private(set) var state: PasteLinkState = .empty

    private let bookings: BookingRepository
    private var work: Task<Void, Never>?

    init(bookings: BookingRepository) {
        self.bookings = bookings
    }

    deinit {
        work?.cancel()
    }

    /// The sheet was reopened for another field.
    func reset() {
        work?.cancel()
        work = nil
        state = .empty
    }

    /// Something was pasted.
    ///
    /// A full URL never touches the network: a resolved parse short-circuits to the pin and only the
    /// reverse-geocode — which the sheet can live without — goes out.
    func onPasted(_ text: String) {
        work?.cancel()
        switch MapsLink.parse(text) {
        case .resolved(let lat, let lng):
            resolve(lat: lat, lng: lng)
        case .needsServer(let url):
            followShortLink(url)
        case .unreadable:
            state = .failed
        }
    }

    // MARK: -

    /// A short link — transit-svc follows the redirect.
    ///
    /// **One retry, because the failure this guards against is a single dropped request rather than
    /// a service being down.** A second attempt after a 3 s timeout costs a passenger three more
    /// seconds; a third would cost nine, by which point picking on the map is faster and the sheet
    /// says so.
    private func followShortLink(_ url: String) {
        state = .parsing
        work = Task {
            var point = await attemptResolve(url)
            if point == nil, !Task.isCancelled { point = await attemptResolve(url) }
            guard !Task.isCancelled else { return }
            guard let point else {
                state = .failed
                return
            }
            resolve(lat: point.lat, lng: point.lng)
        }
    }

    /// One attempt, on D5' §BR-23.4's own three-second budget.
    ///
    /// The timeout is a race rather than a `withTimeout`, which Swift has no equivalent of: the
    /// resolve and a sleep run together and whichever finishes first wins. Cancelling the group
    /// afterwards is what stops the loser outliving the attempt.
    private func attemptResolve(_ url: String) async -> GeoPoint? {
        await withTaskGroup(of: GeoPoint?.self) { [bookings] group in
            group.addTask { try? await bookings.parseMapsLink(url) }
            group.addTask {
                try? await Task.sleep(nanoseconds: Self.resolveTimeoutNanoseconds)
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
    }

    /// Shows the pin, then names it.
    ///
    /// The address is fetched *after* the state is published rather than before, so the preview
    /// appears as soon as the coordinate is known. A reverse-geocode failure leaves the pin usable
    /// with its coordinates showing, which is what SCR-PI-012a draws underneath the address anyway.
    private func resolve(lat: Double, lng: Double) {
        state = .resolved(lat: lat, lng: lng, address: nil)
        work = Task {
            guard let named = try? await bookings.reverseGeocode(GeoPoint(lat: lat, lng: lng)) else { return }
            guard !Task.isCancelled else { return }
            guard case .resolved(let currentLat, let currentLng, _) = state,
                  currentLat == lat, currentLng == lng
            else {
                return
            }
            state = .resolved(lat: lat, lng: lng, address: named.displayName)
        }
    }

    /// D5' §BR-23.4's own budget for a short-link resolve.
    private static let resolveTimeoutNanoseconds: UInt64 = 3_000_000_000
}
