import Foundation
import MageRideShared

/// The `registry.document_fields` keys the four Mode-C onboarding steps produce (AL-29,
/// `DocumentFieldKeys` in registry-svc).
///
/// Machine keys, never display copy — the row labels are `Localizable.strings`' and are trilingual.
/// The `details` step has none: D5' §14.1a marks it "(entered)", so the typing **is** the
/// verification and there is no extracted field to show.
enum VehicleFieldKeys {

    /// Step 2/4. D5' §14.1a verifies insurance on the expiry alone; the other two are extras.
    static let insuranceExpiry = "insurance_expiry"
    static let insurancePolicyNo = "insurance_policy_no"
    static let insurer = "insurer"

    /// Step 3/4 — number **and** expiry, both required.
    static let revenueNo = "revenue_no"
    static let revenueExpiry = "revenue_expiry"

    /// Step 4/4. `reg_no_match` is the plate check; `plate_text` is what the plate was read as.
    static let regNoMatch = "reg_no_match"
    static let plateText = "plate_text"

    /// The keys a driver may retype (Δ MCS-02, BR-25.3).
    ///
    /// `plate_text` and `reg_no_match` are deliberately absent: they are the check Step 4/4 exists
    /// to perform, and a driver who could retype the plate the camera read would be verifying their
    /// own vehicle. The server filters on the same rule — this set only decides which rows draw a ✎.
    static let correctable: Set<String> = [insuranceExpiry, insurancePolicyNo, revenueNo, revenueExpiry]

    /// The keys each step's extract card lists, in the order the wireframe draws them.
    static func forStep(_ step: OnboardingStep) -> [String] {
        switch step {
        case OnboardingStep.insurance: return [insuranceExpiry, insurancePolicyNo, insurer]
        case OnboardingStep.revenue: return [revenueNo, revenueExpiry]
        case OnboardingStep.photos: return [plateText, regNoMatch]
        // `details` is "(entered)" and has no extracted field. A Kotlin enum is a *class* on this
        // side of the bridge, so the compiler cannot prove the four cases are all of them and a
        // `default` is required whatever is written above it.
        default: return []
        }
    }
}

/// The wizard's four steps, in the order it walks them.
///
/// `OnboardingStep.entries` is Kotlin's and does not cross the bridge as anything a Swift `for` can
/// use, so the order is written out here — the same shape ``ProfileSetupScreen`` uses for AL-09's
/// chip set. It is not a second source of truth: ``number(of:)`` and ``next(after:)`` index by the
/// shared enum's own `ordinal`, and `VehicleOnboardingRepositoryTests` asserts the table and the
/// ordinals agree, so a step inserted in `:shared` fails a test here rather than mis-numbering a
/// progress bar.
enum VehicleOnboardingSteps {

    static let all: [OnboardingStep] = [
        OnboardingStep.details,
        OnboardingStep.insurance,
        OnboardingStep.revenue,
        OnboardingStep.photos,
    ]

    /// Four — the wireframe's "Step 2 of 4".
    static var count: Int { all.count }

    /// 1…4, the wireframe's step number.
    static func number(of step: OnboardingStep) -> Int { Int(step.ordinal) + 1 }

    /// The step after [step], or `nil` for the last one.
    static func next(after step: OnboardingStep) -> OnboardingStep? {
        let index = Int(step.ordinal) + 1
        return index < all.count ? all[index] : nil
    }

    /// The step before [step], or `nil` for Step 1/4 — whose Back exits the wizard.
    static func previous(before step: OnboardingStep) -> OnboardingStep? {
        let index = Int(step.ordinal) - 1
        return index >= 0 ? all[index] : nil
    }
}

/// Where the wizard opens.
///
/// **AL-30's whole rule, as a type.** Re-opening Vehicle Onboarding resumes at the first step that
/// is not verified — *never* Step 1 — and once the current vehicle is approved the entry point
/// starts a **new** one. Returning a value with two cases rather than an optional vehicle id is what
/// stops a screen deciding that for itself.
enum ResumePoint {

    /// No vehicle is part-way through. Step 1/4, and `POST /v1/vehicles` will create the vehicle.
    case fresh

    /// A vehicle with at least one saved step.
    ///
    /// - Parameters:
    ///   - vehicleId: The vehicle being onboarded.
    ///   - registrationNumber: What Step 1/4 stored, so the driver does not retype it.
    ///   - vehicleType: Same. `nil` only if the plate is on a type this app cannot offer.
    ///   - step: The first step that is not `StepVerdict.verified`.
    ///   - verdicts: All four verdicts, for the ⚑ states on the steps already saved.
    ///   - fields: Every extracted field on the vehicle, for the extract cards.
    case resume(
        vehicleId: String,
        registrationNumber: String,
        vehicleType: RideVehicleType?,
        step: OnboardingStep,
        verdicts: OnboardingStepVerdicts,
        fields: [ExtractedField]
    )
}

