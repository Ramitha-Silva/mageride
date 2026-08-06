import Foundation
import MageRideShared

/// SCR-DI-003a's state.
///
/// - Parameters:
///   - draft: What the driver has filled in.
///   - extraction: The verdict card, once a save has come back. `nil` before the first one.
///   - isBusy: A save is in flight.
///   - errorKey: Resolved copy for the last failure.
///   - editingKey: The extracted row whose ✎ is open, or `nil`.
///   - isDone: Set when Profile Setup is finished; the screen navigates to SCR-DI-007.
struct ProfileSetupState {

    var draft = ProfileDraft()
    var extraction: LicenceExtraction?
    var isBusy = false
    var errorKey: String?
    var editingKey: String?
    var isDone = false

    /// Name, photo and both licence sides — all three are required before Save (AL-27).
    var canSave: Bool { !isBusy && draft.isComplete }

    /// Whether the ⚑ banner is up: at least one field is manual, doubtful or unread (BR-25.2).
    var hasOfficerFlag: Bool { extraction?.hasOfficerFlag == true }
}

/// SCR-DI-003a — driver identity, and the only thing standing between a new driver and Home.
///
/// **No vehicle is involved** (AL-27, Change 6/22). This screen writes `registry.driver_profiles`
/// and one vehicle-less `registry.documents(kind='driving_license')`; the Mode-C wizard that
/// onboards a vehicle is optional, comes later, and belongs to C087.
///
/// The save is a two-beat interaction, and that is the wireframe's, not an invention:
/// `PUT /v1/drivers/profile` is what *queues* the Gemini Flash extraction, so the "✦ AI-extracted"
/// card cannot exist before the first save. The first Save reveals it; if every field came back
/// confirmed the screen continues immediately, and if anything is doubtful, unread or typed the
/// driver is left on the card to correct it — with the ⚑ chip saying the value goes to a
/// Verification Officer either way (AL-29, US-2.4a).
@MainActor
final class ProfileSetupModel: ObservableObject {

    @Published private(set) var state = ProfileSetupState()

    private let profiles: DriverProfileRepository
    private let captures: DocumentCaptureCoordinator

    /// The draft the last successful save carried, so a second CTA tap knows if anything changed.
    private var lastSubmitted: ProfileDraft?

    init(profiles: DriverProfileRepository, captures: DocumentCaptureCoordinator) {
        self.profiles = profiles
        self.captures = captures
    }

    // MARK: - The form

    func onNameChanged(_ name: String) {
        state.draft.name = name
        state.errorKey = nil
    }

    /// The avatar's camera badge. The photo comes from `PhotosPicker`, which the wireframe itself
    /// specifies (Δ iOS) — an avatar is not a document, so it does not go through the scanner.
    func onPhotoPicked(_ image: CapturedImage) {
        state.draft.photo = image
        state.errorKey = nil
    }

    /// A licence tile was tapped. The scanner is SCR-DI-005's; this only says which slot.
    func requestCapture(_ target: DocumentCaptureTarget) {
        captures.open(target)
    }

    /// SCR-DI-005 (C087) hands its cropped, de-skewed image back here.
    ///
    /// The four vehicle-document slots belong to C087's wizard and are ignored — the coordinator is
    /// shared, and a result this screen did not ask for is not this screen's.
    func apply(_ result: DocumentCaptureResult) {
        switch result.target {
        case .licenceFront: state.draft.licenceFront = result.image
        case .licenceBack: state.draft.licenceBack = result.image
        default: return
        }
        state.errorKey = nil
        captures.consume()
    }

    /// The driver typed a NIC the scan could not read.
    ///
    /// Stored on the draft and sent on the next save. The **client never stamps a provenance** —
    /// registry-svc is what writes `source='manual'`, `verify_status='pending'` and queues the
    /// officer review, because a client that could claim `source='ai'` would make AL-29 advisory.
    func onNicChanged(_ nic: String) {
        state.draft.nicNo = nic
        state.errorKey = nil
    }

    /// Same, for the licence classes the scan could not read.
    func onAllowedVehicleTypesChanged(_ types: [VehicleType]) {
        state.draft.allowedVehicleTypes = types
        state.errorKey = nil
    }

    /// The ✎ on an extracted row. A second tap closes it.
    func toggleEdit(_ key: String) {
        state.editingKey = state.editingKey == key ? nil : key
    }

    /// The driver retyped the licence number or its expiry.
    func onLicenceFieldChanged(key: String, value: String) {
        switch key {
        case LicenceFieldKeys.licenceNo: state.draft.licenceNo = value
        case LicenceFieldKeys.licenceExpiry: state.draft.licenceExpiry = value
        default: return
        }
        state.errorKey = nil
    }

    // MARK: - Saving

    /// The CTA. `PUT /v1/drivers/profile` when there is something to send, otherwise onward.
    ///
    /// Three cases, and the wireframe draws all three under one "Save & continue" bar:
    ///
    /// * **No card yet** — save, which is what queues the extraction. If nothing came back
    ///   doubtful, unread or manual there is nothing to review and the screen continues.
    /// * **A card, and the driver has typed a correction since it appeared** — save again, then
    ///   continue whatever the new verdicts are. A `manual` field is pending *by design* (BR-25.2),
    ///   so waiting for it to clear would trap the driver on this screen forever.
    /// * **A card and nothing new to send** — continue. Re-running the extraction over the same
    ///   images would only produce the same verdicts.
    func save() async {
        guard state.canSave else { return }

        let isFirstSave = state.extraction == nil
        let correctionTyped = state.draft.manualValues != lastSubmitted?.manualValues
        if !isFirstSave && !correctionTyped {
            state.isDone = true
            return
        }
        await submit(continueAfterwards: !isFirstSave)
    }

    private func submit(continueAfterwards: Bool) async {
        state.isBusy = true
        state.errorKey = nil

        let draft = state.draft
        do {
            let extraction = try await profiles.submit(draft)
            lastSubmitted = draft
            state.extraction = extraction
            state.isDone = continueAfterwards || !extraction.needsReview
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }
}
