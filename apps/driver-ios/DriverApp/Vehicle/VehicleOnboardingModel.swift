import Foundation
import MageRideShared

/// SCR-DI-004…004c's state — one wizard, four step bodies.
///
/// - Parameters:
///   - step: Which of the four is on screen. Progress is its position, 1-based.
///   - vehicleId: The vehicle being onboarded; `nil` until Step 1/4 has been saved.
///   - registrationNumber: Step 1/4's plate, unique across the active set (D-37).
///   - vehicleType: Step 1/4's canonical type (AL-09). Mode-C types only.
///   - insurance: Step 2/4's capture.
///   - revenue: Step 3/4's capture.
///   - photoFront: Step 4/4's front photo, number plate visible.
///   - photoBack: Step 4/4's back photo.
///   - savedVerdict: The verdict on the step currently shown, once it has been saved. `nil` means
///     nothing has been sent for this step yet — which is what makes the CTA a save rather than an
///     advance.
///   - fields: Every extracted field on this vehicle; the extract cards filter to their own.
///   - isLoading: The resume read is in flight — AL-30 decides which step opens, not the screen.
///   - isBusy: A save is in flight.
///   - errorKey: Resolved copy for the last failure.
///   - isRegistrationTaken: `409 registration-exists` (D-37) — an inline error on the plate field
///     rather than a screen-level one, because it is that one field that has to change.
///   - corrections: What the driver has retyped over a doubtful extracted value, keyed by
///     `registry.document_fields` key (Δ MCS-02, BR-25.3). Sent on the next save.
///   - editingKey: The row whose ✎ is open, or `nil` when none is.
///   - isSubmitted: Step 4/4 is saved; the wizard hands over to SCR-DI-006.
///   - hasExited: Back from Step 1/4 — *"back exits the wizard"* (D2' §SCR-DI-004).
struct VehicleOnboardingState {

    var step: OnboardingStep = OnboardingStep.details
    var vehicleId: String?
    var registrationNumber = ""
    var vehicleType: RideVehicleType?
    var insurance: CapturedImage?
    var revenue: CapturedImage?
    var photoFront: CapturedImage?
    var photoBack: CapturedImage?
    var savedVerdict: StepVerdict?
    var fields: [ExtractedField] = []
    var isLoading = true
    var isBusy = false
    var errorKey: String?
    var isRegistrationTaken = false
    var corrections: [String: String] = [:]
    var editingKey: String?
    var isSubmitted = false
    var hasExited = false

    /// 1…4 — the wireframe's "Step 2 of 4" and the 25/50/75/100 % progress bar.
    var stepNumber: Int { VehicleOnboardingSteps.number(of: step) }

    /// Whether the CTA is live: this step has everything it needs to be saved.
    var canContinue: Bool { !isBusy && !isLoading && (hasCorrections || isStepComplete) }

    private var isStepComplete: Bool {
        switch step {
        case OnboardingStep.insurance:
            return insurance != nil

        case OnboardingStep.revenue:
            return revenue != nil

        // Both, and the wireframe says why: one photograph cannot show a vehicle's front and back
        // number plates, and the plate is what step 4 is checked on.
        case OnboardingStep.photos:
            return photoFront != nil && photoBack != nil

        // `details`, plus the arm a Kotlin enum forces on every Swift `switch` over one.
        default:
            return !registrationNumber.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                && vehicleType != nil
        }
    }

    /// Whether this step has been saved and came back needing a Verification Officer (BR-25.3).
    var isPendingReview: Bool { savedVerdict == StepVerdict.pendingReview }

    /// Whether the driver has retyped something the next save has to carry.
    var hasCorrections: Bool { corrections.values.contains { !$0.isEmpty } }

    /// The extract card's rows for the step on screen, in the order the wireframe draws them.
    var stepFields: [ExtractedField] {
        VehicleFieldKeys.forStep(step).compactMap { key in fields.first { $0.key == key } }
    }

    /// The weakest extraction in this step, or `nil` when nothing came back with a confidence.
    ///
    /// The **lowest**, because a step is only as verified as its weakest field (BR-25.3).
    var lowestConfidence: Double? { stepFields.compactMap(\.confidenceValue).min() }

    /// Whether this step's capture slot already holds an image.
    func isCaptured(_ target: DocumentCaptureTarget) -> Bool {
        switch target {
        case .insurance: return insurance != nil
        case .revenueLicence: return revenue != nil
        case .vehicleFront: return photoFront != nil
        case .vehicleBack: return photoBack != nil
        default: return false
        }
    }
}

