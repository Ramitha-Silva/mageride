import MageRideShared
import SwiftUI

// The Mode-C wizard's copy table — which `Localizable.strings` key each step, verdict, status and
// extracted field renders as.
//
// Split from the screens because it is a TABLE, not a layout: eight small mappings between a machine
// key and a trilingual label, several of which more than one of C087's four screens reads. It is the
// same file `apps/driver-android/.../vehicle/VehicleOnboardingLabels.kt` is, with the same keys, so
// a divergence between the two driver apps is a diff.

extension OnboardingStep {

    /// The navigation-bar title for each step — the wireframe's four.
    var titleKey: String {
        switch self {
        case OnboardingStep.insurance: return "vehicle_onboard_insurance_title"
        case OnboardingStep.revenue: return "vehicle_onboard_revenue_title"
        case OnboardingStep.photos: return "vehicle_onboard_photos_title"
        // `details`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default: return "vehicle_onboard_title"
        }
    }

    /// The line under the progress bar — *"Step 2 of 4 · Insurance card or paper"*.
    var captionKey: String {
        switch self {
        case OnboardingStep.insurance: return "vehicle_onboard_insurance_caption"
        case OnboardingStep.revenue: return "vehicle_onboard_revenue_caption"
        case OnboardingStep.photos: return "vehicle_onboard_photos_caption"
        default: return "vehicle_onboard_details_caption"
        }
    }

    /// The CTA label. Step 4/4's says *submit for review*.
    var ctaKey: String {
        switch self {
        case OnboardingStep.insurance: return "vehicle_onboard_cta_insurance"
        case OnboardingStep.revenue: return "vehicle_onboard_cta_revenue"
        case OnboardingStep.photos: return "vehicle_onboard_cta_submit"
        default: return "vehicle_onboard_cta_details"
        }
    }

    /// The document name this step stands for — SCR-DI-006's four rows.
    var documentKey: String {
        switch self {
        case OnboardingStep.insurance: return "vehicle_document_insurance"
        case OnboardingStep.revenue: return "vehicle_document_revenue"
        case OnboardingStep.photos: return "vehicle_document_photos"
        default: return "vehicle_document_details"
        }
    }
}

extension StepVerdict {

    /// The verdict as the driver reads it.
    ///
    /// `pendingInput` and `pendingReview` are deliberately different copy: the first is *"not
    /// uploaded"* and the driver can fix it, the second is *"an officer is looking at it"* and they
    /// cannot. Collapsing the two into "Pending" would tell a driver to re-upload a document that is
    /// already in the queue.
    var labelKey: String {
        switch self {
        case StepVerdict.verified: return "verdict_verified"
        case StepVerdict.pendingReview: return "verdict_pending_review"
        default: return "verdict_pending_input"
        }
    }

    var tone: StatusTone {
        switch self {
        case StepVerdict.verified: return .done
        case StepVerdict.pendingReview: return .pending
        default: return .neutral
        }
    }
}

extension RegistrationStatus {

    /// The registration status as the driver reads it (US-2.13).
    var labelKey: String {
        switch self {
        case RegistrationStatus.approved: return "vehicle_registration_approved"
        case RegistrationStatus.rejected: return "vehicle_registration_rejected"
        case RegistrationStatus.deactivated: return "vehicle_registration_deactivated"
        default: return "vehicle_registration_pending"
        }
    }
}

extension VehicleType {

    /// The trilingual display name for a canonical vehicle type (AL-09).
    ///
    /// Built from ``wire`` rather than a ten-arm table because the keys **are** the wire spellings —
    /// `vehicle_type_three_wheeler` is `registry.vehicles.vehicle_type`'s own value with a prefix —
    /// and ``ProfileSetupScreen`` already names its chips the same way. `VehicleLabelTests` walks all
    /// ten and asserts each key resolves, so a type added to the enum fails a test rather than
    /// rendering its own key on a driver's screen.
    var labelKey: String { "vehicle_type_" + wire }
}

extension RideVehicleType {

    /// The same, for the Mode-C subset the driver app can offer (`bus` and `train` are Fleet
    /// Portal's). The two enums share their wire spellings, so they share their keys.
    var labelKey: String { "vehicle_type_" + wire }
}

extension VehicleToken {

    /// MAP-03's legend colour for a canonical vehicle type (D2' §0.2).
    ///
    /// The same eleven hexes the map markers use, so the dot beside *"Three-wheeler · ABC-1234"* on
    /// My Vehicles is the colour that vehicle is on the map.
    ///
    /// Keyed off the `VehicleType` enum rather than through ``VehicleToken/forWire(_:)``: that
    /// lookup takes the token's own camel-case spelling (`ThreeWheeler`) and `:shared`'s wire value
    /// is snake case (`three_wheeler`), so it answers `nil` for three of the ten. See the C087
    /// handoff — the mismatch is C085's marker expression to resolve, not this screen's to work
    /// around silently.
    static func forVehicleType(_ type: VehicleType) -> VehicleToken {
        switch type {
        case VehicleType.motorbike: return .motorbike
        case VehicleType.threeWheeler: return .threeWheeler
        case VehicleType.flex: return .flex
        case VehicleType.sedan: return .sedan
        case VehicleType.miniVan: return .miniVan
        case VehicleType.van: return .van
        case VehicleType.truck: return .truck
        case VehicleType.miniTruck: return .miniTruck
        case VehicleType.bus: return .bus
        case VehicleType.train: return .train
        // A type this build does not know is drawn in the grey `vehPrivate`, exactly as the map's
        // own fallback does: a missing dot reads as a platform outage, an unfamiliar grey one reads
        // as what it is.
        default: return .privateHire
        }
    }
}

extension ExtractedField {

    /// The trilingual row label for one `registry.document_fields` key.
    var labelKey: String {
        switch key {
        case VehicleFieldKeys.insuranceExpiry, VehicleFieldKeys.revenueExpiry: return "profile_setup_field_expiry"
        case VehicleFieldKeys.insurancePolicyNo: return "vehicle_field_policy_no"
        case VehicleFieldKeys.insurer: return "vehicle_field_insurer"
        case VehicleFieldKeys.revenueNo: return "vehicle_field_revenue_no"
        case VehicleFieldKeys.plateText: return "vehicle_field_plate_text"
        default: return "vehicle_field_plate_match"
        }
    }

    /// What the row shows.
    ///
    /// `reg_no_match` is a boolean on the wire and *"true"* is not copy — it is the answer to "did
    /// the plate in the photograph match the one you typed?", and that question has a trilingual yes
    /// and a trilingual no. Every other key carries a value read off the document and is shown as
    /// read.
    var displayValue: String? {
        guard key == VehicleFieldKeys.regNoMatch else { return value }
        guard let value else { return nil }
        return (value.lowercased() == "true" ? "vehicle_plate_matched" : "vehicle_plate_mismatch").localised
    }
}

/// `0.62` — two decimals, built by arithmetic so the decimal separator is the same in all three
/// locales and cannot follow the handset's region.
///
/// `leftPadded(to:)` moved to `UI/MoneyFormat.swift` with C088, which needs the same helper for a
/// clock field and a cent pair. Two copies in one target is an ambiguous call, not two functions.
func formattedConfidence(_ confidence: Double) -> String {
    let hundredths = Int((confidence * 100).rounded())
    return "\(hundredths / 100).\(String(hundredths % 100).leftPadded(to: 2))"
}
