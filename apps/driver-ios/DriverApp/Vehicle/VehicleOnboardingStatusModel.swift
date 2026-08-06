import Foundation
import MageRideShared

/// SCR-DI-006's state — the four-document verdict list.
///
/// - Parameters:
///   - registrationNumber: The plate, for the *"Sedan · ABC-1234"* header.
///   - vehicleType: The type in the same header.
///   - verdicts: The four verdicts. `nil` while the first read is in flight.
///   - registrationStatus: The vehicle's own status; `approved` is the auto-approval AL-27 makes
///     happen with no officer step.
///   - onboardingStatus: Derived incomplete / approved (AL-30).
///   - rejectionReason: Why it was rejected, when it was — US-2.15's re-upload prompt.
///   - isLoading: A read is in flight.
///   - errorKey: Resolved copy for the last failure.
///   - isUnknownVehicle: Nothing named a vehicle for this screen. Not an error: it is what a restore
///     onto a deactivated vehicle looks like, and the screen offers a way back.
struct VehicleOnboardingStatusState {

    var registrationNumber: String?
    var vehicleType: VehicleType?
    var verdicts: OnboardingStepVerdicts?
    var registrationStatus: RegistrationStatus?
    var onboardingStatus: OnboardingStatus?
    var rejectionReason: String?
    var isLoading = true
    var errorKey: String?
    var isUnknownVehicle = false

    /// The four rows, in the order the wireframe lists them.
    var rows: [StepVerdictRow] { verdicts?.rows ?? [] }

    /// How many documents are not yet verified — the wireframe's *"⚠ 1 pending → officer"*.
    var pendingCount: Int { rows.filter { $0.verdict != StepVerdict.verified }.count }

    /// Whether the vehicle came out the other side approved.
    ///
    /// Both halves, because AL-30 makes them different questions: `onboardingStatus` says the four
    /// steps are done and `status` says the registration stands. C029's decision (5) is why they can
    /// disagree — a renewal whose scan was blurry takes `onboardingStatus` back to incomplete and
    /// leaves an APPROVED vehicle on the road until E-03's expiry sweep says otherwise.
    var isApproved: Bool {
        registrationStatus == RegistrationStatus.approved && onboardingStatus == OnboardingStatus.approved
    }

    /// US-2.15 — rejected, with a reason, and the driver has to re-upload.
    var isRejected: Bool { registrationStatus == RegistrationStatus.rejected }

    /// Whether a step is still the **driver's** to finish rather than an officer's (AL-30, US-2.10).
    var canResume: Bool { rows.contains { $0.verdict == StepVerdict.pendingInput } }

    /// The header line — *"Sedan · ABC-1234"*. The type is trilingual and the plate is not: a
    /// registration number is a proper noun.
    var headerText: String {
        [vehicleType.map { $0.labelKey.localised }, registrationNumber]
            .compactMap { $0 }
            .joined(separator: " · ")
    }
}

/// **SCR-DI-006 · vehicle onboarding status** — the four-document verdict list (Change 6/22).
///
/// > *"All Verified → vehicle status APPROVED automatically (no human step) → appears in My Vehicles
/// > as Approved … Any Pending → Verification Officer queue (US-2.10)."*
///
/// **The transition is the server's, and this screen only reports it.** registry-svc sets
/// `status=APPROVED` when the fourth step verifies (AL-27, and the save that caused it says so in its
/// own response), so there is nothing here to poll for and nothing to decide. What the screen does
/// own is ``refresh()``: a Pending document is confirmed by a Verification Officer minutes or days
/// later, and US-2.14's APNs push is what tells the driver to come back and look.
@MainActor
final class VehicleOnboardingStatusModel: ObservableObject {

    @Published private(set) var state = VehicleOnboardingStatusState()

    private let vehicles: VehicleOnboardingRepository
    private let session: VehicleOnboardingSession

    init(vehicles: VehicleOnboardingRepository, session: VehicleOnboardingSession) {
        self.vehicles = vehicles
        self.session = session
    }

    /// Re-reads the verdicts. The screen's own action, and what a US-2.14 push brings a driver to.
    func refresh() async {
        guard let vehicleId = session.vehicleId else {
            state.isLoading = false
            state.isUnknownVehicle = true
            return
        }

        state.isLoading = true
        state.errorKey = nil
        do {
            // Two reads: the verdicts, and the vehicle whose plate and type the header shows.
            let status = try await vehicles.onboardingStatus(vehicleId)
            let vehicle = try await vehicles.vehicle(vehicleId)

            state.registrationNumber = vehicle.registrationNumber
            state.vehicleType = vehicle.vehicleType
            state.verdicts = status.steps
            state.registrationStatus = status.status
            state.onboardingStatus = status.onboardingStatus
            state.rejectionReason = vehicle.rejectionReason
            state.isUnknownVehicle = false
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }
}
