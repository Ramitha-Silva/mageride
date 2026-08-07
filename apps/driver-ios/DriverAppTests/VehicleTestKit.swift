import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures cluster 2's models are driven by.
///
/// Same rule as ``OnboardingTestKit``: every seam here is a **Swift protocol**, because `RegistryApi`
/// is a Kotlin interface and a Kotlin type cannot be stood in for from Swift at all. The DTOs
/// underneath are the real shared ones — a fixture is built with the same initialiser registry-svc's
/// response deserialises into, so a contract change fails these tests rather than a driver's phone.

// MARK: - The vehicle every C087 test is about

/// The vehicle every C087 test is about.
let testVehicleId = "01JVEHICLE0000000000000001"

/// A second one, for the "only one can be live" and the two-group rules.
let testOtherVehicleId = "01JVEHICLE0000000000000002"

/// A vehicle as `GET /v1/vehicles/mine` returns it.
///
/// Mode C by default because that is what the Driver App onboards (AL-27); pass `ServiceMode.a` or
/// `.b` for the temporarily-assigned fleet vehicle of US-13.9.
func summary(
    vehicleId: String = testVehicleId,
    registrationNumber: String = "ABC-1234",
    vehicleType: VehicleType = VehicleType.sedan,
    mode: ServiceMode = ServiceMode.c,
    status: RegistrationStatus = RegistrationStatus.pending,
    onboardingStatus: OnboardingStatus = OnboardingStatus.incomplete,
    fleetName: String? = nil
) -> VehicleSummary {
    VehicleSummary(
        vehicleId: vehicleId,
        registrationNumber: registrationNumber,
        vehicleType: vehicleType,
        mode: mode,
        status: status,
        onboardingStatus: onboardingStatus,
        modeBBilling: nil,
        defaultMonthlyFareMinor: nil,
        fleetName: fleetName,
        assignedUntil: nil
    )
}

/// The four step verdicts, defaulting to "nothing saved yet".
func verdicts(
    details: StepVerdict = StepVerdict.pendingInput,
    insurance: StepVerdict = StepVerdict.pendingInput,
    revenue: StepVerdict = StepVerdict.pendingInput,
    photos: StepVerdict = StepVerdict.pendingInput
) -> OnboardingStepVerdicts {
    OnboardingStepVerdicts(details: details, insurance: insurance, revenue: revenue, photos: photos)
}

/// `GET /v1/vehicles/{id}/onboarding-status`.
func statusResponse(
    steps: OnboardingStepVerdicts,
    nextStep: OnboardingStep? = nil,
    status: RegistrationStatus = RegistrationStatus.pending,
    onboardingStatus: OnboardingStatus = OnboardingStatus.incomplete,
    fields: [ExtractedField] = []
) -> VehicleOnboardingStatusResponse {
    VehicleOnboardingStatusResponse(
        status: status,
        onboardingStatus: onboardingStatus,
        nextStep: nextStep,
        steps: steps,
        fields: fields
    )
}

/// `GET /v1/vehicles/{id}` — SCR-DI-006's header comes from this one.
func detail(
    registrationNumber: String = "ABC-1234",
    vehicleType: VehicleType = VehicleType.sedan,
    status: RegistrationStatus = RegistrationStatus.pending,
    onboardingStatus: OnboardingStatus = OnboardingStatus.incomplete,
    rejectionReason: String? = nil
) -> VehicleDetail {
    VehicleDetail(
        vehicleId: testVehicleId,
        registrationNumber: registrationNumber,
        vehicleType: vehicleType,
        mode: ServiceMode.c,
        status: status,
        onboardingStatus: onboardingStatus,
        modeBBilling: nil,
        defaultMonthlyFareMinor: nil,
        dispatchState: nil,
        rejectionReason: rejectionReason,
        fleetId: nil,
        driver: nil,
        documents: nil,
        createdAt: nil
    )
}

/// One saved step.
func savedStep(
    vehicleId: String = testVehicleId,
    stepStatus: StepVerdict = StepVerdict.verified,
    nextStep: OnboardingStep? = nil,
    onboardingStatus: OnboardingStatus = OnboardingStatus.incomplete,
    registrationStatus: RegistrationStatus = RegistrationStatus.pending
) -> SavedStep {
    SavedStep(
        vehicleId: vehicleId,
        stepStatus: stepStatus,
        onboardingStatus: onboardingStatus,
        registrationStatus: registrationStatus,
        nextStep: nextStep
    )
}

/// One extracted field, pending or confirmed (AL-29).
func field(
    key: String,
    value: String?,
    verifyStatus: VerifyStatus = VerifyStatus.confirmed
) -> ExtractedField {
    ExtractedField(
        key: key,
        value: value,
        source: FieldSource.ai,
        confidence: KotlinDouble(double: verifyStatus == VerifyStatus.confirmed ? 0.97 : 0.42),
        verifyStatus: verifyStatus
    )
}

