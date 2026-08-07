import Foundation
import MageRideShared

/// SCR-DI-028's state.
///
/// - Parameters:
///   - vehicles: The shareable vehicles — Mode A and Mode B only; see ``SharingModel``.
///   - selectedVehicleId: Which vehicle everything below is scoped to.
///   - userId: The passenger id typed into the share form.
///   - expiresAt: When the grant should lapse; `nil` is open-ended (US-4.2).
///   - requests: Incoming Mode B access requests **for the selected vehicle** (US-4.4).
///   - grantees: Who can currently see it (US-4.7).
///   - isLoading: The vehicle read is in flight.
///   - isReadingLists: The per-vehicle lists are being re-read after a selector change.
///   - isGranting: The grant POST is in flight.
///   - busyRequestId: The request whose accept or reject is in flight.
///   - grantedTo: The id of the passenger the last successful grant was offered to.
///   - errorKey: Resolved copy for the last failure.
struct SharingState {

    var vehicles: [VehicleSummary] = []
    var selectedVehicleId: String?
    var userId = ""
    var expiresAt: Timestamp?
    var requests: [AccessRequest] = []
    var grantees: [Subscriber] = []
    var isLoading = true
    var isReadingLists = false
    var isGranting = false
    var busyRequestId: String?
    var grantedTo: String?
    var errorKey: String?

    /// The vehicle the whole screen is about.
    var selected: VehicleSummary? { vehicles.first { $0.vehicleId == selectedVehicleId } }

    /// Whether what has been typed is a well-formed platform id. Blank is *"not yet"*, not *"wrong"*.
    var isUserIdValid: Bool { PlatformId.isValid(userId) }

    /// Whether the field should be drawn in `error` — typed something, and it is not an id.
    var isUserIdRejected: Bool { !userId.trimmingCharacters(in: .whitespaces).isEmpty && !isUserIdValid }

    /// Whether **Grant access** is live.
    var canGrant: Bool { !isGranting && isUserIdValid && selectedVehicleId != nil }

    /// Whether the driver has no vehicle that can be shared at all.
    var hasNoShareableVehicle: Bool { !isLoading && vehicles.isEmpty }
}

/// **SCR-DI-028 · sharing management, per vehicle** (US-4.1–4.4, US-4.7/4.8, US-13.9, AL-35).
///
/// **AL-35's fence: the caption box is gone and the selector is the scope.** The wireframe used to
/// carry a *"Showing sharing for … temporarily assigned by …"* box above the list; it was removed, and
/// the full-device-width vehicle selector took its job — *"the selected chip already conveys the active
/// vehicle"*. So changing the selection is not a filter over one list, it is a **re-read**:
/// ``select(vehicleId:)`` clears both lists and fetches that vehicle's own, because both endpoints are
/// scoped by `vehicleId` in the path and there is no combined read to filter.
///
/// **Only Mode A and Mode B vehicles are offered.** `POST /v1/vehicles/{id}/share` is documented as
/// *"offer a passenger access to a **Mode A/B** vehicle"* and the request queue is literally
/// `/v1/mode-b/…`; a Mode C standby tuk has no subscribers and nothing to share. A driver with only
/// Mode C vehicles therefore sees the empty state rather than a selector that grants nothing.
///
/// **A new grant does not appear in the grantee list, and that is correct.** US-4.3b: visibility begins
/// when the *passenger accepts*, not when the owner offers. The screen acknowledges the offer and
/// leaves the list alone — a row that claimed someone could already see the vehicle would be wrong for
/// as long as they had not answered.
@MainActor
final class SharingModel: ObservableObject {

    @Published private(set) var state = SharingState()

    private let identity: DriverIdentity
    private let sharing: SharingRepository

    init(identity: DriverIdentity, sharing: SharingRepository) {
        self.identity = identity
        self.sharing = sharing
    }

    /// Re-reads the shareable vehicles and then the selected one's two lists.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            let all = try await identity.liveVehicle()
            let shareable = all.vehicles.filter { $0.mode == ServiceMode.a || $0.mode == ServiceMode.b }
            let held = state.selectedVehicleId.flatMap { id in
                shareable.contains { $0.vehicleId == id } ? id : nil
            }
            // The live vehicle first when it is shareable — a driver assigned a fleet van is looking at
            // that van's requests, not at the other vehicle they happen to own.
            let live = all.live?.vehicleId
            let liveShareable = live.flatMap { id in shareable.contains { $0.vehicleId == id } ? id : nil }
            let selected = held ?? liveShareable ?? shareable.first?.vehicleId

