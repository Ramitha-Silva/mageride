import Foundation
import MageRideShared
import UIKit

/// SCR-DI-005's state.
///
/// - Parameters:
///   - target: What this visit to the scanner is for; `nil` when it was opened with no pending
///     request, which is a state that should close rather than photograph anything.
///   - camera: What the system says about the camera grant.
///   - isScannerSupported: Whether `VNDocumentCameraViewController` exists on this device at all.
///   - isScannerPresented: Whether VisionKit's own full-screen scanner is up.
///   - isBusy: An encode is running, or a picked image is being read.
///   - errorKey: Resolved copy for the last failure.
///   - isDone: The image has been delivered — or the driver backed out — and the screen closes.
struct DocumentScannerState {

    var target: DocumentCaptureTarget?
    var camera: CameraAuthorisation = .notDetermined
    var isScannerSupported = true
    var isScannerPresented = false
    var isBusy = false
    var errorKey: String?
    var isDone = false

    /// Whether the scanner could be opened right now.
    var canScan: Bool { camera == .granted && isScannerSupported }

    /// Whether the driver has to leave the app to change the answer — *"denied → Settings"*.
    ///
    /// A device with no document camera is deliberately **not** this: Settings has nothing to offer
    /// it, so it falls through to the gallery like any other unusable camera.
    var isBlockedInSettings: Bool { camera == .blocked && isScannerSupported }

    /// Whether the gallery fallback is offered — whenever the scanner cannot run.
    var offersGallery: Bool { !canScan }

    /// The navigation bar's title, resolved. Generic only when nothing named a target.
    var titleText: String {
        guard let target else { return "capture_title_generic".localised }
        return "capture_title".localisedFormat(target.titleKey.localised)
    }
}

/// **SCR-DI-005 · document capture** — the one document scanner every onboarding image goes through
/// (AL-43, BR-28.4, US-2.4b).
///
/// Shared by C086's two licence slots and C087's four vehicle-document slots, which is why nothing
/// here knows what a licence or an insurance card *is*: ``DocumentCaptureCoordinator`` holds the
/// pending ``DocumentCaptureTarget``, this screen photographs it, and the screen that asked collects
/// the result. The route carries no arguments, so the coordinator is the only way to say what a
/// capture is for.
///
/// **Δ Section C — the quadrilateral is VisionKit's, and that is what the wireframe asks for.**
/// `driver_ios.html`'s own iOS clause on this cell reads *"VisionKit `VNDocumentCameraViewController`
/// (native drag-corner crop); perspective transform applied on confirm"*, and D2' §SCR-DI-005's
/// sequence — live camera → an auto-proposed quad → drag the four corners → Retake / Use photo → a
/// de-skewed image — is exactly that controller's own. So `apps/driver-android`'s `CropQuad`,
/// `DocumentEdgeDetector` and the warp in `DocumentImaging` have **no counterpart here**: their
/// behaviour is the platform's, including the flash toggle and the Retake/Keep-Scan bar, and each is
/// rendered by iOS in the driver's own language rather than out of `Localizable.strings`.
///
/// What is not free is the AL-43 provenance stamp. ``CaptureSource/cameraDragCrop`` is written in
/// ``onScanned(_:)`` and nowhere else, and a picked file is `.gallery`, because the capture route is
/// the fraud signal the Verification-Officer queue sorts on — the only thing allowed to claim a scan
/// happened is the code that performed one.
@MainActor
final class DocumentScannerModel: ObservableObject {

    @Published private(set) var state = DocumentScannerState()

    private let captures: DocumentCaptureCoordinator
    private let camera: CameraAuthoriser

    init(captures: DocumentCaptureCoordinator, camera: CameraAuthoriser) {
        self.captures = captures
        self.camera = camera
        state.target = captures.pending
    }