/// A server failure carrying [code], wrapped exactly as Kotlin/Native delivers one.
///
/// **A Kotlin exception does not cross the bridge as itself** — it arrives as an `NSError` with the
/// original under `KotlinException` — so a fake that threw a bare `MageRideError` would exercise a
/// path the app never takes. `ProblemDetails.code` is derived from the last segment of `type`
/// (`ErrorCode.fromWire`), which is why the URL is built rather than the code passed twice.
///
/// Always a `Conflict`, whatever [status] says. `OnboardingErrors` branches on the **code** for
/// everything cluster 2 can meet — only the transport failures and `413` are matched by type — so a
/// second arm here would test the fake rather than the table.
func apiFailure(code: String, status: Int32 = 409) -> NSError {
    let problem = ProblemDetails(
        type: "https://mageride.lk/errors/\(code)",
        title: code,
        status: status,
        detail: nil,
        instance: nil,
        traceId: nil,
        errors: nil,
        updateUrl: nil,
        latestVersion: nil,
        isMandatory: nil
    )
    return NSError(
        domain: "KotlinException",
        code: 0,
        userInfo: ["KotlinException": MageRideErrorConflict(problem: problem)]
    )
}

// MARK: - Repository

/// ``VehicleOnboardingRepository`` with no gateway behind it.
///
/// Every call is recorded rather than only counted: half of what C087's rules say is *which* request
/// went out — a correction with no file (Δ MCS-02), `POST /v1/vehicles` instead of a `details` save
/// on a fresh wizard (Δ C029) — and a counter cannot tell those apart.
final class FakeVehicleOnboardingRepository: VehicleOnboardingRepository {

    var vehicles: [VehicleSummary] = []
    var resumePoint: ResumePoint = .fresh
    var status = statusResponse(steps: verdicts())
    var vehicleDetail = detail()
    var nextSavedStep = savedStep()

    /// What the next call throws, or `nil` to succeed. Cleared once it has been thrown.
    var nextFailure: Error?

    private(set) var myVehiclesCount = 0
    private(set) var resumeCount = 0
    private(set) var statusReads: [String] = []
    private(set) var started: [(registrationNumber: String, vehicleType: RideVehicleType)] = []
    private(set) var savedDetails: [(vehicleId: String, registrationNumber: String)] = []
    private(set) var savedDocuments: [(step: OnboardingStep, front: CapturedImage, back: CapturedImage?)] = []
    private(set) var savedCorrections: [(step: OnboardingStep, corrections: OnboardingCorrections)] = []
    private(set) var deactivated: [String] = []

    func myVehicles() async throws -> [VehicleSummary] {
        myVehiclesCount += 1
        try throwIfProgrammed()
        return vehicles
    }

    func resume() async throws -> ResumePoint {
        resumeCount += 1
        try throwIfProgrammed()
        return resumePoint
    }

    func onboardingStatus(_ vehicleId: String) async throws -> VehicleOnboardingStatusResponse {
        statusReads.append(vehicleId)
        try throwIfProgrammed()
        return status
    }

    func vehicle(_ vehicleId: String) async throws -> VehicleDetail {
        try throwIfProgrammed()
        return vehicleDetail
    }

    func start(registrationNumber: String, vehicleType: RideVehicleType) async throws -> SavedStep {
        started.append((registrationNumber, vehicleType))
        try throwIfProgrammed()
        return nextSavedStep
    }

    func saveDetails(
        vehicleId: String,
        registrationNumber: String,
        vehicleType: RideVehicleType
    ) async throws -> SavedStep {
        savedDetails.append((vehicleId, registrationNumber))
        try throwIfProgrammed()
        return nextSavedStep
    }

    func saveDocument(
        vehicleId: String,
        step: OnboardingStep,
        front: CapturedImage,
        back: CapturedImage?
    ) async throws -> SavedStep {
        savedDocuments.append((step, front, back))
        try throwIfProgrammed()
        return nextSavedStep
    }

    func saveCorrections(
        vehicleId: String,
        step: OnboardingStep,
        corrections: OnboardingCorrections
    ) async throws -> SavedStep {
        savedCorrections.append((step, corrections))
        try throwIfProgrammed()
        return nextSavedStep
    }

    func deactivate(_ vehicleId: String) async throws {
        deactivated.append(vehicleId)
        try throwIfProgrammed()
    }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

// MARK: - Local stores

/// ``ActiveVehicleStore`` in memory. `UserDefaults` would leak between tests through the standard
/// suite, and D-03's single-publisher rule is a function of exactly this one value.
final class FakeActiveVehicleStore: ActiveVehicleStore {

    var activeVehicleId: String?

    init(activeVehicleId: String? = nil) {
        self.activeVehicleId = activeVehicleId
    }
}

/// ``CameraAuthoriser`` with no system behind it.
@MainActor
final class FakeCameraAuthoriser: CameraAuthoriser {

    var authorisation: CameraAuthorisation = .granted
    var isScannerSupported = true

    /// Whether VisionKit's **code** scanner can run (Δ C092). `false` is every simulator and every
    /// device older than the A12 — the state SCR-DI-027 answers with a disabled Scan button.
    var isCodeScannerSupported = true

    /// What ``request()`` answers.
    var grantsOnRequest = true

    private(set) var requestCount = 0
    private(set) var settingsOpenedCount = 0

    func request() async -> Bool {
        requestCount += 1
        authorisation = grantsOnRequest ? .granted : .blocked
        return grantsOnRequest
    }

    func openSettings() {
        settingsOpenedCount += 1
    }
}
