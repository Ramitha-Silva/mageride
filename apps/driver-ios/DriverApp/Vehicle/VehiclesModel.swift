import Foundation
import MageRideShared

/// One row of My Vehicles.
///
/// - Parameters:
///   - summary: The vehicle as `GET /v1/vehicles/mine` returned it.
///   - nextStep: Where **Resume** opens, for a vehicle that is still Incomplete (AL-30). Read per
///     vehicle from `GET …/onboarding-status`, because the list read does not carry it and the
///     wireframe prints it — *"Incomplete · Step 3 of 4"*, not just "Incomplete".
struct VehicleRow: Identifiable {

    let summary: VehicleSummary
    var nextStep: OnboardingStep?

    var id: String { summary.vehicleId }

    /// The vehicle.
    var vehicleId: String { summary.vehicleId }

    /// Whether this one can be selected to go live (US-9.6).
    var canGoLive: Bool { summary.canGoLive }

    /// Whether the wizard still has work to do on it.
    var isIncomplete: Bool {
        summary.isOnboardable && summary.onboardingStatus == OnboardingStatus.incomplete
    }

    /// 1-based position of ``nextStep`` in the wizard, for *"Step 3 of 4"*.
    var nextStepNumber: Int? { nextStep.map(VehicleOnboardingSteps.number(of:)) }

    /// *"Three-wheeler · ABC-1234"*. The type is trilingual and the plate is not — a registration
    /// number is a proper noun, which is C068's rule and C086's.
    var titleText: String {
        summary.vehicleType.labelKey.localised + " · " + summary.registrationNumber
    }

    /// MAP-03's legend colour for this row's dot.
    var token: VehicleToken { VehicleToken.forVehicleType(summary.vehicleType) }

    /// The wireframe's assignment caption — the fleet, and the date the loan ends (Δ MCS-02,
    /// US-13.9).
    ///
    /// `nil` for an owned vehicle. The date goes through `:shared`'s `BusinessCalendar` (see
    /// ``colomboBusinessDate(at:)``) because D-38 gives the platform one timezone for a business date
    /// and it is **Asia/Colombo**, not the handset's — a driver in another zone must not read a
    /// different expiry from the one the fleet set. It is absent again on an open-ended assignment,
    /// which is a real state rather than a missing value.
    var assignmentCaption: String? {
        guard let fleet = summary.fleetName else { return nil }
        guard let until = summary.assignedUntil else { return fleet }
        return "vehicles_assigned_until".localisedFormat(fleet, IosBusinessDateKt.colomboBusinessDate(at: until))
    }

    /// The wireframe's per-row status: *"Active now"*, *"Approved"*, *"Incomplete · Step 3 of 4"*.
    ///
    /// The step number is what makes Incomplete actionable rather than a scold — a driver who knows
    /// they are on Step 3 of 4 finishes; one told only that something is missing goes to support.
    func status(isActive: Bool) -> (label: String, tone: StatusTone) {
        if isActive {
            return ("vehicles_active_now".localised, .done)
        }
        if isIncomplete, let stepNumber = nextStepNumber {
            return (
                "vehicles_incomplete_step".localisedFormat(stepNumber, VehicleOnboardingSteps.count),
                .pending
            )
        }
        if isIncomplete {
            return ("vehicles_incomplete".localised, .pending)
        }
        return (
            summary.status.labelKey.localised,
            summary.status == RegistrationStatus.approved ? .done : .pending
        )
    }
}

/// SCR-DI-026 / 026a's state.
///
/// - Parameters:
///   - owned: The driver's own Mode C vehicles — the *"My vehicles"* group.
///   - assigned: Fleet vehicles temporarily assigned or shared to them (US-13.9) — the
///     *"Temporarily assigned to me"* group.
///   - activeVehicleId: Which one is the single live publisher (D-03).
///   - isLoading: A read is in flight.
///   - errorKey: Resolved copy for the last failure.
///   - isOnboardPromptVisible: SCR-DI-026a — the *"Onboard a standby vehicle?"* alert.
///   - deactivating: The vehicle whose deactivate-confirm alert is up (US-2.16).
struct VehiclesState {

    var owned: [VehicleRow] = []
    var assigned: [VehicleRow] = []
    var activeVehicleId: String?
    var isLoading = true
    var errorKey: String?
    var isOnboardPromptVisible = false
    var deactivating: VehicleRow?

    /// Whether the driver has no vehicle at all — what raises SCR-DI-026a.
    var isEmpty: Bool { !isLoading && owned.isEmpty && assigned.isEmpty }

    /// Whether **any** vehicle could be selected to go live (US-9.6).
    ///
    /// C088's dashboard asks the same question of the same rule; it is here because this is the
    /// screen that can do something about the answer.
    var canGoOnline: Bool { (owned + assigned).contains(where: \.canGoLive) }
}