/// What one saved step answered.
///
/// - Parameters:
///   - vehicleId: The vehicle the step belongs to — minted by the first save when the wizard
///     started fresh.
///   - stepStatus: The verdict on the step just saved. `pendingReview` is what raises the ⚑
///     (BR-25.3).
///   - onboardingStatus: Derived incomplete / approved after the save (AL-30).
///   - registrationStatus: The vehicle's status after the save — `approved` is the auto-approval
///     that happens with no officer step at all (Change 6/22).
///   - nextStep: Where the wizard goes; `nil` when every step is verified.
struct SavedStep {
    let vehicleId: String
    let stepStatus: StepVerdict
    let onboardingStatus: OnboardingStatus
    let registrationStatus: RegistrationStatus
    let nextStep: OnboardingStep?
}

/// The Mode-C wizard's data layer — SCR-DI-004…004c, SCR-DI-006 and SCR-DI-026 all read it.
///
/// **Mode C, and only Mode C** (AL-27). Nothing here can register a Mode A/B vehicle: `POST
/// /v1/vehicles` pins `mode` to `ServiceMode.c` by contract `const` and the type is a
/// `RideVehicleType`, which has no `bus` and no `train`. Permits are not modelled at all — they are
/// the Fleet Portal's (SCR-FP-004).
///
/// **The wizard's persistence is the server's** (BR-25.4). Nothing about a part-finished vehicle is
/// kept on the handset: each step is saved as it is completed, and a cold start asks
/// `GET /v1/vehicles/mine` + `GET …/onboarding-status` where it left off. A local draft would be a
/// second source of truth for a state machine that already has one, and the one the driver carries
/// in their pocket is the one that gets wiped.
///
/// A protocol for the reason every cluster-1 seam is one: `RegistryApi` is a Kotlin interface and a
/// Kotlin type cannot be stood in for from Swift, so a model test with no gateway needs the seam on
/// this side of the bridge. ``ApiVehicleOnboardingRepository`` converts and forwards and decides
/// nothing; what these models do decide is what the tests cover.
protocol VehicleOnboardingRepository: AnyObject {

    /// Every vehicle this driver owns or has been assigned (US-2.8, US-13.9).
    func myVehicles() async throws -> [VehicleSummary]

    /// Where the wizard should open (AL-30).
    func resume() async throws -> ResumePoint

    /// The four verdicts and every field for one vehicle — half of SCR-DI-006's payload.
    func onboardingStatus(_ vehicleId: String) async throws -> VehicleOnboardingStatusResponse

    /// The vehicle itself — the other half.
    func vehicle(_ vehicleId: String) async throws -> VehicleDetail

    /// Step 1/4 for a vehicle that does not exist yet — `POST /v1/vehicles`.
    func start(registrationNumber: String, vehicleType: RideVehicleType) async throws -> SavedStep

    /// Step 1/4 for a vehicle that already exists — the driver stepped back to change something.
    func saveDetails(
        vehicleId: String,
        registrationNumber: String,
        vehicleType: RideVehicleType
    ) async throws -> SavedStep

    /// Steps 2–4 — the captured document goes up with the step in one request (Δ MCS-01).
    func saveDocument(
        vehicleId: String,
        step: OnboardingStep,
        front: CapturedImage,
        back: CapturedImage?
    ) async throws -> SavedStep

    /// A driver correction to a step that already saved its document (Δ MCS-02, BR-25.3).
    func saveCorrections(
        vehicleId: String,
        step: OnboardingStep,
        corrections: OnboardingCorrections
    ) async throws -> SavedStep

    /// `POST /v1/vehicles/{id}/deactivate` — US-2.16, and it releases the plate (D-37).
    func deactivate(_ vehicleId: String) async throws
}

/// ``VehicleOnboardingRepository`` over registry-svc.
final class ApiVehicleOnboardingRepository: VehicleOnboardingRepository {

    private let registry: RegistryApi

    init(registry: RegistryApi) {
        self.registry = registry
    }

    func myVehicles() async throws -> [VehicleSummary] {
        try await registry.listMyVehicles().items
    }

