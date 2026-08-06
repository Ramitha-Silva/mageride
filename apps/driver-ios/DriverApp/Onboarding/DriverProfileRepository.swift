import Foundation
import MageRideShared

/// The four licence fields Gemini Flash extracts, as the keys `registry.document_fields` stores
/// them (AL-29; `DocumentFieldKeys` in registry-svc).
///
/// Machine keys, never display copy — the row labels are `Localizable.strings`' and are trilingual.
enum LicenceFieldKeys {
    static let licenceNo = "licence_no"
    static let licenceExpiry = "licence_expiry"
    static let nicNo = "nic_no"
    static let allowedVehicleTypes = "allowed_vehicle_types"

    /// The order SCR-DI-003a's extract card lists them in.
    static let order = [licenceNo, licenceExpiry, nicNo, allowedVehicleTypes]
}

/// What the driver has filled in on SCR-DI-003a.
///
/// - Parameters:
///   - name: `registry.driver_profiles.display_name`, at most 200 characters.
///   - photo: Required (US-2.12) — a passenger has to be able to recognise the driver.
///   - licenceFront: Front of the driving licence, captured through SCR-DI-005.
///   - licenceBack: Back of the same licence.
///   - nicNo: Typed only when the scan was unclear; goes up as `source='manual'` (AL-29).
///   - allowedVehicleTypes: Same, for the licence classes.
///   - licenceNo: Same, for the licence number.
///   - licenceExpiry: Same, for its expiry.
struct ProfileDraft: Equatable {

    var name: String = ""
    var photo: CapturedImage?
    var licenceFront: CapturedImage?
    var licenceBack: CapturedImage?
    var nicNo: String?
    var allowedVehicleTypes: [VehicleType]?
    var licenceNo: String?
    var licenceExpiry: String?

    /// Whether Save is allowed: name, photo and both licence sides are all required (AL-27).
    var isComplete: Bool {
        !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && photo != nil
            && licenceFront != nil
            && licenceBack != nil
    }

    /// Just the driver-typed values, for comparing one save against the next.
    ///
    /// The images are deliberately not part of it: what a second CTA tap needs to know is whether a
    /// **correction** was typed, not whether the same photograph is still in memory.
    var manualValues: [String] {
        [
            nicNo?.nilIfBlank,
            allowedVehicleTypes?.isEmpty == false ? allowedVehicleTypes?.map(\.wire).joined(separator: ",") : nil,
            licenceNo?.nilIfBlank,
            licenceExpiry?.nilIfBlank,
        ].map { $0 ?? "" }
    }
}

/// One row of SCR-DI-003a's "✦ AI-extracted from licence" card.
///
/// - Parameters:
///   - key: The `registry.document_fields` key, e.g. `nic_no`.
///   - value: What was read or typed; `nil` when extraction found nothing.
///   - source: Whether OCR or the driver produced it (AL-29).
///   - needsOfficerReview: Whether this field is flagged ⚑ for a Verification Officer.
struct LicenceField: Equatable, Identifiable {
    let key: String
    let value: String?
    let source: FieldSource
    let needsOfficerReview: Bool

    var id: String { key }

    /// Whether the driver typed this one, which is what the ⚑ chip's copy explains.
    var isManual: Bool { source == FieldSource.manual }
}

/// The verdict SCR-DI-003a shows after a save.
struct LicenceExtraction: Equatable {

    /// The four licence rows, in ``LicenceFieldKeys/order``.
    let fields: [LicenceField]

    /// The name registry-svc stored, which is not always the one that was sent.
    let displayName: String?

    /// Whether the driver has to look at this before continuing.
    ///
    /// BR-25.2: a field is pending when it is `manual` **or** low confidence, and a required field
    /// that extraction never returned comes back with a null value. Either way the card is the
    /// point of the screen and skipping past it would hide the ⚑.
    var needsReview: Bool {
        fields.contains { $0.needsOfficerReview || ($0.value?.isEmpty ?? true) }
    }

    /// Whether any field is flagged for the Verification Officer queue (SCR-AP-003).
    var hasOfficerFlag: Bool { fields.contains(where: \.needsOfficerReview) }

