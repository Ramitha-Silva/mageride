import Foundation
import MageRideShared

/// SCR-PI-018's state.
struct TripSummaryState {

    var ride: RideDetail?

    /// Whether the money is done. `false` puts a *"Pay now"* CTA on the screen.
    var isSettled = false

    /// Whether this handset has already rated the ride — see ``RideRatings``.
    var isRated = false

    var errorKey: String?

    /// The total, as the hero line.
    var totalMinor: Int64? { ride?.fare?.amountMinor }

    /// Whether the cell's itemised breakdown can be drawn at all.
    ///
    /// **It cannot, and that is a contract gap rather than an unfinished screen.** The wireframe
    /// draws *Distance · First km · Per km × 7.2 · Total*, and `FareBreakdown` — which carries
    /// exactly those four numbers — exists on **`GET /v1/fare/estimate` only**. `RideDetail.fare` is
    /// a `FareEstimate` with `amountMinor`, `currency` and `surchargeMinor` and nothing else; no
    /// operation on the app-facing surface returns the parts of a *settled* fare. So the receipt
    /// shows the total it was actually charged and says the itemisation is unavailable, rather than
    /// re-deriving four numbers from a quote that is not what the passenger paid.
    ///
    /// `apps/passenger-android`'s `TripSummaryState.breakdown` is a field **nothing ever assigns** —
    /// the same hole, reached silently. Recorded in the C098 handoff.
    var hasBreakdown: Bool { false }

    /// Whether SCR-PI-019 should be offered — a settled ride with a driver nobody has rated yet.
    var canRate: Bool { ride?.driver != nil && !isRated }
}

/// SCR-PI-018 — the receipt.
///
/// **No tip control, and that is the wireframe's own instruction**: SCR-PI-016's state line reads
/// *"(drop tip / India charges)"*. `InitiatePaymentRequest.tipMinor` exists and E-10 defines the
/// journal kind, so the contract is ready whenever the product decision reverses — but a tip row
/// nobody asked for on a receipt is a charge a passenger has to notice and decline.
///
/// **The breakdown is not re-derived here.** `FareEstimate` carries the total and `FareBreakdown`
/// carries the parts; multiplying a per-km rate by a distance on the device would produce a second,
/// disagreeing number the moment a rounding rule changed server-side (R-05). That is also why the
/// *"Per km × 7.2"* the cell draws is rendered as the **rate** rather than as a computed line — see
/// ``TripSummaryScreen``.
@MainActor
final class TripSummaryModel: ObservableObject {

    @Published private(set) var state = TripSummaryState()

    private let rideId: String
    private let rides: RideRepository
    private let ratings: RideRatings

    private var work: [Task<Void, Never>] = []

    init(rideId: String, rides: RideRepository, ratings: RideRatings) {
        self.rideId = rideId
        self.rides = rides
        self.ratings = ratings
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Reads the ride and the local rating queue.
    ///
    /// **Not idempotent on purpose** — the screen calls this on every appear, because SCR-PI-019 pops
    /// back here and *"Rate your driver"* has to stop being offered once it has been answered.
    func load() {
        work.append(Task {
            do {
                let ride = try await rides.ride(rideId: rideId)
                guard !Task.isCancelled else { return }
                state.ride = ride
                state.isSettled = ride.state.isSettled
                state.errorKey = nil
            } catch is CancellationError {
                return
            } catch {
                state.errorKey = RideErrors.messageKey(for: error)
            }
            let rated = await ratings.isRated(rideId: rideId)
            guard !Task.isCancelled else { return }
            state.isRated = rated
        })
    }

    func clearError() {
        state.errorKey = nil
    }
}
