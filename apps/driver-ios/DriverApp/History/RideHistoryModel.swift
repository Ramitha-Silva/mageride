import Foundation
import MageRideShared

/// One row of SCR-DI-030's list.
///
/// - Parameters:
///   - summary: What `GET /v1/trips/{driverId}` answered — route, fare and when.
///   - distanceKm: From the trip detail; `nil` when the read has not landed or the geometry source
///     does not carry one.
///   - rating: The stars **this driver** left on this trip, or `nil` for one not yet rated.
struct HistoryTrip: Identifiable {

    let summary: TripSummary
    var distanceKm: Double?
    var rating: Int?

    /// The trip id, which is a `rides.rides` id or a `trips.sessions` id depending on the plane.
    var id: String { summary.tripId }

    /// Whether the wireframe's *"Rate ★"* link is drawn.
    ///
    /// A **Mode C ride only**, and only once. A Mode A/B session is a vehicle's journey rather than one
    /// person's trip — it has no single passenger to rate, and `DriverRatingInput` requires one — so no
    /// link is drawn on it. That is not a gap; it is what a bus journey is.
    var isRateable: Bool { summary.plane == TripPlane.ride && rating == nil }

    /// The wireframe's `Galle Face → Nugegoda`.
    ///
    /// `Place.address` is server-supplied display text and query-svc's trip list sends bare coordinates
    /// with no address at all — the same absence ``ScheduleLabels/route(pickup:dropoff:)`` documents for
    /// a scheduled ride — so a trip with no address prints the dash rather than two decimal degrees no
    /// driver can read.
    var routeText: String {
        (summary.pickup?.address ?? MageRideSymbols.unknown)
            + MageRideSymbols.routeArrow
            + (summary.dropoff?.address ?? MageRideSymbols.unknown)
    }

    /// *"17 Jun · 8 km · fare to you Rs 480"* — assembled from what actually arrived.
    ///
    /// Each part is dropped rather than faked when its source is absent: `distanceKm` is missing while
    /// the detail read is in flight and permanently on the `aggregate_1m` source, and a fare is
    /// absent on a Mode A/B session, which has no fare at all.
    var captionText: String {
        var parts = [ScheduleLabels.date(summary.startedAt)]
        if let distanceKm {
            parts.append(MoneyFormat.distance(metres: distanceKm * Self.metresInKm))
        }
        if let fareMinor = summary.fareMinor {
            parts.append("history_fare_to_you".localisedFormat(MoneyFormat.rupees(fareMinor.int64Value)))
        }
        return parts.joined(separator: MageRideSymbols.separator)
    }

    /// `TripDetail.distanceKm` is kilometres and ``MoneyFormat/distance(metres:)`` takes metres.
    private static let metresInKm: Double = 1_000
}

/// The rate-passenger sheet's own state (AL-35: *"a modal bottom sheet, not an inline card"*).
///
/// - Parameters:
///   - trip: The row the sheet was opened from.
///   - passengerId: Who is being rated; `nil` on a proxy booking for an unregistered rider (P-01),
///     which is the one completed ride nobody can be rated for.
///   - passengerName: Their name, when the ride carried one.
///   - stars: 1–5. Five to start, which is the wireframe's five filled stars.
///   - comment: US-18.2's optional free text.
///   - isLoading: The `GET /v1/rides/{rideId}` that names the passenger is in flight.
///   - isSubmitting: The rating POST is in flight.
struct RatePassengerState: Identifiable {

    let trip: HistoryTrip
    var passengerId: String?
    var passengerName: String?
    var stars: Int = RatePassengerState.maxStars
    var comment = ""
    var isLoading = true
    var isSubmitting = false

    var id: String { trip.id }

    /// Whether **Submit rating** is live.
    var canSubmit: Bool {
        !isLoading && !isSubmitting && passengerId != nil && Self.range.contains(stars)
    }

    /// `trip-state.yaml#/components/schemas/RatingInput` — `stars` is `1..5`.
    static let minStars = 1
    static let maxStars = 5
    static let range = minStars...maxStars
}

/// SCR-DI-030's state.
///
/// - Parameters:
///   - trips: The driver's completed journeys, newest first.
///   - rating: The open rate-passenger sheet, or `nil`.
///   - isLoading: The list read is in flight.
///   - errorKey: Resolved copy for the last failure.
struct RideHistoryState {

    var trips: [HistoryTrip] = []
    var rating: RatePassengerState?
    var isLoading = true
    var errorKey: String?

    /// Whether the driver has driven nothing at all yet.
    var isEmpty: Bool { !isLoading && trips.isEmpty }
}

