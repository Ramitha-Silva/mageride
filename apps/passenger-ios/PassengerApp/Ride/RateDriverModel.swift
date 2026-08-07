import Foundation
import MageRideShared

/// SCR-PI-019's reason chips.
///
/// The wireframe's, and they are **compliments rather than complaints**: the screen is shown after a
/// completed ride and its job is to make five stars quick to justify. A report is a different action
/// with a different destination (`POST /v1/vehicles/report`), and mixing the two would put *"unsafe
/// driving"* one tap from *"on time"*.
///
/// **Four, where the cell draws three.** `passenger_ios.html`'s chip row is a wrapping `flex` showing
/// *Clean · On time · Polite*; `apps/passenger-android`'s tag set has a fourth (*Safe driving*), and
/// the set is a **rule** rather than a layout — the row wraps, so the drawing accommodates it.
enum RatingTag: String, CaseIterable, Hashable {
    case clean
    case onTime
    case polite
    case safeDriving

    var labelKey: String {
        switch self {
        case .clean: return "rate_tag_clean"
        case .onTime: return "rate_tag_on_time"
        case .polite: return "rate_tag_polite"
        case .safeDriving: return "rate_tag_safe"
        }
    }
}

/// SCR-PI-019's state.
struct RateDriverState {

    var driverId: String?
    var driverName: String?

    var stars = 0
    var tags: Set<RatingTag> = []
    var comment = ""

    var isSubmitting = false

    /// The rating was **saved locally**, because there is nowhere to send it. See ``RideRatings`` —
    /// this is a contract gap, surfaced honestly rather than hidden behind a spinner.
    var isQueued = false

    var errorKey: String?

    /// US-18.1 is 1–5 stars; the chips and the comment are both optional.
    var canSubmit: Bool { (1...RateDriverState.maximumStars).contains(stars) && !isSubmitting }

    static let maximumStars = 5
}

/// SCR-PI-019 — rating the driver, and the one place this component could not finish.
///
/// **Submit saves rather than sends, and the copy says so**, because there is no contract to send it
/// to: `ride.yaml` declares no rating operation and trip-state-svc's is scoped to a *session*, which
/// a Mode C ride is not. See ``RideRatings`` for the whole argument and the C098 handoff for the
/// route this needs. Telling a passenger their rating was submitted would be telling them something
/// that did not happen.
@MainActor
final class RateDriverModel: ObservableObject {

    @Published private(set) var state = RateDriverState()

    private let rideId: String
    private let rides: RideRepository
    private let ratings: RideRatings

    private var work: [Task<Void, Never>] = []
    private var hasStarted = false

    init(rideId: String, rides: RideRepository, ratings: RideRatings) {
        self.rideId = rideId
        self.rides = rides
        self.ratings = ratings
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Reads the driver's name for the heading. Idempotent.
    func start() {
        guard !hasStarted else { return }
        hasStarted = true
        work.append(Task { await self.loadDriver() })
    }

    func setStars(_ stars: Int) {
        state.stars = min(max(stars, 0), RateDriverState.maximumStars)
        state.errorKey = nil
    }

    func toggle(_ tag: RatingTag) {
        if state.tags.contains(tag) {
            state.tags.remove(tag)
        } else {
            state.tags.insert(tag)
        }
    }

    func onCommentChanged(_ value: String) {
        state.comment = value
    }

    func clearError() {
        state.errorKey = nil
    }

    /// Submit.
    ///
    /// Writes `ratings_pending` and reports it as **saved**. When `POST /v1/rides/{rideId}/rating`
    /// exists this becomes a send with the same row as its retry buffer.
    func submit() {
        guard state.canSubmit else { return }
        state.isSubmitting = true
        state.errorKey = nil

        let driverId = state.driverId
        work.append(Task {
            await ratings.queue(rideId: rideId, driverId: driverId)
            guard !Task.isCancelled else { return }
            state.isSubmitting = false
            state.isQueued = true
        })
    }

    // MARK: -

    /// *"How was your ride?"* without a name is still the right question, so a failed read is not an
    /// error state — it costs the heading its name and nothing else.
    private func loadDriver() async {
        guard let ride = try? await rides.ride(rideId: rideId), !Task.isCancelled else { return }
        state.driverId = ride.driver?.driverId
        state.driverName = ride.driver?.name
    }
}
