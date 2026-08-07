import MageRideShared
import XCTest

@testable import DriverApp

/// The tables SCR-DI-016a/b/c renders from, and the two fences AL-33 draws that are not layout.
@MainActor
final class DeliveryLabelTests: XCTestCase {

    /// Every key the three sheets draw resolves. ``LocalizationTests`` proves the three files agree
    /// with each other; this proves the *code* names keys those files actually carry — a key that
    /// exists in none of them agrees with itself perfectly and renders as its own name.
    func testEveryKeyTheDeliverySheetsRenderHasAnEntry() {
        var keys = [
            "delivery_title",
            "delivery_leg_pickup",
            "delivery_leg_drop",
            "delivery_payment",
            "delivery_call",
            "delivery_cancel",
            "delivery_start",
            "delivery_navigate_pickup_distance",
            "delivery_pickup_at",
            "delivery_pickup_otp_label",
            "delivery_verify_pickup",
            "delivery_complete_title",
            "delivery_delivery_otp_label",
            "delivery_photo_proof",
            "delivery_photo_retake",
            "delivery_completed",
            "delivery_otp_wrong",
            "delivery_otp_attempts_left",
            "delivery_otp_locked",
            // Sheet 2 borrows three of SCR-DI-015's, because they are the same sentences. A second key
            // with the same words in three languages is what drifts apart.
            "ride_call_sender",
            "ride_sos",
            "ride_navigate_pickup",
            "ride_call_unavailable",
            DocumentCaptureTarget.deliveryProof.titleKey,
        ]
        keys += DeliveryParty.allCases.map(\.labelKey)

        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no entry in Localizable.strings")
        }
    }

    /// **AL-33** — the log records *which end of the delivery* was rung, because on these two sheets the
    /// ride's kind cannot say. `sender` and `recipient` are `comms.call_log`'s own values.
    func testEachPartyLogsItsOwnCalleeRole() {
        XCTAssertEqual(DeliveryParty.sender.calleeRole, CalleeRole.sender)
        XCTAssertEqual(DeliveryParty.recipient.calleeRole, CalleeRole.recipient)
        XCTAssertEqual(DeliveryParty.allCases.count, 2, "a delivery has two ends and only two")
    }

    /// The four digits are `:shared`'s number, not this screen's (P-07, D5' §14.1's six is a sign-in
    /// code). Counting them here as well would be a second answer to a question `PackageHandoff` has.
    func testTheOtpShapeIsTheDomainLayers() {
        XCTAssertEqual(PackageHandoff.companion.OTP_LENGTH, 4)
        XCTAssertEqual(PackageHandoff.companion.MAX_OTP_ATTEMPTS, 5)
        XCTAssertTrue(PackageHandoff.companion.isWellFormed(otp: "4821"))
        XCTAssertFalse(PackageHandoff.companion.isWellFormed(otp: "482"))
        XCTAssertFalse(PackageHandoff.companion.isWellFormed(otp: "48a1"))
    }

    /// The proof photo is not a document, and its target says so: it is the one capture slot whose file
    /// lands in `rides.proof_artifacts` rather than `docs.uploads`, and the file name is still its own so
    /// a support agent settling a P-14 dispute is not looking at eight rows called `IMG_0042`.
    func testTheProofSlotIsNamedLikeEveryOtherCapture() {
        XCTAssertEqual(DocumentCaptureTarget.deliveryProof.fileName, "delivery-proof.jpg")
        XCTAssertTrue(DocumentCaptureTarget.allCases.contains(.deliveryProof))
    }
}