    /// Where the wizard should open (AL-30).
    ///
    /// The vehicle it resumes is the driver's **own Mode C** one that is still `incomplete`. A
    /// temporarily-assigned Mode A/B vehicle is never onboarded here (AL-27) and an approved one is
    /// finished, so both fall through to ``ResumePoint/fresh`` — which is exactly US-2.27's "the ＋
    /// button starts a NEW vehicle when the current one is approved".
    func resume() async throws -> ResumePoint {
        let incomplete = try await myVehicles().first {
            $0.isOnboardable && $0.onboardingStatus == OnboardingStatus.incomplete
        }
        guard let incomplete else { return .fresh }

        let status = try await registry.getVehicleOnboardingStatus(vehicleId: incomplete.vehicleId)

        return .resume(
            vehicleId: incomplete.vehicleId,
            registrationNumber: incomplete.registrationNumber,
            vehicleType: RideVehicleType.companion.from(vehicleType: incomplete.vehicleType),
            // `nextStep` is the server's own resume pointer. Falling back to the first non-verified
            // verdict rather than to Step 1 keeps AL-30 true even if it is absent.
            step: status.nextStep ?? status.steps.firstUnverified ?? OnboardingStep.photos,
            verdicts: status.steps,
            fields: status.fields
        )
    }

    func onboardingStatus(_ vehicleId: String) async throws -> VehicleOnboardingStatusResponse {
        try await registry.getVehicleOnboardingStatus(vehicleId: vehicleId)
    }

    /// The vehicle itself.
    ///
    /// SCR-DI-006's header is *"Sedan · ABC-1234"* and the onboarding-status read carries neither
    /// the type nor the plate, so the two reads go together.
    func vehicle(_ vehicleId: String) async throws -> VehicleDetail {
        try await registry.getVehicle(vehicleId: vehicleId)
    }

    /// Step 1/4 for a vehicle that does not exist yet — `POST /v1/vehicles`.
    ///
    /// **The registration is the `details` step** (Δ C029): this request carries exactly the type
    /// and plate that step stores, D5' §14.1a verifies it on entry, and the response's `nextStep` is
    /// therefore `insurance`. Asking for the plate again on Step 1 would be a screen the driver has
    /// already filled in.
    ///
    /// `409 registration-exists` when the plate is on a live vehicle (D-37).
    func start(registrationNumber: String, vehicleType: RideVehicleType) async throws -> SavedStep {
        let response = try await registry.registerVehicle(
            request: VehicleRegistration(
                registrationNumber: registrationNumber.trimmingCharacters(in: .whitespacesAndNewlines),
                vehicleType: vehicleType,
                mode: ServiceMode.c,
                insuranceFileId: nil,
                revenueLicenseFileId: nil,
                vehiclePhotoFrontFileId: nil,
                vehiclePhotoBackFileId: nil,
                driverName: nil,
                driverPhotoFileId: nil
            ),
            idempotencyKey: nil
        )

        return SavedStep(
            vehicleId: response.vehicleId,
            stepStatus: response.verification.vehicleDetails,
            onboardingStatus: response.onboardingStatus,
            registrationStatus: response.status,
            nextStep: response.nextStep ?? OnboardingStep.insurance
        )
    }

    func saveDetails(
        vehicleId: String,
        registrationNumber: String,
        vehicleType: RideVehicleType
    ) async throws -> SavedStep {
        let response = try await registry.uploadVehicleOnboardingStep(
            vehicleId: vehicleId,
            step: OnboardingStep.details,
            registrationNumber: registrationNumber.trimmingCharacters(in: .whitespacesAndNewlines),
            vehicleType: vehicleType,
            file: nil,
            fileBack: nil,
            corrections: nil
        )
        return Self.savedStep(response, vehicleId: vehicleId)
    }

    /// Steps 2–4 — the captured document goes up with the step in one request (Δ MCS-01).
    ///
    /// One request and not two because there is no id for a client to mint: registry-svc writes
    /// `docs.uploads` and `registry.onboarding_steps` in the same call, and each image carries its
    /// own `CaptureSource` (AL-43) rather than the form carrying one for both — a driver can scan the
    /// front and pick the back out of the gallery.
    ///
    /// - Parameter back: Step 4/4 only. One photograph cannot show a vehicle's front and back plates.
    func saveDocument(
        vehicleId: String,
        step: OnboardingStep,
        front: CapturedImage,
        back: CapturedImage?
    ) async throws -> SavedStep {
        let response = try await registry.uploadVehicleOnboardingStep(
            vehicleId: vehicleId,
            step: step,
            registrationNumber: nil,
            vehicleType: nil,
            file: front.asDocument(),
            fileBack: back?.asDocument(),
            corrections: nil
        )
        return Self.savedStep(response, vehicleId: vehicleId)
    }

    /// A driver correction to a step that already saved its document (Δ MCS-02, BR-25.3).
    ///
    /// No file: the document is already on record and the correction is applied to it. The value
    /// lands `source='manual'`, `verifyStatus='pending'` — **registry-svc stamps that**, not the
    /// client — and takes the step back to `pendingReview`, which is the rule rather than a failure:
    /// BR-25.2 lets the driver carry on and trusts the value once an officer confirms it.
    func saveCorrections(
        vehicleId: String,
        step: OnboardingStep,
        corrections: OnboardingCorrections
    ) async throws -> SavedStep {
        let response = try await registry.uploadVehicleOnboardingStep(
            vehicleId: vehicleId,
            step: step,
            registrationNumber: nil,
            vehicleType: nil,
            file: nil,
            fileBack: nil,
            corrections: corrections
        )
        return Self.savedStep(response, vehicleId: vehicleId)
    }

