import AVFoundation
import UIKit

/// What the system currently says about the camera.
///
/// Three values rather than `AVAuthorizationStatus`' four, because the screen only has three
/// answers: ask, use it, or send the driver to Settings. `.restricted` — a managed device with the
/// camera switched off by policy — collapses into ``blocked`` for the same reason `.denied` does:
/// neither can be changed from inside the app.
enum CameraAuthorisation {

    /// Never asked. A sheet will appear.
    case notDetermined

    /// Held. SCR-DI-005 can open the scanner.
    case granted

    /// Refused, restricted by policy, or the device has no usable document camera. Only Settings —
    /// or the gallery fallback — gets the driver past this.
    case blocked
}

/// Reads and asks for the camera grant.
///
/// A protocol for the reason ``DriverPermissions`` is one: every member is a system API whose answer
/// in a unit test is whatever the test host happens to have been granted, and a model that believed
/// those answers would report a permission state nobody set.
///
/// `@MainActor` because the implementation is — `UIApplication.open` and `VNDocumentCameraViewController`
/// are both main-thread APIs.
@MainActor
protocol CameraAuthoriser: AnyObject {

    /// What the system says right now. Re-read on appear and on every return to the foreground; a
    /// Settings trip reports back no other way.
    var authorisation: CameraAuthorisation { get }

    /// Whether this device can present the document scanner at all.
    ///
    /// `false` on the **simulator**, where `VNDocumentCameraViewController.isSupported` is `false` —
    /// which is also every CI run, so the screen has to have an answer for it rather than a black
    /// rectangle. The gallery fallback is that answer.
    var isScannerSupported: Bool { get }

    /// Shows the system sheet, once. Answers whether the grant is held afterwards.
    func request() async -> Bool

    /// Opens this app's own page in Settings.
    func openSettings()
}

/// ``CameraAuthoriser`` over `AVCaptureDevice` and VisionKit.
@MainActor
final class SystemCameraAuthoriser: CameraAuthoriser {

    var authorisation: CameraAuthorisation {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined: return .notDetermined
        case .authorized: return .granted
        default: return .blocked
        }
    }

    var isScannerSupported: Bool { DocumentCameraView.isSupported }

    func request() async -> Bool {
        await AVCaptureDevice.requestAccess(for: .video)
    }

    func openSettings() {
        guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
        UIApplication.shared.open(url)
    }
}
