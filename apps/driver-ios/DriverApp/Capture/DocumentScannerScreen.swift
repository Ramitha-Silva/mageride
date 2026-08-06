import PhotosUI
import SwiftUI
import VisionKit

/// **SCR-DI-005 · document capture (camera + drag-crop)** — the shared scanner (AL-43).
///
/// The wireframe draws a dark screen with a `✕ Cancel` / *"Capture: Licence front"* / `⚡` bar, a
/// viewfinder carrying an adjustable crop quadrilateral with four corner handles and a rule-of-thirds
/// grid, the hint *"Drag the corners so the whole document fills the frame"*, and a
/// `Retake · ◉ · Use photo ›` bar under it.
///
/// **On this platform the viewfinder, the quad, the flash and that bottom bar are all
/// `VNDocumentCameraViewController`'s**, which is what the cell's own `Δ iOS` clause asks for. What
/// this view owns is everything around it: the one thing VisionKit cannot say — *which* document is
/// being captured — the permission-denied state D2' §SCR-DI-005 requires, the gallery fallback, and
/// the failure copy. So it is drawn as the wireframe's dark screen with the platform's scanner
/// presented over it, rather than as a second scanner beside Apple's.
///
/// See ``DocumentScannerModel`` for the Section-C delta in full, and for why the provenance stamp is
/// the one part of AL-43 that is *not* free here.
///
/// - Parameter onFinished: Closes the takeover and returns to the screen that asked for the capture.
///   The result reaches it through ``DocumentCaptureCoordinator``, not through a navigation
///   argument — the route has none.
///
/// `@MainActor` on the whole view, not on its initialiser: every member reads a `@MainActor` model,
/// and annotating the type once is what keeps a helper added later from being the one non-isolated
/// member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct DocumentScannerScreen: View {

    @StateObject private var model: DocumentScannerModel
    @State private var pickedItem: PhotosPickerItem?

    private let onFinished: () -> Void

    init(
        captures: DocumentCaptureCoordinator,
        camera: CameraAuthoriser,
        onFinished: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: DocumentScannerModel(captures: captures, camera: camera))
        self.onFinished = onFinished
    }

    var body: some View {
        VStack(spacing: 0) {
            bar

            Spacer(minLength: 0)
            content(for: model.state)
            Spacer(minLength: 0)

            if let errorKey = model.state.errorKey {
                failure(errorKey)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // The fill ignores the safe area and the content does not: `driver_ios.html` draws this cell
        // dark to the edges of the phone, with its bar still clear of the notch.
        .background(MageRideScannerColor.background.ignoresSafeArea())
        .task { await model.start() }
        .onChange(of: pickedItem) { item in
            Task { await loadPicked(item) }
        }
        .onChange(of: model.state.isDone) { isDone in
            if isDone { onFinished() }
        }
        // VisionKit's own full-screen scanner. It is presented rather than embedded because that is
        // how the class ships: it owns its chrome, its shutter and its corner-drag review step, and
        // an `UIViewControllerRepresentable` sitting inside a `VStack` would put our bar on top of
        // its bar.
        .fullScreenCover(
            isPresented: Binding(
                get: { model.state.isScannerPresented },
                // Only fires when SwiftUI dismisses on its own; the delegate path clears the flag
                // itself. Guarded so a dismissal that follows a delivered scan cannot be read as a
                // cancellation of it.
                set: { presented in
                    if !presented, model.state.isScannerPresented { model.onScanCancelled() }
                }
            )
        ) {
            DocumentCameraView(
                onScanned: model.onScanned,
                onCancelled: model.onScanCancelled,
                onFailed: model.onScanFailed
            )
            .ignoresSafeArea()
        }
    }

    // MARK: - The wireframe's dark bar

    /// `✕ Cancel · Capture: Insurance`. No flash toggle: VisionKit draws its own, inside its own
    /// scanner, and a second one out here would control nothing.
    private var bar: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Button(action: model.cancel) {
                Image(systemName: "xmark")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(MageRideScannerColor.onScanner)
                    .frame(
                        width: MageRideControl.minimumTapTarget,
                        height: MageRideControl.minimumTapTarget
                    )
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text(key: "action_close"))

            Text(model.state.titleText)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideScannerColor.onScanner)

            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.xs)
    }

    // MARK: - What sits where the viewfinder is

    @ViewBuilder
    private func content(for state: DocumentScannerState) -> some View {
        if state.isBusy {
            ProgressView().tint(MageRideScannerColor.accent)
        } else if state.canScan {
            // The scanner is either up or about to be. The hint is the wireframe's own line and is
            // what a driver reads in the moment between tapping a capture tile and VisionKit
            // appearing over this screen.
            VStack(spacing: MageRideSpacing.sm) {
                Image(systemName: "doc.viewfinder")
                    .font(.system(size: MageRideControl.illustrationIcon))
                    .foregroundStyle(MageRideScannerColor.accent)
                Text(key: "capture_hint")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideScannerColor.hint)
                    .multilineTextAlignment(.center)
            }
            .padding(MageRideSpacing.lg)
            .accessibilityElement(children: .combine)
        } else {
            cameraUnavailable
        }
    }

    /// D2' §SCR-DI-005's *"Permission-denied → 'Allow camera' prompt"*, with the way out.
    ///
    /// The same panel covers the device that has **no** document camera — every simulator, which is
    /// also every CI run. Settings has nothing to offer that case, so the CTA disappears and the
    /// gallery is the whole answer.
    private var cameraUnavailable: some View {
        VStack(spacing: MageRideSpacing.sm) {
            Text(key: "capture_permission_title")
                .mageFont(.title)
                .foregroundStyle(MageRideScannerColor.onScanner)
                .multilineTextAlignment(.center)

            Text(key: "capture_permission_body")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideScannerColor.hint)
                .multilineTextAlignment(.center)

            if model.state.isBlockedInSettings || model.state.camera == .notDetermined {
                Button(action: { Task { await model.allowCamera() } }) {
                    Text(key: "capture_permission_allow")
                }
                .buttonStyle(.mageCta)
            }

            if model.state.offersGallery {
                galleryPicker
            }
        }
        .padding(MageRideSpacing.lg)
    }

    private var galleryPicker: some View {
        PhotosPicker(selection: $pickedItem, matching: .images, photoLibrary: .shared()) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: "photo.on.rectangle")
                    .font(.footnote)
                Text(key: "capture_from_gallery")
                    .mageFont(.bodySmall)
            }
            .foregroundStyle(MageRideScannerColor.accent)
            .frame(minHeight: MageRideControl.minimumTapTarget)
        }
    }

    // MARK: - Failure

    private func failure(_ errorKey: String) -> some View {
        VStack(spacing: MageRideSpacing.xs) {
            Text(key: errorKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.error)
                .multilineTextAlignment(.center)

            if model.state.canScan {
                Button(action: model.retry) {
                    Text(key: "action_retry")
                }
                .buttonStyle(.mageCta)
            }
        }
        .padding(MageRideSpacing.md)
    }

    /// Reads the picked image into memory.
    ///
    /// Bytes rather than the picker's item, for ``CapturedImage``'s own reason: a `PhotosPickerItem`
    /// is valid for the session that produced it, and the step this image belongs to outlives
    /// several of those.
    private func loadPicked(_ item: PhotosPickerItem?) async {
        guard let item else { return }
        guard let data = try? await item.loadTransferable(type: Data.self) else { return }
        model.onPicked(data)
    }
}