/// **SCR-DI-004 → 004c** — the optional, in-app, **Mode-C-only** four-step vehicle wizard
/// (AL-27, AL-30, Change 6/22).
///
/// Three fences hold this screen up, and each one is visible in the code rather than in a comment
/// alone:
///
/// * **Mode C only.** `POST /v1/vehicles` pins `mode` to `C` by contract `const` and the type is a
///   `RideVehicleType`, which has no `bus` and no `train`. **There is no permit field and no
///   GPS-tracker field** — a Mode A vehicle and its route permit are onboarded in the Fleet Portal
///   (SCR-FP-004), never here.
/// * **Per-step save** (BR-25.4). Each step is persisted on its own as it is completed, so a driver
///   who closes the app on Step 3 has lost nothing.
/// * **Resume at the first non-verified step, never Step 1** (AL-30). ``ResumePoint`` is that rule as
///   a type, and this model asks for it on every open rather than deciding for itself.
///
/// **The CTA is two-beat on a step that comes back Pending**, and that is the wireframe's own
/// sequence rather than an invention: the extract card cannot exist before the upload, because the
/// upload is what queues the Gemini Flash extraction. So the first tap saves; if the step verified
/// there is nothing to read and the wizard moves straight on, and if anything was doubtful or the
/// plate did not match, the driver is left on the card with the ⚑ chip saying an officer will look at
/// it (BR-25.3). A second tap continues — a `pendingReview` step is pending **by design** and waiting
/// for it to clear would trap the driver in the wizard forever.
@MainActor
final class VehicleOnboardingModel: ObservableObject {

    @Published private(set) var state = VehicleOnboardingState()

    private let vehicles: VehicleOnboardingRepository
    private let captures: DocumentCaptureCoordinator
    private let session: VehicleOnboardingSession

    init(
        vehicles: VehicleOnboardingRepository,
        captures: DocumentCaptureCoordinator,
        session: VehicleOnboardingSession
    ) {
        self.vehicles = vehicles
        self.captures = captures
        self.session = session
    }

    // MARK: - Opening

    /// Reads AL-30's resume point. Called on open and again after a failure the driver retries.
    func load() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            switch try await vehicles.resume() {
            case .fresh:
                break

            case let .resume(vehicleId, registrationNumber, vehicleType, step, _, fields):
                state.vehicleId = vehicleId
                state.registrationNumber = registrationNumber
                state.vehicleType = vehicleType
                state.step = step
                state.fields = fields
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    // MARK: - The form

    func onRegistrationChanged(_ value: String) {
        state.registrationNumber = value
        state.errorKey = nil
        state.isRegistrationTaken = false
        state.savedVerdict = nil
    }

    func onVehicleTypeChanged(_ type: RideVehicleType) {
        state.vehicleType = type
        state.errorKey = nil
        state.savedVerdict = nil
    }

    /// A capture tile was tapped. The scanner is SCR-DI-005's; this only says which slot.
    func requestCapture(_ target: DocumentCaptureTarget) {
        captures.open(target)
    }

    /// The ✎ on an extracted row (Δ MCS-02). Opens it for editing; a second tap closes it.
    func toggleEdit(_ key: String) {
        state.editingKey = state.editingKey == key ? nil : key
    }

    /// The driver retyped a value the scan got wrong or could not read (BR-25.3).
    ///
    /// Held until the next save. **The client never stamps a provenance** — registry-svc writes
    /// `source='manual'`, `verify_status='pending'` and queues the officer review, because a client
    /// that could claim `source='ai'` would make AL-29 advisory.
    func onCorrectionChanged(key: String, value: String) {
        if value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            state.corrections.removeValue(forKey: key)
        } else {
            state.corrections[key] = value
        }
        state.errorKey = nil
    }

    /// SCR-DI-005 hands its cropped, de-skewed image back here.
    ///
    /// The two licence slots belong to C086's Profile Setup and never reach a vehicle — AL-27 keeps
    /// driver identity and vehicle onboarding apart — so a result this screen did not ask for is
    /// left alone rather than consumed out from under the screen that did.
    func apply(_ result: DocumentCaptureResult) {
        switch result.target {
        case .insurance: state.insurance = result.image
        case .revenueLicence: state.revenue = result.image
        case .vehicleFront: state.photoFront = result.image
        case .vehicleBack: state.photoBack = result.image
        default: return
        }

        // A fresh capture un-saves the step: what is on screen is no longer what was sent, so the
        // CTA has to be a save again rather than an advance.
        state.savedVerdict = nil
        state.errorKey = nil
        captures.consume()
    }

    // MARK: - Moving

    /// The ‹ in the navigation bar.
    ///
    /// Step 1/4's back **exits the wizard** (D2' §SCR-DI-004); anything else steps back one, which is
    /// how a driver corrects a plate they mistyped before the photos were judged against it.
    func onBack() {
        guard let previous = VehicleOnboardingSteps.previous(before: state.step) else {
            state.hasExited = true
            return
        }
        move(to: previous)
    }