/// **SCR-DI-026 · vehicle management** and **SCR-DI-026a · the no-vehicles alert**.
///
/// Three rules, all of them AL-30's or US-9.6's rather than this screen's:
///
/// * **Only an approved Mode C vehicle can be selected to go live** — or a shared/assigned Mode A/B
///   one, which the Fleet Portal approved. ``VehicleSummary/canGoLive`` is that rule, once.
/// * **An Incomplete vehicle resumes at its next step, never Step 1.** The ＋ button and a row's
///   Resume are the same action for that reason: both open the wizard, and
///   ``VehicleOnboardingRepository/resume()`` is what decides whether that is a resume or a **new**
///   vehicle at Step 1/4 (US-2.27).
/// * **Owned is Mode C; temporarily assigned is Mode A/B.** The driver app onboards Mode C only
///   (AL-27), so a Mode A/B vehicle in this driver's list arrived by assignment or share — there is
///   no other way for one to be there.
@MainActor
final class VehiclesModel: ObservableObject {

    @Published private(set) var state: VehiclesState

    private let vehicles: VehicleOnboardingRepository
    private let session: VehicleOnboardingSession
    private let activeVehicle: ActiveVehicleStore

    init(
        vehicles: VehicleOnboardingRepository,
        session: VehicleOnboardingSession,
        activeVehicle: ActiveVehicleStore
    ) {
        self.vehicles = vehicles
        self.session = session
        self.activeVehicle = activeVehicle
        state = VehiclesState(activeVehicleId: activeVehicle.activeVehicleId)
    }

    /// Re-reads `GET /v1/vehicles/mine`, and the resume point of anything still Incomplete.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            var rows: [VehicleRow] = []
            for summary in try await vehicles.myVehicles() {
                var row = VehicleRow(summary: summary)
                // Only for a vehicle that is actually part-way through: one extra read for the one
                // row that prints a step number, rather than a read per vehicle.
                if row.isIncomplete {
                    row.nextStep = await nextStep(of: summary.vehicleId)
                }
                rows.append(row)
            }

            // A vehicle that has been deactivated elsewhere must not stay selected — going online as
            // one the platform has retired is a connection that fails with no way for the driver to
            // see why.
            let stillHeld = activeVehicle.activeVehicleId.flatMap { id in
                rows.contains { $0.vehicleId == id } ? id : nil
            }
            activeVehicle.activeVehicleId = stillHeld

            state.owned = rows.filter(\.summary.isOnboardable)
            state.assigned = rows.filter { !$0.summary.isOnboardable }
            state.activeVehicleId = stillHeld
            state.isOnboardPromptVisible = rows.isEmpty
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// Picks the single live publisher (D-03).
    ///
    /// Refused for a vehicle that is not go-live eligible rather than left to the dashboard: the row
    /// is not selectable in the wireframe either, and a selection the go-online toggle then rejected
    /// would be a state the driver cannot understand.
    func select(_ row: VehicleRow) {
        guard row.canGoLive else { return }
        activeVehicle.activeVehicleId = row.vehicleId
        state.activeVehicleId = row.vehicleId
    }

    /// Names the vehicle whose status or remaining steps the next screen is about.
    func open(_ row: VehicleRow) {
        session.open(row.vehicleId)
    }

    /// SCR-DI-026a's *"Not now"* — the empty list, with no alert over it.
    func dismissOnboardPrompt() {
        state.isOnboardPromptVisible = false
    }

    /// US-2.16 — the confirm the wireframe requires before a vehicle is removed.
    func confirmDeactivate(_ row: VehicleRow) {
        state.deactivating = row
    }

    func cancelDeactivate() {
        state.deactivating = nil
    }

    /// `POST /v1/vehicles/{id}/deactivate` — US-2.16.
    ///
    /// It also **releases the plate** from the D-37 active-set index, which is why the same
    /// registration can be onboarded again afterwards.
    func deactivate() async {
        guard let row = state.deactivating else { return }
        state.deactivating = nil
        state.isLoading = true
        state.errorKey = nil
        do {
            try await vehicles.deactivate(row.vehicleId)
            if session.vehicleId == row.vehicleId { session.close() }
            if activeVehicle.activeVehicleId == row.vehicleId {
                activeVehicle.activeVehicleId = nil
                state.activeVehicleId = nil
            }
            await refresh()
        } catch {
            state.isLoading = false
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    private func nextStep(of vehicleId: String) async -> OnboardingStep? {
        guard let status = try? await vehicles.onboardingStatus(vehicleId) else { return nil }
        return status.nextStep ?? status.steps.firstUnverified
    }
}
