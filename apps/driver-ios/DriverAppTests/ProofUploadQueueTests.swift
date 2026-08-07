import MageRideShared
import XCTest

@testable import DriverApp

/// P-10's proof queue — `mobile_db_schema.md` §3.6's verbs, in memory (see ``ProofUploadQueue``).
///
/// What is being asserted is the behaviour a durable table would have to keep if it replaced this one:
/// one photograph per delivery, a failed upload keeps its file, and an accepted one is dropped. Same
/// three cases as `ProofUploadQueueTest.kt`.
@MainActor
final class ProofUploadQueueTests: XCTestCase {

    private var queue: ProofUploadQueue!

    override func setUp() {
        super.setUp()
        queue = ProofUploadQueue()
    }

    func testARetakeReplacesThePhotographRatherThanAddingASecond() {
        queue.enqueue(rideId: testRideId, image: testProofImage("first.jpg"), at: nil, capturedAt: Date())
        queue.enqueue(rideId: testRideId, image: testProofImage("second.jpg"), at: nil, capturedAt: Date())

        // The second picture is the driver saying the first one was wrong; keeping both would put a
        // rejected shot in front of a §11.14 dispute.
        XCTAssertEqual(queue.pending(for: testRideId)?.image.fileName, "second.jpg")
    }

    func testAClaimCountsTheAttemptAndARescheduleKeepsTheFile() {
        let entry = queue.enqueue(
            rideId: testRideId,
            image: testProofImage(),
            at: testHere,
            capturedAt: Date()
        )

        XCTAssertEqual(queue.claim(entry.id)?.attempts, 1)
        queue.reschedule(entry.id)

        guard let waiting = queue.pending(for: testRideId) else { return XCTFail("the photograph was lost") }
        XCTAssertEqual(waiting.state, .pending)
        XCTAssertEqual(waiting.attempts, 1, "the count survives, so a second failure is visible")
        XCTAssertNotNil(waiting.at, "captured_geo is part of the proof (D5' §11)")
    }

    func testAnUploadedPhotographIsDroppedAndAReleasedDeliveryForgetsItsOwn() {
        let uploaded = queue.enqueue(rideId: testRideId, image: testProofImage("a.jpg"), at: nil, capturedAt: Date())
        queue.markUploaded(uploaded.id)
        XCTAssertNil(queue.pending(for: testRideId), "§4.3 keeps no uploaded row")

        queue.enqueue(rideId: testRideId, image: testProofImage("b.jpg"), at: nil, capturedAt: Date())
        queue.discard(rideId: testRideId)
        XCTAssertNil(queue.pending(for: testRideId))
    }

    /// A refusal a retry will not fix keeps the row so the driver can see it, unlike an accepted one.
    func testAFailedUploadIsKeptRatherThanDropped() {
        let entry = queue.enqueue(rideId: testRideId, image: testProofImage(), at: nil, capturedAt: Date())

        queue.markFailed(entry.id)

        XCTAssertEqual(queue.pending(for: testRideId)?.state, .failed)
    }
}