    /// The CTA — *"Continue · Insurance ›"*, *"Save & continue"*, *"Save & submit for review"*.
    ///
    /// See ``VehicleOnboardingModel`` for why a Pending step takes two taps and a verified one takes
    /// one.
    func onContinue() async {
        guard state.canContinue else { return }

        // A correction is a save of its own, even on a step that is already saved — it is the one
        // thing that can change a verdict without a new photograph (Δ MCS-02, BR-25.3).
        if state.savedVerdict != nil, !state.hasCorrections {
            advance(from: state.step, serverNext: nil)
            return
        }
        await save()
    }

    private func save() async {
        let current = state
        state.isBusy = true
        state.errorKey = nil
        state.isRegistrationTaken = false

        do {
            let saved = try await saveStep(current)
            session.open(saved.vehicleId)

            // The save answers a verdict but not the fields behind it — only the onboarding-status
            // read carries those, and the extract card is made of them.
            let fields = (try? await vehicles.onboardingStatus(saved.vehicleId).fields) ?? current.fields

            state.vehicleId = saved.vehicleId
            state.savedVerdict = saved.stepStatus
            state.fields = fields
            state.corrections = [:]
            state.editingKey = nil
            state.isBusy = false

            // A clean step has nothing for the driver to read, so it does not stop for them.
            if saved.stepStatus == StepVerdict.verified {
                advance(from: current.step, serverNext: saved.nextStep)
            }
        } catch {
            state.isBusy = false
            state.isRegistrationTaken = Self.isRegistrationTaken(error)
            state.errorKey = state.isRegistrationTaken ? nil : OnboardingErrors.messageKey(for: error)
        }
    }

    private func saveStep(_ current: VehicleOnboardingState) async throws -> SavedStep {
        switch current.step {
        case OnboardingStep.insurance, OnboardingStep.revenue:
            guard let vehicleId = current.vehicleId else {
                preconditionFailure("a document step cannot be saved before Step 1/4 minted a vehicle")
            }
            // A correction with the document already on record needs no upload at all — which is the
            // whole point of it (Δ MCS-02). A first save still carries the image.
            if current.savedVerdict != nil, current.hasCorrections {
                return try await vehicles.saveCorrections(
                    vehicleId: vehicleId,
                    step: current.step,
                    corrections: onboardingCorrections(from: current.corrections)
                )
            }
            let capture = current.step == OnboardingStep.insurance ? current.insurance : current.revenue
            guard let capture else {
                preconditionFailure("the CTA is dead until this step's document is captured")
            }
            return try await vehicles.saveDocument(
                vehicleId: vehicleId,
                step: current.step,
                front: capture,
                back: nil
            )

        case OnboardingStep.photos:
            guard
                let vehicleId = current.vehicleId,
                let front = current.photoFront,
                let back = current.photoBack
            else {
                preconditionFailure("the CTA is dead until both vehicle photographs are captured")
            }
            return try await vehicles.saveDocument(
                vehicleId: vehicleId,
                step: OnboardingStep.photos,
                front: front,
                back: back
            )

        // `details`, plus the arm a Kotlin enum forces on every Swift `switch` over one.
        default:
            guard let type = current.vehicleType else {
                preconditionFailure("the CTA is dead until a vehicle type is chosen")
            }
            guard let vehicleId = current.vehicleId else {
                return try await vehicles.start(
                    registrationNumber: current.registrationNumber,
                    vehicleType: type
                )
            }
            return try await vehicles.saveDetails(
                vehicleId: vehicleId,
                registrationNumber: current.registrationNumber,
                vehicleType: type
            )
        }
    }

    /// Moves the wizard on from [from].
    ///
    /// [serverNext] is registry-svc's own `nextStep` and wins when it is present — it is derived from
    /// all four verdicts, so a driver who resumed at Step 3 and whose Step 2 is still pending is sent
    /// back to Step 2 rather than marched forward. Step 4/4 has no next: it hands over to SCR-DI-006,
    /// which is where the four verdicts are read.
    private func advance(from step: OnboardingStep, serverNext: OnboardingStep?) {
        // Step 4/4 ends the wizard whatever the server points at. Its own `nextStep` is non-null
        // when an earlier step is still pending, and following it would send a driver who has just
        // submitted back into the form — SCR-DI-006 is the screen that explains a pending verdict.
        guard step != OnboardingStep.photos else {
            state.isSubmitted = true
            return
        }
        guard let next = serverNext ?? VehicleOnboardingSteps.next(after: step) else {
            state.isSubmitted = true
            return
        }
        move(to: next)
    }

    private func move(to step: OnboardingStep) {
        state.step = step
        state.savedVerdict = nil
        state.corrections = [:]
        state.editingKey = nil
        state.errorKey = nil
    }

    /// `409 registration-exists` — the plate is already on a live vehicle (D-37, US-2.7).
    private static func isRegistrationTaken(_ error: Error) -> Bool {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else { return false }
        return failure.code == ErrorCode.registrationExists
    }
}
