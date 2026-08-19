import Combine
import Foundation
import MageRideShared

/// One captured or picked image, in memory.
///
/// Bytes rather than a `PHPickerResult` or a file URL: what leaves this app is a
/// `multipart/form-data` part (`FileUpload` takes a byte array), and a picker's item provider is
/// valid only for as long as the picker session — which ends well before a driver has finished
/// filling in Profile Setup.
///
/// - Parameters:
///   - fileName: Name to send in the part's `Content-Disposition`.
///   - data: The image, already perspective-corrected when it came from SCR-DI-005.
///   - mimeType: Media type, e.g. `image/jpeg`.
///   - capturedVia: How this image was obtained (AL-43). Carried **on the image** rather than
///     passed at the call site, because the two travel together all the way to
///     `docs.uploads.captured_via` and an image that lost its provenance would be recorded as a
///     scan that never happened — which is the one thing the Verification-Officer queue sorts on.
struct CapturedImage: Equatable {

    let fileName: String
    let data: Data
    let mimeType: String
    let capturedVia: CaptureSource

    init(
        fileName: String,
        data: Data,
        mimeType: String = "image/jpeg",
        capturedVia: CaptureSource = CaptureSource.cameraDragCrop
    ) {
        self.fileName = fileName
        self.data = data
        self.mimeType = mimeType
        self.capturedVia = capturedVia
    }

    /// How many bytes this is, for the size guard on the upload.
    var sizeBytes: Int { data.count }

    /// This image as the shared client's multipart part.
    ///
    /// The conversion is Kotlin's (`IosCapturedDocument.kt`) and deliberately so: `FileUpload` takes
    /// a `ByteArray`, whose only Swift-facing mutator is one Objective-C message per byte, and a
    /// three-megabyte photograph is three million of them. Kotlin does it with one `memcpy`.
    ///
    /// The `data:` parameter is `NSData *` in the generated header, which Swift's importer bridges
    /// to `Data` — so the value goes over as it is, and an `as NSData` cast is what fails to build.
    func asDocument() -> CapturedDocument {
        IosCapturedDocumentKt.capturedDocument(
            fileName: fileName,
            data: data,
            contentType: mimeType,
            capturedVia: capturedVia
        )
    }
}

/// Which slot a capture is filling.
///
/// The whole set lives here rather than in the screen that opens the camera, because SCR-DI-005 is
/// **one** screen shared by C086's two licence slots and C087's four vehicle-document slots
/// (AL-43). The scanner names the document in its own title bar, and that title is a property of the
/// target rather than of whoever navigated — the mapping itself is the scanner's and arrives with
/// C087, exactly as `labelRes()` does on the Android side.
///
/// The set is `driver_ios.html`'s, and it matches `driver_android.html`'s target for target since C089
/// added ``deliveryProof``.
enum DocumentCaptureTarget: String, CaseIterable {

    /// SCR-DI-003a — front of the driving licence.
    case licenceFront

    /// SCR-DI-003a — back of the driving licence.
    case licenceBack

    /// SCR-DI-004a — insurance card or paper (C087).
    case insurance

    /// SCR-DI-004b — vehicle revenue licence (C087).
    case revenueLicence

    /// SCR-DI-004c — vehicle front, number plate visible (C087).
    case vehicleFront

    /// SCR-DI-004c — vehicle back, number plate visible (C087).
    case vehicleBack

    /// SCR-DI-016c — proof that the parcel was left at the door (P-10, C089).
    ///
    /// **The first non-document use of SCR-DI-005**, and deliberately so: a proof photo goes to
    /// `rides.proof_artifacts` rather than to `docs.uploads`, carries no `captured_via`, and never
    /// reaches the Verification-Officer queue. What it wants from the scanner is the one thing the
    /// scanner is for — a framed, de-skewed photograph the driver has confirmed — so AL-43's provenance
    /// stamp is simply dropped at the upload instead of being filed against a queue it does not belong
    /// to. The same case, for the same reason, as `apps/driver-android`'s `DELIVERY_PROOF`.
    case deliveryProof
}

/// A finished capture, on its way back to the screen that asked for one.
struct DocumentCaptureResult: Equatable {
    let target: DocumentCaptureTarget
    let image: CapturedImage
}

/// The seam between a screen with a capture slot and **SCR-DI-005**, the shared camera
/// document-scanner (AL-43, owned by C087).
///
/// ``DriverRoute/documentCapture`` carries no arguments — the shell fixed the route table before any
/// screen group existed — so "capture the licence front, and give the result back to Profile Setup"
/// cannot be expressed as a navigation argument. It is expressed here instead: the caller ``open``s
/// a target and navigates to the route; the scanner reads ``pending``, shows the right title, and
/// ``deliver``s the cropped image; the requesting screen observes ``result`` and ``consume``s it.
///
/// One instance for the process, held by ``DriverGraph``. Two screens cannot be capturing at once —
/// the scanner is a full-screen takeover — so one slot is the whole state this needs.
///
/// `@MainActor` and `@Published` rather than a `Flow`: the replay Android needs (a screen recreated
/// while the camera had the foreground) is what a stored property already is on this platform, and
/// SwiftUI does not tear a view down to present a `fullScreenCover` over it. ``consume()`` is still
/// what clears the slot, so a captured image cannot be applied twice.
@MainActor
final class DocumentCaptureCoordinator: ObservableObject {

    /// What SCR-DI-005 has been asked to capture, or `nil` when it was opened with no request.
    @Published private(set) var pending: DocumentCaptureTarget?

    /// The last finished capture, until the screen that asked for it consumes it.
    @Published private(set) var result: DocumentCaptureResult?

    /// Declares what the next visit to SCR-DI-005 is for. Call immediately before navigating.
    func open(_ target: DocumentCaptureTarget) {
        pending = target
    }

    /// SCR-DI-005 hands back the cropped, de-skewed image (C087).
    func deliver(_ image: CapturedImage) {
        guard let target = pending else { return }
        pending = nil
        result = DocumentCaptureResult(target: target, image: image)
    }

    /// The requesting screen has applied the result. Drops it so it is not applied twice.
    func consume() {
        result = nil
    }

    /// The driver backed out of the scanner without taking a photo.
    func cancel() {
        pending = nil
    }
}