    /// The row for one key, or `nil` when the server did not return it.
    func field(_ key: String) -> LicenceField? { fields.first { $0.key == key } }
}

/// SCR-DI-003a's data: upload the three images, `PUT /v1/drivers/profile`, read back the verdicts.
///
/// A protocol so ``ProfileSetupModel`` and ``SplashModel`` can be driven by a fake.
protocol DriverProfileRepository: AnyObject {

    /// `GET /v1/users/me` — the splash router's input for "has this driver a profile yet?".
    ///
    /// Answers the stored display name, which D1' A.1 makes the entry condition for Profile Setup
    /// on both apps.
    func profileName() async throws -> String?

    /// `PUT /v1/drivers/profile` — the three images and the name, in one request.
    func submit(_ draft: ProfileDraft) async throws -> LicenceExtraction
}

/// ``DriverProfileRepository`` over registry-svc and iam-svc.
///
/// **Profile Setup is driver identity and nothing else** (AL-27, Change 6/22). It writes
/// `registry.driver_profiles` plus a **vehicle-less** `registry.documents(kind='driving_license')`
/// and it precedes Home; no vehicle is involved, and the Mode-C wizard that onboards one is
/// optional and belongs to C087.
final class ApiDriverProfileRepository: DriverProfileRepository {

    private let registry: RegistryApi
    private let iam: IamApi

    init(registry: RegistryApi, iam: IamApi) {
        self.registry = registry
        self.iam = iam
    }

    func profileName() async throws -> String? {
        try await iam.getMyProfile().firstName
    }

    /// The multipart arm (Δ MCS-01): registry-svc writes each image to `docs.uploads` and the
    /// profile row in the same call, so there is no id for a client to mint and nothing to hold
    /// between two requests on a mobile network. Each image carries its own capture source (AL-43)
    /// — the avatar came from the picker, the licence from SCR-DI-005 — and that provenance is what
    /// the Verification-Officer queue sorts on.
    ///
    /// A driver-supplied value is sent as-is: **registry-svc** is what stamps it `source='manual'`,
    /// `verify_status='pending'` and queues the officer review (AL-29, US-2.4a). The client never
    /// claims a provenance of its own.
    func submit(_ draft: ProfileDraft) async throws -> LicenceExtraction {
        guard
            let photo = draft.photo,
            let front = draft.licenceFront,
            let back = draft.licenceBack,
            draft.isComplete
        else {
            preconditionFailure("Profile Setup needs a name, a photo and both licence sides")
        }

        let response = try await registry.uploadDriverProfile(
            driverName: draft.name.trimmingCharacters(in: .whitespacesAndNewlines),
            photo: photo.asDocument(),
            licenseFront: front.asDocument(),
            licenseBack: back.asDocument(),
            nicNo: draft.nicNo?.nilIfBlank,
            allowedVehicleTypes: draft.allowedVehicleTypes?.isEmpty == false ? draft.allowedVehicleTypes : nil,
            licenceNo: draft.licenceNo?.nilIfBlank,
            licenceExpiry: draft.licenceExpiry?.nilIfBlank
        )

        return Self.extraction(
            of: response.fields,
            fallbackName: draft.name.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    /// Maps the server's fields onto the four rows the card draws, in the card's own order.
    ///
    /// A key the server did not answer for at all has not been verified either; the screen shows it
    /// as unread, which is the same prompt to type it in.
    static func extraction(of fields: [ExtractedField], fallbackName: String) -> LicenceExtraction {
        let byKey = Dictionary(fields.map { ($0.key, $0) }, uniquingKeysWith: { first, _ in first })
        let rows = LicenceFieldKeys.order.map { key -> LicenceField in
            let field = byKey[key]
            return LicenceField(
                key: key,
                value: field?.value,
                source: field?.source ?? FieldSource.ai,
                needsOfficerReview: field == nil || field?.verifyStatus == VerifyStatus.pending
            )
        }
        return LicenceExtraction(fields: rows, displayName: fallbackName)
    }
}

extension String {

    /// `nil` when this is blank, so a whitespace-only correction is not sent as one.
    var nilIfBlank: String? {
        trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : self
    }
}