            state.vehicles = shareable
            state.selectedVehicleId = selected
            state.isLoading = false
            if let selected {
                // Raised before the two reads for the reason ``select(vehicleId:)`` raises it: without
                // it the queue's *"nobody has asked"* line shows for as long as the read takes, which
                // is a different sentence from "we do not know yet".
                state.isReadingLists = true
                await readLists(for: selected)
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
            state.isLoading = false
        }
    }

    /// The wireframe's full-device-width vehicle selector. Re-scopes the queue and the grantee list.
    func select(vehicleId: String) async {
        guard state.selectedVehicleId != vehicleId else { return }

        state.selectedVehicleId = vehicleId
        // Emptied rather than left up: a request row belongs to the vehicle it targets, and showing the
        // previous vehicle's queue under a new chip is the one thing AL-35's "never mixed across
        // vehicles" forbids.
        state.requests = []
        state.grantees = []
        state.isReadingLists = true
        state.grantedTo = nil
        state.errorKey = nil

        await readLists(for: vehicleId)
    }

    /// The **Share with User ID** field.
    func onUserIdChange(_ raw: String) {
        state.userId = raw
        state.grantedTo = nil
        state.errorKey = nil
    }

    /// The **Expiry** picker. `nil` clears it back to an open-ended grant.
    func onExpiryChange(_ expiresAt: Timestamp?) {
        state.expiresAt = expiresAt
        state.errorKey = nil
    }

    /// **Grant access** — `POST /v1/vehicles/{vehicleId}/share` (US-4.1/4.2).
    func grant() async {
        guard state.canGrant, let vehicleId = state.selectedVehicleId else { return }
        let userId = PlatformId.of(state.userId)
        let expiresAt = state.expiresAt

        state.isGranting = true
        state.grantedTo = nil
        state.errorKey = nil
        do {
            _ = try await sharing.grant(vehicleId: vehicleId, userId: userId, expiresAt: expiresAt)
            // The form clears because the offer is out; leaving the id in place invites a second grant
            // to the same passenger, which is a second pending invitation.
            state.grantedTo = userId
            state.userId = ""
            state.expiresAt = nil
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isGranting = false
    }

    /// The wireframe's **Accept** / **Reject** pair — one decision with two answers (US-4.4).
    ///
    /// One function rather than two, because the two differ only in which subscription-svc route is
    /// called and share every other rule: one decision at a time, a re-read of both lists after it, and
    /// a failed decision that leaves the row where it was so the driver can look at it again.
    ///
    /// The re-read is what keeps the lists honest. Accepting moves a row out of the queue **and** into
    /// the grantee roster — the accept creates the entitlement and starts the subscription in one
    /// transaction — and rejecting moves it out of the queue only; moving rows locally would be this
    /// screen guessing at what two services just did.
    ///
    /// - Parameter isAccepted: `true` admits them, `false` declines.
    func decide(requestId: String, isAccepted: Bool) async {
        guard let vehicleId = state.selectedVehicleId, state.busyRequestId == nil else { return }

        state.busyRequestId = requestId
        state.errorKey = nil
        do {
            if isAccepted {
                try await sharing.accept(requestId: requestId)
            } else {
                try await sharing.reject(requestId: requestId)
            }
            await readLists(for: vehicleId)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.busyRequestId = nil
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// The two per-vehicle reads, folded in only if the selection has not moved on.
    ///
    /// A driver tapping quickly between two vehicles has two reads in flight; without the guard the
    /// slower one wins and paints the wrong vehicle's queue under the selected chip.
    private func readLists(for vehicleId: String) async {
        do {
            let requests = try await sharing.requests(vehicleId: vehicleId)
                .filter { $0.status == AccessRequestStatus.pending }
            let grantees = try await sharing.grantees(vehicleId: vehicleId)
                .filter { $0.status == GrantStatus.active }

            guard state.selectedVehicleId == vehicleId else { return }
            state.requests = requests
            state.grantees = grantees
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        if state.selectedVehicleId == vehicleId {
            state.isReadingLists = false
        }
    }
}
