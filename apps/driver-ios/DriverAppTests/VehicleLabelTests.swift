import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// The copy tables, the step table and the two rules that are properties of a vehicle.
///
/// Small pure functions, and the reason they are worth a suite of their own: each one maps a machine
/// key onto a trilingual label, and the failure mode is a driver reading `vehicle_field_insurer` on
/// their own screen. `LocalizationTests` checks the three files against each other; this checks the
/// tables against the files.
@MainActor
final class VehicleLabelTests: XCTestCase {

    // MARK: - The step table

    /// ``VehicleOnboardingSteps/all`` is written out in Swift and indexed by the shared enum's own
    /// `ordinal`. The two have to agree, or a step inserted in `:shared` would mis-number a progress
    /// bar rather than fail anything.
    func testTheStepTableMatchesTheSharedEnumsOrdinals() {
        for (index, step) in VehicleOnboardingSteps.all.enumerated() {
            XCTAssertEqual(Int(step.ordinal), index, "\(step.wire) is at the wrong place in the table")
        }
        XCTAssertEqual(VehicleOnboardingSteps.count, 4)
    }

    func testStepNumbersAreOneBasedAndTheWalkIsBounded() {
        XCTAssertEqual(VehicleOnboardingSteps.number(of: OnboardingStep.details), 1)
        XCTAssertEqual(VehicleOnboardingSteps.number(of: OnboardingStep.photos), 4)

        XCTAssertNil(VehicleOnboardingSteps.previous(before: OnboardingStep.details), "Step 1/4's back exits")
        XCTAssertNil(VehicleOnboardingSteps.next(after: OnboardingStep.photos), "Step 4/4 hands over")
        XCTAssertEqual(VehicleOnboardingSteps.next(after: OnboardingStep.details), OnboardingStep.insurance)
        XCTAssertEqual(VehicleOnboardingSteps.previous(before: OnboardingStep.revenue), OnboardingStep.insurance)
    }

    /// **AL-30.** The resume point is the first step that is not verified, and `nil` once all four
    /// are — which is what makes a finished vehicle start a *new* one.
    func testTheFirstUnverifiedStepIsTheResumePoint() {
        XCTAssertEqual(
            verdicts(details: StepVerdict.verified, insurance: StepVerdict.pendingReview).firstUnverified,
            OnboardingStep.insurance
        )
        XCTAssertNil(
            verdicts(
                details: StepVerdict.verified,
                insurance: StepVerdict.verified,
                revenue: StepVerdict.verified,
                photos: StepVerdict.verified
            ).firstUnverified
        )
    }

    /// The read DTO keys the four verdicts by name; this is the one place they are mapped.
    func testEachStepReadsItsOwnVerdict() {
        let steps = verdicts(
            details: StepVerdict.verified,
            insurance: StepVerdict.pendingReview,
            revenue: StepVerdict.pendingInput,
            photos: StepVerdict.verified
        )

        XCTAssertEqual(steps.verdict(for: OnboardingStep.details), StepVerdict.verified)
        XCTAssertEqual(steps.verdict(for: OnboardingStep.insurance), StepVerdict.pendingReview)
        XCTAssertEqual(steps.verdict(for: OnboardingStep.revenue), StepVerdict.pendingInput)
        XCTAssertEqual(steps.verdict(for: OnboardingStep.photos), StepVerdict.verified)
        XCTAssertEqual(steps.rows.count, 4)
    }

    // MARK: - US-9.6 · the go-live rule

    func testAnOwnedModeCVehicleNeedsBothApprovals() {
        XCTAssertTrue(
            summary(status: RegistrationStatus.approved, onboardingStatus: OnboardingStatus.approved).canGoLive
        )
        XCTAssertFalse(
            summary(status: RegistrationStatus.approved, onboardingStatus: OnboardingStatus.incomplete).canGoLive
        )
        XCTAssertFalse(
            summary(status: RegistrationStatus.pending, onboardingStatus: OnboardingStatus.approved).canGoLive
        )
    }

    func testAnAssignedVehicleIsEligibleOnItsRegistrationAlone() {
        let assigned = summary(
            mode: ServiceMode.a,
            status: RegistrationStatus.approved,
            onboardingStatus: OnboardingStatus.incomplete
        )
        XCTAssertFalse(assigned.isOnboardable, "AL-27 — the driver app onboards Mode C only")
        XCTAssertTrue(assigned.canGoLive)
    }

    // MARK: - The extract card's keys

    /// The `details` step is "(entered)" and has no extracted field; the other three list their own
    /// keys in the order the wireframe draws them.
    func testEachStepListsItsOwnFieldKeys() {
        XCTAssertTrue(VehicleFieldKeys.forStep(OnboardingStep.details).isEmpty)
        XCTAssertEqual(
            VehicleFieldKeys.forStep(OnboardingStep.insurance),
            [VehicleFieldKeys.insuranceExpiry, VehicleFieldKeys.insurancePolicyNo, VehicleFieldKeys.insurer]
        )
        XCTAssertEqual(
            VehicleFieldKeys.forStep(OnboardingStep.revenue),
            [VehicleFieldKeys.revenueNo, VehicleFieldKeys.revenueExpiry]
        )
        XCTAssertEqual(
            VehicleFieldKeys.forStep(OnboardingStep.photos),
            [VehicleFieldKeys.plateText, VehicleFieldKeys.regNoMatch]
        )
    }

