import Foundation
import MageRideShared

/// Where the driver's single offer slot is, as SCR-DI-014 needs to see it.
///
/// A Swift mirror of `OfferSessionState` rather than the Kotlin type itself: a sealed interface
/// crosses the bridge as an Objective-C **protocol**, and pattern-matching one in Swift is a chain of
/// `as?` casts on names the compiler generates. Four cases with an associated value where there is one
/// is what a `switch` in a view model wants.
enum OfferSlotState {

    /// No offer in hand. The driver is available and dispatch may reserve them.
    case idle

    /// An offer is live and the countdown is running.
    case live(RideOffer)

    /// The accept or decline is in flight.
    case deciding

    /// The driver won it and now holds a ride.
    case won
}

/// The driver's single offer slot (ADD Appendix B.2 invariant 3).
///
/// **A protocol for two reasons, and the second is the decisive one.** `OfferSession` is a Kotlin class
/// and cannot be stood in for from Swift; and its state arrives as a `StateFlow`, which Swift reaches
/// only through `IosFlowWatcher` — whose constructor is `internal`, so a test cannot build one at all.
/// Injecting the watcher into ``OfferModel`` would make the fifteen-second countdown, the enrichment
/// read and the outcome handling untestable, which is most of what C088 has to get right.
///
/// Nothing behind this protocol decides anything. `OfferSession` still holds the slot, still refuses an
/// accept whose deadline has gone, and still keeps `409 offer-already-accepted` apart from
/// `410 offer-expired`; this is the shape those answers arrive in.
@MainActor
protocol OfferSlot: AnyObject {

    /// The live offer's id, or `nil`. Read to confirm a countdown that has just expired is still the
    /// one that was running.
    var liveOfferId: String? { get }

    /// Starts reporting slot changes. Idempotent — a second call replaces the handler.
    func observe(_ onChange: @escaping (OfferSlotState) -> Void)

    /// Stops. **Cancel every subscription** — nothing on the Kotlin side is tied to a view's lifetime.
    func stopObserving()

    /// `POST /v1/rides/{rideId}/offer/{driverId}/accept` (R-02). `nil` only if the call was cancelled.
    func accept() async -> OfferOutcome?

    /// `POST …/decline`. No penalty (D5' §7); dispatch cascades to the next candidate.
    func decline() async -> OfferOutcome?

    /// Drops the live offer without telling the server — the local countdown reached zero.
    func expire()

    /// Applies the ride version the enrichment read already learned (R-14).
    func onVersionKnown(_ version: Int32)
}

/// ``OfferSlot`` over `:shared`'s `OfferSession`.
///
/// Converts and forwards, and decides nothing. The one thing it owns is the `FlowSubscription`, which
/// has to be cancelled or the closure — and whatever it captured — lives for the life of the process.
@MainActor
final class SharedOfferSlot: OfferSlot {

    private let offers: OfferSession
    private let states: IosFlowWatcher<OfferSessionState>
    private var subscription: FlowSubscription?

    init(offers: OfferSession, states: IosFlowWatcher<OfferSessionState>) {
        self.offers = offers
        self.states = states
    }

    var liveOfferId: String? { offers.offer?.offerId }

    func observe(_ onChange: @escaping (OfferSlotState) -> Void) {
        subscription?.cancel()
        subscription = states.watch { state in
            onChange(SharedOfferSlot.slotState(state))
        }
    }

    func stopObserving() {
        subscription?.cancel()
        subscription = nil
    }

    /// `try?` rather than a thrown error: a Kotlin suspend function reaches Swift as `async throws`
    /// because cancellation is an exception on that side, and `OfferSession.accept()` has already
    /// turned every *server* failure into an `OfferOutcome` before it returns. `nil` therefore means
    /// "the task was cancelled", which is not an ending the screen has to explain.
    func accept() async -> OfferOutcome? {
        try? await offers.accept()
    }

    func decline() async -> OfferOutcome? {
        try? await offers.decline()
    }

    func expire() {
        offers.onExpired()
    }

    func onVersionKnown(_ version: Int32) {
        offers.onVersionKnown(version: version)
    }

    private static func slotState(_ state: OfferSessionState) -> OfferSlotState {
        if let live = state as? OfferSessionStateLive { return .live(live.offer) }
        if state is OfferSessionStateDeciding { return .deciding }
        if state is OfferSessionStateWon { return .won }
        return .idle
    }
}
