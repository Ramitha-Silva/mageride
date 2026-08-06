import Foundation
import MageRideShared
import UIKit

/// Turning what a scanner or a picker produced into the bytes that go up with the step.
///
/// The perspective correction itself is **VisionKit's** on this platform — `VNDocumentCameraScan`
/// hands back a page that has already been de-skewed against the quadrilateral the driver dragged —
/// so unlike `apps/driver-android/.../capture/DocumentImaging.kt` there is no warp to write here.
/// What is left is the part that is the same on both: bound the pixels, encode once, and refuse
/// anything the gateway would reject anyway.
enum DocumentImaging {

    /// The longest edge the upload keeps.
    ///
    /// The same 2400 the Android scanner samples down to. Above it the extra pixels buy Gemini Flash
    /// nothing — a licence fills the frame by construction — and cost a driver on a 3G tether real
    /// seconds per step.
    static let maximumEdge: CGFloat = 2400

    /// 0.92, not 1.0. The image is going to Gemini Flash after a redaction pre-pass (D-36), and the
    /// difference is invisible to OCR and about a third of the bytes.
    static let jpegQuality: CGFloat = 0.92

    /// Above this, an image is refused rather than uploaded — the gateway's own body ceiling.
    ///
    /// Refusing here rather than letting the request fail is what turns a `413` a driver cannot act
    /// on into *"take it again from a little further back"* (`error_image_too_large`).
    static let maximumBytes = 8 * 1024 * 1024

    /// JPEG bytes for [image], scaled down to ``maximumEdge`` first.
    ///
    /// `nil` when the encode fails or the result is over ``maximumBytes``; the caller shows
    /// `capture_failed`, because there is nothing in either case a driver could act on.
    static func jpegData(from image: UIImage) -> Data? {
        guard let data = downscaled(image).jpegData(compressionQuality: jpegQuality) else { return nil }
        return data.count <= maximumBytes ? data : nil
    }

    /// [image] with its longer edge at most ``maximumEdge``, or the image itself when it already is.
    ///
    /// `UIGraphicsImageRenderer` with an explicit `scale` of 1: the default is the screen's, which on
    /// a 3× handset would redraw a 2400pt image at 7200 pixels — three times the bytes this bound
    /// exists to avoid.
    static func downscaled(_ image: UIImage) -> UIImage {
        let size = image.size
        let longest = max(size.width, size.height)
        guard longest > maximumEdge, longest > 0 else { return image }

        let ratio = maximumEdge / longest
        let target = CGSize(width: (size.width * ratio).rounded(), height: (size.height * ratio).rounded())

        let format = UIGraphicsImageRendererFormat.default()
        format.scale = 1
        format.opaque = true

        return UIGraphicsImageRenderer(size: target, format: format).image { _ in
            image.draw(in: CGRect(origin: .zero, size: target))
        }
    }
}

extension DocumentCaptureTarget {

    /// The name this slot's image is uploaded under.
    ///
    /// `docs.uploads` keeps the original file name, and "insurance.jpg" in the Verification
    /// Officer's document viewer is the difference between a queue they can work and eight identical
    /// rows called `IMG_0042`. The same six names the Android scanner sends.
    var fileName: String {
        switch self {
        case .licenceFront: return "licence-front.jpg"
        case .licenceBack: return "licence-back.jpg"
        case .insurance: return "insurance.jpg"
        case .revenueLicence: return "revenue-licence.jpg"
        case .vehicleFront: return "vehicle-front.jpg"
        case .vehicleBack: return "vehicle-back.jpg"
        }
    }

    /// The trilingual name of what is being captured — the wireframe's `Capture: Licence front`.
    ///
    /// C086 left this mapping to C087 deliberately: it is the scanner that renders it, and the
    /// scanner is this component's. The six keys are `apps/driver-android`'s, with its translations.
    var titleKey: String {
        switch self {
        case .licenceFront: return "capture_target_licence_front"
        case .licenceBack: return "capture_target_licence_back"
        case .insurance: return "capture_target_insurance"
        case .revenueLicence: return "capture_target_revenue_licence"
        case .vehicleFront: return "capture_target_vehicle_front"
        case .vehicleBack: return "capture_target_vehicle_back"
        }
    }
}