    /// **`reg_no_match` is a boolean on the wire, and *"true"* is not copy.** It is the answer to
    /// "did the plate in the photograph match the one you typed?", and that has a trilingual yes and
    /// a trilingual no.
    func testThePlateCheckRendersAsAnAnswerRatherThanABoolean() {
        XCTAssertEqual(
            field(key: VehicleFieldKeys.regNoMatch, value: "true").displayValue,
            "vehicle_plate_matched".localised
        )
        XCTAssertEqual(
            field(key: VehicleFieldKeys.regNoMatch, value: "false").displayValue,
            "vehicle_plate_mismatch".localised
        )
        XCTAssertNil(field(key: VehicleFieldKeys.regNoMatch, value: nil).displayValue)
        XCTAssertEqual(field(key: VehicleFieldKeys.plateText, value: "ABC-1234").displayValue, "ABC-1234")
    }

    /// A field extraction never returned is unread, and unread needs an officer (AL-29, BR-25.2).
    func testAnUnreadFieldNeedsAnOfficer() {
        XCTAssertTrue(field(key: VehicleFieldKeys.insurer, value: nil).needsOfficerReview)
        XCTAssertTrue(
            field(key: VehicleFieldKeys.insurer, value: "Ceylinco", verifyStatus: VerifyStatus.pending)
                .needsOfficerReview
        )
        XCTAssertFalse(field(key: VehicleFieldKeys.insurer, value: "Ceylinco").needsOfficerReview)
    }

    /// `0.62`, built by arithmetic. Two decimals, always, and the separator cannot follow the
    /// handset's region — the same number has to read the same in all three locales.
    func testConfidenceIsFormattedIdenticallyInEveryLocale() {
        XCTAssertEqual(formattedConfidence(0.62), "0.62")
        XCTAssertEqual(formattedConfidence(0.9), "0.90")
        XCTAssertEqual(formattedConfidence(1), "1.00")
        XCTAssertEqual(formattedConfidence(0.005), "0.01")
    }

    // MARK: - Every key the tables can produce

    /// The tables can only produce keys that exist in all three files.
    ///
    /// `String.localised` answers the key itself when there is no entry, which is precisely the bug
    /// this looks for — a mistyped key renders as `vehicle_field_insurer` on a driver's screen.
    func testEveryKeyTheTablesCanProduceIsLocalised() {
        var keys: [String] = []

        for step in VehicleOnboardingSteps.all {
            keys += [step.titleKey, step.captionKey, step.ctaKey, step.documentKey]
        }
        for verdict in [StepVerdict.verified, StepVerdict.pendingReview, StepVerdict.pendingInput] {
            keys.append(verdict.labelKey)
        }
        for status in [
            RegistrationStatus.pending,
            RegistrationStatus.approved,
            RegistrationStatus.rejected,
            RegistrationStatus.deactivated,
        ] {
            keys.append(status.labelKey)
        }
        for target in DocumentCaptureTarget.allCases {
            keys.append(target.titleKey)
        }
        for key in VehicleOnboardingSteps.all.flatMap(VehicleFieldKeys.forStep) {
            keys.append(field(key: key, value: nil).labelKey)
        }
        keys += Self.everyVehicleType.map(\.labelKey)

        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no entry in Localizable.strings")
        }
    }

    /// AL-09's ten canonical types all name a colour and a key. A fleet vehicle assigned to a driver
    /// (US-13.9) can be a bus or a train, which the eight ride-bookable types do not cover.
    func testEveryCanonicalVehicleTypeHasALabelAndAColour() {
        var tokens: Set<VehicleToken> = []
        for type in Self.everyVehicleType {
            XCTAssertEqual(type.labelKey, "vehicle_type_" + type.wire)
            tokens.insert(VehicleToken.forVehicleType(type))
        }
        XCTAssertEqual(tokens.count, Self.everyVehicleType.count, "two types share a legend colour")
    }

    /// The six slots the scanner fills, each with the name the Verification Officer's document viewer
    /// shows. `docs.uploads` keeps the original file name.
    func testEveryCaptureTargetHasItsOwnFileName() {
        let names = DocumentCaptureTarget.allCases.map(\.fileName)
        XCTAssertEqual(Set(names).count, names.count)
        XCTAssertEqual(names.filter { !$0.hasSuffix(".jpg") }, [])
        XCTAssertEqual(DocumentCaptureTarget.insurance.fileName, "insurance.jpg")
    }

    private static let everyVehicleType: [VehicleType] = [
        VehicleType.motorbike,
        VehicleType.threeWheeler,
        VehicleType.flex,
        VehicleType.sedan,
        VehicleType.miniVan,
        VehicleType.van,
        VehicleType.truck,
        VehicleType.miniTruck,
        VehicleType.bus,
        VehicleType.train,
    ]
}
