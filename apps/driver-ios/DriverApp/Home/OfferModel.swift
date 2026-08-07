import Foundation
import MageRideShared

/// SCR-DI-014's state.
///
/// - Parameters:
///   - offer: The live offer, or `nil` when the takeover is not up.
///   - detail: The ride behind it, once the enrichment read has landed. `nil` is a real state — the
///     countdown runs from the first frame and the badges appear when they appear.
///   - remaining: What is left of the fifteen seconds, for the ring.
///   - isDeciding: An accept or a decline is in flight.
///   - outcome: How it ended, for the screen to act on once.
struct OfferUiState {

    var offer: RideOffer?
    var detail: RideDetail?
    var remaining: TimeInterval = OfferUiState.ttl
    var isDeciding = false
    var outcome: OfferOutcome?

    /// Whether the full-screen takeover is showing.
    var isLive: Bool { offer != nil }

    /// The last five seconds — the wireframe's *"ring last 5s pulse"*.
    var isUrgent: Bool { remaining <= OfferUiState.urgent }

    /// `1.0` at the moment of the offer down to `0.0` at the deadline.
    ///
    /// Scaled against the **fixed** fifteen-second TTL rather than against whatever is left, which is
    /// what makes a push that took two seconds to arrive show thirteen seconds of ring instead of a
    /// full one. `RideOffer.progress` derives the same figure from the deadline.
    var progress: Double { min(max(remaining / OfferUiState.ttl, 0), 1) }

    /// The whole seconds the ring's middle reads.
    var secondsLeft: Int { max(Int(remaining.rounded(.up)), 0) }

    /// The fare the takeover leads with — the ride's once it has been read, the push's until then.
    var fareMinor: Int64 { detail?.fare?.amountMinor ?? offer?.fareEstimateMinor ?? 0 }

    /// **The offer window** (US-6A.3, D5' §3.5).
    ///
    /// `RideOffer.TTL` is a `kotlin.time.Duration`, which the Objective-C export flattens to an
    /// opaque nanosecond `Int64`; fifteen seconds is spelled here rather than unpacked from it, and
    /// `OfferModelTests` is what holds the two together.
    static let ttl: TimeInterval = 15

    /// D2' §SCR-DI-014: *"counting-down (ring shrinks, last 5s pulse red)"*.
    static let urgent: TimeInterval = 5
}

/// **SCR-DI-014 · the fifteen-second offer** (US-6A.2/6A.3, R-02, E-01).
///
/// The decision itself is `:shared`'s — `OfferSession` holds the driver's single slot, refuses to send
/// an accept whose deadline has already gone, and keeps `409 offer-already-accepted`
/// (`OfferOutcome.Taken`) apart from `410 offer-expired` (`OfferOutcome.Expired`) all the way out.
/// What this model adds is the three things a screen needs and a domain object should not have:
///
/// * **The countdown drives the ring**, and reaching zero **returns the driver to standby**.
/// * **The enrichment read.** `offer.created` carries an id, a deadline and a rendered fare (see
///   ``OfferInbox``); the proxy badge, the package size, the pickup and the drop are on the ride. One
///   `GET /v1/rides/{rideId}` inside the window, and its `version` is handed straight to
///   `OfferSession.onVersionKnown` so the accept does not have to spend a second round trip on
///   `GET …/state` (R-14).
/// * **The outcome is consumed once.** Winning navigates to SCR-DI-015; every other ending puts the
///   driver back on the standby map. A rotation must not do either twice.
///
/// **The countdown is local arithmetic and not `OfferSession.countdown()`**, which is a `Flow<Duration>`
/// — a type Swift cannot collect and whose element is an inline value class the export flattens to an
/// opaque `Int64`. It is not a second rule: that flow derives what is left from `expiresAt` against
/// the wall clock and so does this, which is why a push delayed in transit shows a short ring on both
/// platforms. The **decision** rule — an offer whose deadline has passed is never sent — stays in
/// `OfferSession.accept()` where both apps share it.
@MainActor
final class OfferModel: ObservableObject {

    @Published private(set) var state = OfferUiState()

    private let slot: OfferSlot
    private let rides: ActiveRideRepository
    private let tick: TimeInterval