    func deactivate(_ vehicleId: String) async throws {
        try await registry.deactivateVehicle(vehicleId: vehicleId, idempotencyKey: nil)
    }

    private static func savedStep(_ response: SaveOnboardingStepResponse, vehicleId: String) -> SavedStep {
        SavedStep(
            vehicleId: vehicleId,
            stepStatus: response.stepStatus,
            onboardingStatus: response.onboardingStatus,
            registrationStatus: response.status,
            nextStep: response.nextStep
        )
    }
}

// MARK: - The two rules that are properties of a vehicle

extension VehicleSummary {

    /// Whether this vehicle is one the **driver app** onboards — Mode C, owned (AL-27).
    var isOnboardable: Bool { mode == ServiceMode.c }

    /// Whether this vehicle can be selected to go live (US-9.6, D5' §14.1a).
    ///
    /// An owned Mode C vehicle has to be **approved**: AL-30 makes `onboardingStatus` the gate and
    /// `status` the registration decision, and both have to be good. A shared or temporarily-assigned
    /// Mode A/B vehicle carries neither — the Fleet Portal approved it — so it is eligible on the
    /// strength of being assigned at all.
    var canGoLive: Bool {
        if isOnboardable {
            return status == RegistrationStatus.approved && onboardingStatus == OnboardingStatus.approved
        }
        return status == RegistrationStatus.approved
    }
}

extension OnboardingStepVerdicts {

    /// This step's verdict. The read DTO keys them by name, so this is the one place that maps them.
    func verdict(for step: OnboardingStep) -> StepVerdict {
        switch step {
        case OnboardingStep.details: return details
        case OnboardingStep.insurance: return insurance
        case OnboardingStep.revenue: return revenue
        // `photos` and the arm a Kotlin enum forces on every Swift `switch` over one. There is no
        // fifth step: `OnboardingStep` is `registry.onboarding_steps.step`'s CHECK domain.
        default: return photos
        }
    }

    /// The first step that is not verified, or `nil` when all four are (AL-30).
    var firstUnverified: OnboardingStep? {
        VehicleOnboardingSteps.all.first { verdict(for: $0) != StepVerdict.verified }
    }

    /// The four rows SCR-DI-006 lists, in the order the wireframe draws them.
    var rows: [StepVerdictRow] {
        VehicleOnboardingSteps.all.map { StepVerdictRow(step: $0, verdict: verdict(for: $0)) }
    }
}

/// One row of SCR-DI-006's `Document verification (4)` card.
///
/// A named type rather than the tuple its Android twin uses, and the reason is the platform's: a
/// `ForEach` needs an `Identifiable` element or a key path to a stable id, and **Swift has no key
/// paths into tuples** — so a pair would have to be indexed by position, which is exactly the thing
/// that reorders a list when the underlying data changes.
struct StepVerdictRow: Identifiable {

    let step: OnboardingStep
    let verdict: StepVerdict

    /// The step's own wire value — `details`, `insurance`, `revenue`, `photos`.
    var id: String { step.wire }
}

extension ExtractedField {

    /// Whether this field still has to be looked at by a Verification Officer (AL-29).
    var needsOfficerReview: Bool {
        verifyStatus == VerifyStatus.pending || (value?.isEmpty ?? true)
    }

    /// Gemini Flash's confidence, unboxed.
    ///
    /// A nullable Kotlin `Double` crosses the bridge as a `KotlinDouble?`, so every reader would
    /// otherwise write `.doubleValue` at the call site and one of them would eventually forget the
    /// optional and print `Optional(0.62)` on a driver's screen.
    var confidenceValue: Double? { confidence?.doubleValue }
}

/// The driver's retyped values as the client's typed correction set (Δ MCS-02).
///
/// A dictionary in the model and a named type on the wire: the screen collects by
/// `registry.document_fields` key, and `OnboardingCorrections` is what stops a key the step does not
/// accept — `reg_no_match` above all — reaching the request at all.
func onboardingCorrections(from typed: [String: String]) -> OnboardingCorrections {
    OnboardingCorrections(
        insuranceExpiry: typed[VehicleFieldKeys.insuranceExpiry]?.nilIfBlank,
        insurancePolicyNo: typed[VehicleFieldKeys.insurancePolicyNo]?.nilIfBlank,
        revenueNo: typed[VehicleFieldKeys.revenueNo]?.nilIfBlank,
        revenueExpiry: typed[VehicleFieldKeys.revenueExpiry]?.nilIfBlank
    )
}
