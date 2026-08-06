import Foundation
import MageRideShared
import UIKit
import XCTest

@testable import DriverApp

/// SCR-DI-005 — the seam, the permission states and AL-43's provenance stamp.
///
/// **The quadrilateral itself is not under test, and cannot be**: it is
/// `VNDocumentCameraViewController`'s, which is the platform's own drag-corner crop and de-skew
/// (the wireframe's `Δ iOS` clause). What *is* this component's is everything around it — which slot
/// a capture is for, where the image goes, and which capture source it is stamped with.
@MainActor
final class DocumentScannerModelTests: XCTestCase {

    private var captures = DocumentCaptureCoordinator()
    private var camera = FakeCameraAuthoriser()

    override func setUp() {
        super.setUp()
        captures = DocumentCaptureCoordinator()
        camera = FakeCameraAuthoriser()
    }

    private func makeModel(for target: DocumentCaptureTarget? = .insurance) -> DocumentScannerModel {
        if let target { captures.open(target) }
        return DocumentScannerModel(captures: captures, camera: camera)
    }

    /// Opened with nothing pending — a restore after the requesting screen was rebuilt, or a
    /// navigation nobody declared a target for. There is nothing to photograph *for*.
    func testOpeningWithNoPendingTargetCloses() async {
        let model = makeModel(for: nil)
        await model.start()

        XCTAssertTrue(model.state.isDone)
        XCTAssertFalse(model.state.isScannerPresented)
    }

    func testAGrantedCameraOpensTheScanner() async {
        let model = makeModel()
        await model.start()

        XCTAssertTrue(model.state.isScannerPresented)
        XCTAssertEqual(camera.requestCount, 0, "already granted — no second sheet")
    }

    func testAnUnansweredCameraIsAskedForOnce() async {
        camera.authorisation = .notDetermined

        let model = makeModel()
        await model.start()

        XCTAssertEqual(camera.requestCount, 1)
        XCTAssertTrue(model.state.isScannerPresented)
    }

    /// D2' §SCR-DI-005's *"Permission-denied → 'Allow camera' prompt"*. Only Settings can change a
    /// refusal, so that is where the CTA goes.
    func testARefusedCameraOffersSettingsAndTheGallery() async {
        camera.authorisation = .notDetermined
        camera.grantsOnRequest = false

        let model = makeModel()
        await model.start()

        XCTAssertFalse(model.state.isScannerPresented)
        XCTAssertTrue(model.state.isBlockedInSettings)
        XCTAssertTrue(model.state.offersGallery)

        await model.allowCamera()
        XCTAssertEqual(camera.settingsOpenedCount, 1)
    }

    /// **Every simulator, and therefore every CI run**, has no document camera. Settings has nothing
    /// to offer that, so the gallery is the whole answer and the Allow CTA is not drawn.
    func testADeviceWithNoDocumentCameraFallsThroughToTheGallery() async {
        camera.isScannerSupported = false

        let model = makeModel()
        await model.start()

        XCTAssertFalse(model.state.canScan)
        XCTAssertTrue(model.state.offersGallery)
        XCTAssertFalse(model.state.isBlockedInSettings, "Settings cannot install a camera")
    }

    // MARK: - AL-43 · what is stamped on the image

    /// **`cameraDragCrop` is written here and nowhere else.** The capture route is the fraud signal
    /// the Verification-Officer queue sorts on, so the only thing allowed to claim a scan happened is
    /// the code that performed one.
    func testAScannedPageIsDeliveredAsACameraDragCrop() throws {
        let model = makeModel()
        model.onScanned([Self.image(width: 40, height: 60)])

        let result = try XCTUnwrap(captures.result)
        XCTAssertEqual(result.target, .insurance)
        XCTAssertEqual(result.image.capturedVia, CaptureSource.cameraDragCrop)
        XCTAssertEqual(result.image.fileName, "insurance.jpg")
        XCTAssertFalse(result.image.data.isEmpty)
        XCTAssertTrue(model.state.isDone)
    }

    /// The gallery is a **fallback, not a peer**: a file already on the handset is how a document
    /// belonging to somebody else arrives, and that is exactly what the officer queue wants to know.
    func testAPickedImageIsDeliveredAsAGalleryPick() throws {
        let model = makeModel(for: .vehicleFront)
        let data = try XCTUnwrap(Self.image(width: 40, height: 60).jpegData(compressionQuality: 1))

        model.onPicked(data)

        let result = try XCTUnwrap(captures.result)
        XCTAssertEqual(result.image.capturedVia, CaptureSource.gallery)
        XCTAssertEqual(result.image.fileName, "vehicle-front.jpg")
    }

    func testAScanThatProducedNoPageIsAFailureTheDriverCanRetry() async {
        let model = makeModel()
        await model.start()

        model.onScanned([])

        XCTAssertEqual(model.state.errorKey, "capture_failed")
        XCTAssertFalse(model.state.isDone)
        XCTAssertNil(captures.result)

        model.retry()
        XCTAssertNil(model.state.errorKey)
        XCTAssertTrue(model.state.isScannerPresented)
    }

    func testAnUndecodableFileIsAFailureRatherThanAnEmptyUpload() {
        let model = makeModel()
        model.onPicked(Data([0x00, 0x01, 0x02]))

        XCTAssertEqual(model.state.errorKey, "capture_failed")
        XCTAssertNil(captures.result)
    }

    /// The ✕. The slot stays empty and the requesting screen is untouched — and the pending request
    /// is dropped, so a later, unrelated visit cannot deliver into a slot nobody asked for.
    func testCancellingDropsThePendingRequest() {
        let model = makeModel()
        model.cancel()

        XCTAssertNil(captures.pending)
        XCTAssertNil(captures.result)
        XCTAssertTrue(model.state.isDone)
    }

    /// VisionKit cannot say *which* document it is scanning, which is the one thing this screen owns.
    func testTheTitleNamesTheDocumentBeingCaptured() {
        XCTAssertEqual(
            makeModel(for: .revenueLicence).state.titleText,
            "capture_title".localisedFormat("capture_target_revenue_licence".localised)
        )
        XCTAssertEqual(makeModel(for: nil).state.titleText, "capture_title_generic".localised)
    }

    // MARK: - Bounding the bytes

    /// The upload is bounded to 2400px on its longer edge. Above that the pixels buy Gemini Flash
    /// nothing and cost a driver on a 3G tether real seconds per step.
    func testALargeImageIsScaledDownBeforeItIsEncoded() {
        let scaled = DocumentImaging.downscaled(Self.image(width: 4000, height: 3000))

        XCTAssertEqual(scaled.size.width, DocumentImaging.maximumEdge, accuracy: 1)
        XCTAssertEqual(scaled.size.height, DocumentImaging.maximumEdge * 3 / 4, accuracy: 1)
    }

    func testASmallImageIsLeftAlone() {
        let original = Self.image(width: 40, height: 60)
        XCTAssertEqual(DocumentImaging.downscaled(original).size, original.size)
    }

    private static func image(width: CGFloat, height: CGFloat) -> UIImage {
        let format = UIGraphicsImageRendererFormat.default()
        format.scale = 1
        return UIGraphicsImageRenderer(size: CGSize(width: width, height: height), format: format).image { context in
            UIColor.gray.setFill()
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        }
    }
}