    private var isObserving = false
    private var countdown: Task<Void, Never>?
    private var enrichment: Task<Void, Never>?

    /// - Parameter tick: How often the ring is re-rendered. A parameter so a test can run a countdown
    ///   to its end without spending fifteen real seconds on it.
    init(slot: OfferSlot, rides: ActiveRideRepository, tick: TimeInterval = OfferModel.countdownInterval) {
        self.slot = slot
        self.rides = rides
        self.tick = tick
    }

    deinit {
        countdown?.cancel()
        enrichment?.cancel()
    }

    /// Subscribes to the slot. Cancel with ``stop()`` — nothing here is tied to a view's lifetime.
    func start() {
        guard !isObserving else { return }
        isObserving = true
        slot.observe { [weak self] state in
            self?.onSlotState(state)
        }
    }

    func stop() {
        slot.stopObserving()
        isObserving = false
        countdown?.cancel()
        countdown = nil
        enrichment?.cancel()
        enrichment = nil
    }

    /// The wireframe's **Accept** — the atomic single-winner accept (R-02).
    func accept() async {
        state.isDeciding = true
        state.outcome = await slot.accept()
        state.isDeciding = false
    }

    /// The wireframe's **Reject**. No penalty (D5' §7); dispatch cascades to the next candidate.
    func reject() async {
        state.isDeciding = true
        state.outcome = await slot.decline()
        state.isDeciding = false
    }

    /// Clears the ending once the screen has navigated or dismissed on it.
    func consumeOutcome() {
        state.outcome = nil
    }

    private func onSlotState(_ slotState: OfferSlotState) {
        switch slotState {
        case .live(let offer):
            onLive(offer)

        case .deciding:
            state.isDeciding = true

        // Won is reported through the accept's own outcome; the slot going Idle after a decline, an
        // expiry or a loss is what takes the takeover down.
        case .idle, .won:
            countdown?.cancel()
            countdown = nil
            state.offer = nil
            state.detail = nil
            state.isDeciding = false
        }
    }

    private func onLive(_ offer: RideOffer) {
        let alreadyShowing = state.offer?.offerId == offer.offerId
        state.offer = offer
        if !alreadyShowing { state.detail = nil }
        guard !alreadyShowing else { return }

        enrich(offer)
        countdown?.cancel()
        countdown = Task { [weak self] in
            await self?.runCountdown(for: offer)
        }
    }

    /// Ticks the ring until the deadline, then frees the slot.
    ///
    /// The server has already released the driver at that point, so declining would be a `410` for
    /// nothing — the slot is dropped locally and the screen falls back to the standby map.
    private func runCountdown(for offer: RideOffer) async {
        let deadline = Date(
            timeIntervalSince1970: TimeInterval(IosInstantKt.timestampEpochMillis(instant: offer.expiresAt)) / 1000
        )

        while !Task.isCancelled {
            let left = deadline.timeIntervalSinceNow
            state.remaining = max(left, 0)
            if left <= 0 { break }
            try? await Task.sleep(nanoseconds: UInt64(min(tick, left) * 1_000_000_000))
        }

        guard !Task.isCancelled, slot.liveOfferId == offer.offerId else { return }
        slot.expire()
        state.outcome = OfferOutcomeExpired.shared
    }

    /// Reads the ride behind the offer, and tells the session the version it found.
    ///
    /// Failure is silent on purpose: a driver has fifteen seconds, and an offer they can still accept
    /// with no badges is worth more than an error over a countdown. The accept falls back to
    /// `GET /v1/rides/{rideId}/state` for its version, which is what `OfferSession` does with no
    /// version in hand.
    private func enrich(_ offer: RideOffer) {
        enrichment?.cancel()
        enrichment = Task { [weak self] in
            guard let self, let detail = try? await self.rides.detail(rideId: offer.rideId) else { return }
            guard self.state.offer?.offerId == offer.offerId else { return }
            self.slot.onVersionKnown(detail.version)
            self.state.detail = detail
        }
    }

    /// Fine enough that a fifteen-second ring does not visibly step.
    static let countdownInterval: TimeInterval = 0.25
}