/// **SCR-DI-030 · ride history, trip details and the rate-passenger sheet** (US-8.8, US-18.2, AL-35).
///
/// **The list is one read and the rows are one more each, in parallel.** `GET /v1/trips/{driverId}`
/// answers a page of summaries carrying route, fare and date; the wireframe's row also prints *"8 km"*
/// and *"rated ★5"*, and neither is on a summary — `TripSummary` has no distance and no rating, by the
/// contract. So the detail of every row on the page is fetched concurrently, through a
/// `TaskGroup`, and folded in as it lands. It is a fan-out and it is bounded: one page is
/// `PageRequest.DEFAULT_LIMIT` = 20 cacheable GETs against a read model, issued once when the screen
/// opens. The alternative — fetching a detail only when a row is opened — would leave the *"Rate ★"*
/// link drawn on a trip the driver had already rated, and a second tap on it would write a second
/// `trips.ratings` row.
///
/// **A failed detail is not a failed screen.** Each is read best-effort: a row whose detail did not come
/// back keeps its summary and simply shows no distance. Losing the whole history because one trip's
/// polyline query timed out would be the wrong trade on a roadside connection.
@MainActor
final class RideHistoryModel: ObservableObject {

    @Published private(set) var state = RideHistoryState()

    private let identity: DriverIdentity
    private let history: RideHistoryRepository

    init(identity: DriverIdentity, history: RideHistoryRepository) {
        self.identity = identity
        self.history = history
    }

    /// Re-reads the trip list, then every row's detail.
    func refresh() async {
        guard let driverId = identity.driverId else {
            state.isLoading = false
            return
        }

        state.isLoading = true
        state.errorKey = nil
        do {
            let summaries = try await history.trips(driverId: driverId)
            state.trips = summaries.map { HistoryTrip(summary: $0) }
            state.isLoading = false
            await enrich(driverId: driverId, summaries: summaries)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
            state.isLoading = false
        }
    }

    /// The wireframe's **Rate ★** on a completed trip — opens the sheet (AL-35).
    ///
    /// The passenger is named by a second read rather than carried on the row, because a trip summary
    /// has no passenger on it at all: `GET /v1/trips/{userId}` is the same shape for both parties to a
    /// ride and never says who the other one was.
    func openRating(tripId: String) async {
        guard let trip = state.trips.first(where: { $0.id == tripId }), trip.isRateable else { return }

        state.rating = RatePassengerState(trip: trip)
        state.errorKey = nil
        do {
            let ride = try await history.rideParties(rideId: tripId)
            guard state.rating?.trip.id == tripId else { return }
            state.rating?.isLoading = false
            state.rating?.passengerId = ride.riderId
            state.rating?.passengerName = ride.riderName
        } catch {
            state.rating = nil
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// One of the five stars.
    func onStarsChange(_ stars: Int) {
        state.rating?.stars = min(max(stars, RatePassengerState.minStars), RatePassengerState.maxStars)
    }

    /// The optional comment.
    func onCommentChange(_ raw: String) {
        state.rating?.comment = raw
    }

    /// Closes the sheet without rating.
    func dismissRating() {
        state.rating = nil
    }

    /// **Submit rating** — US-18.2.
    ///
    /// On success the row becomes *"rated ★N"* locally rather than through a re-read: the trip is
    /// finished and nothing else about it can have changed, and re-reading a page of twenty trips to
    /// learn one number the response already carried would be a round trip for nothing.
    func submitRating() async {
        guard let sheet = state.rating, sheet.canSubmit, let passengerId = sheet.passengerId else { return }

        state.rating?.isSubmitting = true
        state.errorKey = nil
        do {
            let recorded = try await history.ratePassenger(
                subjectId: sheet.trip.id,
                passengerId: passengerId,
                stars: sheet.stars,
                comment: sheet.comment
            )
            state.rating = nil
            if let index = state.trips.firstIndex(where: { $0.id == sheet.trip.id }) {
                state.trips[index].rating = Int(recorded.stars)
            }
        } catch {
            state.rating?.isSubmitting = false
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// Reads every listed trip's detail concurrently and folds the two extra facts in.
    private func enrich(driverId: String, summaries: [TripSummary]) async {
        let details = await withTaskGroup(
            of: (String, (distanceKm: Double?, rating: Int?)?).self
        ) { group -> [String: (distanceKm: Double?, rating: Int?)] in
            for summary in summaries {
                group.addTask { [history] in
                    guard let detail = try? await history.detail(driverId: driverId, tripId: summary.tripId) else {
                        // One dead detail must not take the list down with it.
                        return (summary.tripId, nil)
                    }
                    return (
                        summary.tripId,
                        (detail.distanceKm?.doubleValue, detail.rating.map { Int($0.int32Value) })
                    )
                }
            }

            var folded: [String: (distanceKm: Double?, rating: Int?)] = [:]
            for await (tripId, detail) in group {
                if let detail { folded[tripId] = detail }
            }
            return folded
        }

        for index in state.trips.indices {
            guard let detail = details[state.trips[index].id] else { continue }
            state.trips[index].distanceKm = detail.distanceKm
            state.trips[index].rating = detail.rating
        }
    }
}
