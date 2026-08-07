import Foundation
import MageRideShared

/// SCR-PI-023's state.
struct TripDetailsState {

    var trip: TripDetail?

    /// The Kalman-filtered track (E-04), decoded — the **same** one the fare was computed from, so
    /// the map and the receipt agree about the distance.
    var route: [GeoPoint] = []

    var isLoading = true
    var errorKey: String?

    /// Whether the polyline is the 1/min operational sample rather than full telemetry, in which case
    /// `distanceKm` is a **lower bound** and the screen must not present it as exact.
    var isApproximate: Bool {
        guard let source = trip?.geometrySource else { return false }
        return source == GeometrySource.operational
    }

    /// `17 Jun, 08:32` — when the trip started, in Colombo.
    var startedAt: String? {
        trip.map { TripLabels.dateTime($0.startedAt) }
    }

    /// The plate.
    ///
    /// **The wireframe draws `Sedan · ABC-1234` and the type half cannot be drawn**: `TripDriver`
    /// carries `driverId`, `name` and `registrationNumber` and nothing else, and `TripDetail`'s own
    /// `mode` is the *service* mode (A/B/C) rather than a vehicle type. The plate alone is what the
    /// contract answers; a type guessed from the mode would be a claim about a vehicle nobody sent.
    /// Recorded in the C099 handoff.
    var vehicle: String? { trip?.driver?.registrationNumber }

    /// Who drove.
    ///
    /// **No star follows it**, where the wireframe draws `K. Fernando ★4.8`. `TripDriver` has no
    /// rating, and `TripDetail.rating` is the rating *this user left for this trip* — joined on
    /// `rater_id`, which is what C092 established from the driver side. Printing that beside the
    /// driver's name would state the passenger's own score as the driver's average.
    var driver: String? { trip?.driver?.name }

    /// `8.2 km`, from the figure the fare was computed from — never a sum over the decoded polyline.
    ///
    /// Two numbers on one receipt is an argument waiting to happen: `TripDetail.distanceKm` is what
    /// fare-svc charged against, and re-measuring here would produce a second figure that disagreed
    /// the first time a rounding rule changed server-side.
    var distance: String? {
        guard let kilometres = trip?.distanceKm?.doubleValue else { return nil }
        return MapFormat.distance(metres: kilometres * TripDetailsState.metresInKilometre)
    }

    /// What the trip cost. `nil` on a Mode A/B journey, which has no fare at all.
    var total: String? {
        trip?.fareMinor.map { MoneyFormat.rupees($0.int64Value) }
    }

    private static let metresInKilometre: Double = 1_000
}

/// SCR-PI-023 — one trip, its route and its receipt.
///
/// **query-svc rather than ride-svc**, because this is the only read that carries the polyline and
/// the filtered distance — and because a Mode A bus journey has a trip detail and no ride at all.
/// `GET /v1/trips/{userId}/{tripId}` spans all three modes, which is what makes one details screen
/// possible.
///
/// **Signed out, there is nothing to read.** The path is `{userId}/{tripId}` and the id is the
/// session's; a screen reached with no session loads nothing rather than guessing one.
@MainActor
final class TripDetailsModel: ObservableObject {

    @Published private(set) var state = TripDetailsState()

    private let tripId: String
    private let history: HistoryRepository
    private let sessions: PassengerSessions

    init(tripId: String, history: HistoryRepository, sessions: PassengerSessions) {
        self.tripId = tripId
        self.history = history
        self.sessions = sessions
    }

    func load() async {
        guard let userId = sessions.userId else {
            state.isLoading = false
            return
        }

        state.isLoading = true
        state.errorKey = nil
        do {
            let trip = try await history.trip(userId: userId, tripId: tripId)
            guard !Task.isCancelled else { return }
            state.trip = trip
            state.route = EncodedPolyline.decode(trip.polyline)
            state.isLoading = false
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = RideErrors.messageKey(for: error)
        }
    }

    func clearError() {
        state.errorKey = nil
    }
}