/// `VNDocumentCameraViewController`, as a SwiftUI view.
///
/// **This is the whole of AL-43's drag-corner crop on iOS.** The controller proposes a quadrilateral
/// from its own edge detection, lets the driver drag all four corners, applies the perspective
/// transform on confirm and hands back a de-skewed page — which is the sequence D2' §SCR-DI-005
/// specifies and the reason `apps/driver-android`'s `CropQuad` / `DocumentEdgeDetector` have no
/// counterpart in this target.
///
/// Its chrome is Apple's, so it is localised by iOS in whatever language the driver's handset is
/// set to — including Sinhala and Tamil — and nothing in `Localizable.strings` names its buttons.
struct DocumentCameraView: UIViewControllerRepresentable {

    /// The de-skewed pages, in scan order.
    let onScanned: ([UIImage]) -> Void

    /// The controller's own Cancel.
    let onCancelled: () -> Void

    /// The controller failed — a camera fault, not a driver error.
    let onFailed: () -> Void

    /// Whether this device has a document camera. `false` on every simulator.
    static var isSupported: Bool { VNDocumentCameraViewController.isSupported }

    func makeUIViewController(context: Context) -> VNDocumentCameraViewController {
        let controller = VNDocumentCameraViewController()
        controller.delegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: VNDocumentCameraViewController, context: Context) {
        // Nothing to push: the controller owns every piece of its own state, and re-assigning the
        // delegate on each SwiftUI update is how a scan in progress gets torn down.
    }

    func makeCoordinator() -> Coordinator { Coordinator(view: self) }

    /// The delegate. A class, because `VNDocumentCameraViewControllerDelegate` is an Objective-C
    /// protocol and a SwiftUI `View` is a struct.
    final class Coordinator: NSObject, VNDocumentCameraViewControllerDelegate {

        private let view: DocumentCameraView

        init(view: DocumentCameraView) {
            self.view = view
        }

        func documentCameraViewController(
            _ controller: VNDocumentCameraViewController,
            didFinishWith scan: VNDocumentCameraScan
        ) {
            let pages = (0..<scan.pageCount).map { scan.imageOfPage(at: $0) }
            view.onScanned(pages)
        }

        func documentCameraViewControllerDidCancel(_ controller: VNDocumentCameraViewController) {
            view.onCancelled()
        }

        func documentCameraViewController(
            _ controller: VNDocumentCameraViewController,
            didFailWithError error: Error
        ) {
            view.onFailed()
        }
    }
}
