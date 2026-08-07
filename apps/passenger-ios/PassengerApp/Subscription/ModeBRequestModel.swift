import Foundation
import MageRideShared

/// SCR-PI-024's state.
struct ModeBRequestState {

    /// What will be asked about. Pre-filled from a marker (AL-23) or typed.
    var vehicleId = ""

    /// Whether the id arrived from the map rather than from the keyboard.
    ///
    /// Only the caption differs — the field stays editable either way, because a passenger who opened
    /// the wrong marker should not have to go back to the map to fix a character.
    var isPrefilled = false

    /// Where the decision stands, once there is a request (US-4.6).
    var status: AccessRequestStatus?

    /// The subscription this passenger already has for ``vehicleId``, when there is one. Its presence
    /// is the only evidence this app has that a request was **accepted** — see ``ModeBRequestModel``.
    var existing: Subscription?

    var isLoading = false
    var isSending = false
    var errorKey: String?

    /// Whether a decision is pending on the server.
    var isPending: Bool { status == AccessRequestStatus.pending }

    /// Whether the owner has granted access — inferred, never observed. See ``ModeBRequestModel``.
    var isAccepted: Bool { status == AccessRequestStatus.accepted }

    /// Whether **Send request** can fire: an id, nothing in flight, and no grant already held.
    var canSend: Bool {
        guard !vehicleId.isEmpty, !isSending, existing == nil else { return false }
        return !isPending
    }
}

/// SCR-PI-024 — asking a private vehicle's owner for access.
///
/// **A request is PER VEHICLE, never per fleet** (AL-23). `POST /v1/mode-b/{vehicleId}/access-requests`
/// is scoped to the one vehicle whose marker was tapped, and a passenger who wants to see a second van
/// asks that van's owner separately. ``PassengerRoute/modeBRequest(vehicleId:)`` carries the id
/// optionally for the same reason: from a marker it is known, from the Menu tab's *"Private
/// transport"* row it is typed.
///
/// **Nothing is visible until the owner accepts** (D-23). The grant is what fanout-svc checks when a
/// passenger joins a geocell group, so a pending request changes nothing on the map — which is also
/// why AL-25's *"re-joining requires request → accept"* is the same flow as joining the first time,
/// and this screen is deliberately reachable after an unsubscribe.
///
/// **Accepted is inferred from the subscription, and Rejected cannot be observed at all.** There is no
/// passenger-facing read of one's own access requests — `GET /v1/mode-b/{vehicleId}/access-requests`
/// is the owner's — and notification-svc mints no Mode B push kind, so nothing tells this app a
/// decision was made. What *does* exist is `GET /v1/mode-b/subscriptions/{passengerId}`, and an
/// accepted request creates a subscription in the same transaction: a subscription for this vehicle
/// therefore means accepted, and the screen says so rather than leaving a chip on Pending forever. A
/// rejection leaves no trace either way. Both gaps are C082's and are restated in the C100 handoff.
@MainActor
final class ModeBRequestModel: ObservableObject {

    @Published private(set) var state: ModeBRequestState

    private let subscriptions: SubscriptionRepository
    private let sessions: PassengerSessions
    private let keys: IdempotencyKeys

    init(
        vehicleId: String?,
        subscriptions: SubscriptionRepository,
        sessions: PassengerSessions,
        keys: IdempotencyKeys
    ) {
        let identifier = vehicleId?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        self.state = ModeBRequestState(vehicleId: identifier, isPrefilled: !identifier.isEmpty)
        self.subscriptions = subscriptions
        self.sessions = sessions
        self.keys = keys
    }

    func onVehicleIdChange(_ value: String) {
        // Trimmed, because a Vehicle ID read off a marker and pasted back in brings whitespace with
        // it, and the server's answer to `" MR-VEH-48213"` is a 404 nobody can act on.
        state.vehicleId = value.trimmingCharacters(in: .whitespacesAndNewlines)
        state.status = nil
        state.errorKey = nil
    }

    func clearError() {
        state.errorKey = nil
    }

    /// Sends the request. Idempotent on a key minted once, so a double tap is one request (R-14).
    func send() async {
        guard state.canSend else { return }
        let vehicleId = state.vehicleId
        state.isSending = true
        state.errorKey = nil
        let key = keys.next()

        do {
            let request = try await subscriptions.requestAccess(
                vehicleId: vehicleId,
                note: nil,
                idempotencyKey: key
            )
            state.isSending = false
            state.status = request.status
        } catch is CancellationError {
            state.isSending = false
        } catch {
            state.isSending = false
            state.errorKey = ModeBErrors.messageKey(for: error)
        }
    }

    /// Looks for a subscription this passenger already holds for the vehicle in the field.
    ///
    /// Runs once on entry, and is silent on failure: it is a courtesy that turns *"Pending approval"*
    /// into *"you already have access"*, not a precondition for asking. A passenger who is genuinely
    /// already subscribed and taps Send anyway gets the server's `409 conflict`, which ``ModeBErrors``
    /// has copy for.
    func loadExisting() async {
        let vehicleId = state.vehicleId
        guard !vehicleId.isEmpty, let passengerId = sessions.userId else { return }

        state.isLoading = true
        guard let held = try? await subscriptions.subscriptions(passengerId: passengerId) else {
            state.isLoading = false
            return
        }

        state.isLoading = false
        guard let existing = held.first(where: { $0.vehicleId == vehicleId }) else { return }
        state.existing = existing
        state.status = AccessRequestStatus.accepted
    }
}
