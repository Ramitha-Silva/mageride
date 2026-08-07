import AVFoundation
import UIKit

/// What the system currently says about the camera.
///
/// Three values rather than `AVAuthorizationStatus`' four, because SCR-PI-017 has three answers: ask,
/// scan, or send the passenger to Settings. `.restricted` — a managed device with the camera switched
/// off by policy — collapses into ``blocked`` for the same reason `.denied` does: neither can be
/// changed from inside the app, and both leave *"Pay with my bank app"* as the way through.
enum CameraAccess {

    /// Never asked. The system sheet will appear.
    case notDetermined

    /// Held. The scanner can open.
    case granted

    /// Refused or restricted by policy. Only Settings — or the bank-app link — gets past this.
    case blocked
}

/// Reads and asks for the camera grant, and says whether this handset can scan at all.
///
/// A protocol for the reason ``LocationPermission`` is one: every member is a system API whose answer
/// in a unit test is whatever the **test host** happens to have been granted, and a model that
/// believed those answers would report a permission state nobody set.
///
/// **The grant is asked for *before* the scanner is presented**, not by the scanner.
/// `DataScannerViewController.isAvailable` is `false` without it, so presenting first would show a
/// passenger a viewfinder that cannot see — the same ordering `apps/driver-ios` arrived at in C092.
///
/// `@MainActor` because `UIApplication.open` and VisionKit are.
@MainActor
protocol CameraAuthoriser: AnyObject {

    /// What the system says right now. Re-read on appear and on every return to the foreground; a
    /// Settings trip reports back no other way.
    var access: CameraAccess { get }

    /// Whether this device can run VisionKit's code scanner.
    ///
    /// **`false` on every simulator** — `DataScannerViewController.isSupported` needs an A12 or later
    /// — and `false` without the camera grant, which the controller folds into its own `isAvailable`.
    /// SCR-PI-017 has to have an answer for that rather than a black rectangle, and AL-15's bank-app
    /// link is it.
    var isScannerSupported: Bool { get }

    /// Shows the system sheet, once. Answers whether the grant is held afterwards.
    func request() async -> Bool

    /// Opens this app's own page in Settings.
    func openSettings()
}

/// ``CameraAuthoriser`` over `AVCaptureDevice` and VisionKit.
@MainActor
final class SystemCameraAuthoriser: CameraAuthoriser {

    var access: CameraAccess {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .notDetermined: return .notDetermined
        case .authorized: return .granted
        default: return .blocked
        }
    }

    var isScannerSupported: Bool { DriverQrScannerView.isSupported }

    func request() async -> Bool {
        await AVCaptureDevice.requestAccess(for: .video)
    }

    func openSettings() {
        guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
        UIApplication.shared.open(url)
    }
}