    /// Resolves the camera grant and opens the scanner when it can.
    ///
    /// Called on appear and again on every return to the foreground: a driver who went to Settings
    /// to allow the camera comes back to a screen that has to notice.
    func start() async {
        // Opened with nothing pending — a restore after the requesting screen was rebuilt, or a
        // navigation nobody declared a target for. There is nothing to photograph *for*, so leave.
        guard state.target != nil else {
            state.isDone = true
            return
        }

        state.isScannerSupported = camera.isScannerSupported
        state.camera = camera.authorisation

        if state.camera == .notDetermined {
            _ = await camera.request()
            state.camera = camera.authorisation
        }

        if state.canScan, !state.isScannerPresented, state.errorKey == nil {
            state.isScannerPresented = true
        }
    }

    /// The `Allow camera` CTA. Asks, or sends the driver to Settings when only Settings can answer.
    func allowCamera() async {
        guard !state.isBlockedInSettings else {
            camera.openSettings()
            return
        }
        state.errorKey = nil
        await start()
    }

    /// `Retry` after a failed encode — back into the scanner.
    func retry() {
        state.errorKey = nil
        guard state.canScan else { return }
        state.isScannerPresented = true
    }

    /// VisionKit handed back the de-skewed pages.
    ///
    /// The **first** page only. A driver who scanned three pages of an insurance booklet has given
    /// the step one document slot's worth of answer, and picking the first is the same choice the
    /// Android shutter makes by taking one frame — Step 4/4's two photographs are two separate
    /// visits to this screen, not two pages of one scan.
    func onScanned(_ pages: [UIImage]) {
        state.isScannerPresented = false

        guard let target = state.target, let page = pages.first else {
            state.errorKey = "capture_failed"
            return
        }

        state.isBusy = true
        guard let data = DocumentImaging.jpegData(from: page) else {
            state.isBusy = false
            // Over the ceiling or unencodable. `error_image_too_large` is deliberately not used: at
            // this point the image has already been bounded to 2400px, so what failed was the encode
            // and taking it again is the honest advice.
            state.errorKey = "capture_failed"
            return
        }

        captures.deliver(
            CapturedImage(
                fileName: target.fileName,
                data: data,
                capturedVia: CaptureSource.cameraDragCrop
            )
        )
        state.isBusy = false
        state.isDone = true
    }

    /// VisionKit could not scan — a hardware failure, not a driver error.
    func onScanFailed() {
        state.isScannerPresented = false
        state.isBusy = false
        state.errorKey = "capture_failed"
    }

    /// The driver dismissed VisionKit's own Cancel. The slot stays empty and the requesting screen
    /// is untouched — the same thing the Android scanner's ✕ does.
    func onScanCancelled() {
        state.isScannerPresented = false
        cancel()
    }

    /// The gallery fallback, for a handset whose camera is denied, restricted or absent.
    ///
    /// It is a fallback and not a peer: what it delivers is stamped ``CaptureSource/gallery``,
    /// because a file already on the handset is how a document belonging to somebody else arrives
    /// (AL-43) — and that is exactly what the officer queue wants to know. Offering it anyway is what
    /// keeps a handset with a broken camera onboardable at all.
    func onPicked(_ data: Data) {
        guard let target = state.target else { return }

        state.isBusy = true
        guard let image = UIImage(data: data), let encoded = DocumentImaging.jpegData(from: image) else {
            state.isBusy = false
            state.errorKey = data.count > DocumentImaging.maximumBytes ? "error_image_too_large" : "capture_failed"
            return
        }

        captures.deliver(
            CapturedImage(
                fileName: target.fileName,
                data: encoded,
                capturedVia: CaptureSource.gallery
            )
        )
        state.isBusy = false
        state.isDone = true
    }

    /// The ✕ in the bar. The pending request is dropped so a later, unrelated visit to this screen
    /// does not deliver into a slot nobody asked for.
    func cancel() {
        captures.cancel()
        state.isDone = true
    }
}
