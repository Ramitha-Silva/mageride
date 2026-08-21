import SwiftUI
import VisionKit

/// SCR-DI-027's **▣ Scan device QR** — VisionKit's live scanner, presented as a sheet.
///
/// **Δ Section C, and it is D2' §SCR-DI-027's own SwiftUI column.** That table reads *"QR scan ·
/// CameraX + ML Kit · `DataScannerViewController` · device QR"*, so the two apps decode the same
/// sticker through two different first-party stacks: Android added the reader half of
/// `com.google.zxing:core` behind a CameraX `ImageAnalysis` (C074's own deviation note, because ML Kit
/// would have meant a Play-services dependency and a downloaded model), and here the platform ships a
/// scanner that does barcodes, live highlighting and the reticle animation with no dependency at all.
/// This is the first decoder linked into this target — the C091 handoff's *"no decoder is linked at
/// all"* was a statement about the binary at that point, and ``WalletFenceTests`` still pins the part
/// that matters: nothing in the **wallet** scans a code (AL-34).
///
/// **A sheet rather than a route or a takeover.** A QR read is a short string that comes straight back
/// to the model underneath, so it needs neither a ``DriverRoute`` nor ``DocumentCaptureCoordinator`` —
/// which is exactly the reasoning the Android twin gives for drawing its viewfinder in a `Dialog`.
///
/// - Parameters:
///   - onScanned: The first payload read. The sheet is closed by the model, not from in here: what a
///     payload means is ``TrackerImei/imeiIn(_:)``'s answer, and a scanner that dismissed itself on an
///     unreadable code would leave the driver with no idea why.
///   - onDismiss: The Cancel button, and the swipe.
@MainActor
struct DeviceQrScannerSheet: View {

    let onScanned: (String) -> Void
    let onDismiss: () -> Void

    var body: some View {
        NavigationStack {
            ZStack {
                if DeviceQrScannerView.isSupported {
                    DeviceQrScannerView(onScanned: onScanned)
                        .ignoresSafeArea(edges: .bottom)
                } else {
                    unsupported
                }
            }
            .navigationTitle(Text(key: "tracker_scan_action"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(action: onDismiss) { Text(key: "action_cancel") }
                }
            }
            .safeAreaInset(edge: .bottom) {
                Text(key: "tracker_scan_hint")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity)
                    .padding(MageRideSpacing.sm)
                    .background(MageRideColor.background)
            }
        }
    }

    /// Every simulator, and any device older than the A12 the data scanner needs.
    ///
    /// Drawn rather than prevented: the screen's Scan button is disabled when the scanner cannot run,
    /// and this is the second line of that defence for a device whose answer changes underneath a
    /// sheet that is already up. Typing is the path that always works.
    private var unsupported: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "qrcode.viewfinder")
                .font(.system(size: MageRideControl.illustrationIcon))
                .foregroundStyle(MageRideColor.outlineVariant)
            Text(key: "tracker_scan_unsupported")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .padding(MageRideSpacing.lg)
    }
}

/// `DataScannerViewController` restricted to QR codes.
///
/// The controller owns the camera session, the viewfinder, the highlight and the reticle D2' asks for
/// under **Anim**; what this wrapper adds is the one restriction that matters — `.barcode(symbologies:
/// [.qr])`, so a data scanner that can also read text and every other symbology is not offered a
/// driving licence lying on the seat.
struct DeviceQrScannerView: UIViewControllerRepresentable {

    /// A payload, as printed on the device. Interpreting it is ``TrackerImei/imeiIn(_:)``'s job.
    let onScanned: (String) -> Void

    /// Whether this device can run the scanner **and** is allowed to.
    ///
    /// Two separate answers and both are needed: `isSupported` is the hardware (A12 and later, so
    /// `false` on every simulator) and `isAvailable` is the camera grant plus Screen Time
    /// restrictions. Neither is something the app can talk its way past.
    /// `@MainActor` because both VisionKit properties are, and a `UIViewControllerRepresentable`
    /// does not confer that on its own statics under Swift 5. Both callers are already on the actor.
    @MainActor
    static var isSupported: Bool {
        DataScannerViewController.isSupported && DataScannerViewController.isAvailable
    }

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let controller = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced,
            // A tracker sticker is stationary and so is the phone held up to it; asking for high-frame-
            // rate tracking would spend battery on movement that is not happening.
            recognizesMultipleItems: false,
            isHighFrameRateTrackingEnabled: false,
            isHighlightingEnabled: true
        )
        controller.delegate = context.coordinator
        return controller
    }

    /// Scanning is started here rather than in `makeUIViewController` because the controller refuses
    /// until it is in a window — `startScanning()` throws `ScanningUnavailable` before that.
    func updateUIViewController(_ controller: DataScannerViewController, context: Context) {
        guard !controller.isScanning else { return }
        try? controller.startScanning()
    }

    static func dismantleUIViewController(_ controller: DataScannerViewController, coordinator: Coordinator) {
        controller.stopScanning()
    }

    func makeCoordinator() -> Coordinator { Coordinator(view: self) }

    /// The delegate. A class, because `DataScannerViewControllerDelegate` is an Objective-C protocol
    /// and a SwiftUI `View` is a struct.
    @MainActor
    final class Coordinator: NSObject, DataScannerViewControllerDelegate {

        private let view: DeviceQrScannerView

        /// What has already been handed over. The scanner re-reports a code it is still looking at on
        /// every frame, and delivering the same payload sixty times a second would re-run the model's
        /// whole read for a driver holding the phone steady.
        private var delivered: String?

        init(view: DeviceQrScannerView) {
            self.view = view
        }

        func dataScanner(
            _ scanner: DataScannerViewController,
            didAdd addedItems: [RecognizedItem],
            allItems: [RecognizedItem]
        ) {
            deliver(from: addedItems)
        }

        /// A driver who taps the highlighted code rather than waiting. Same payload, same handler.
        func dataScanner(_ scanner: DataScannerViewController, didTapOn item: RecognizedItem) {
            deliver(from: [item])
        }

        private func deliver(from items: [RecognizedItem]) {
            for item in items {
                guard case .barcode(let barcode) = item, let payload = barcode.payloadStringValue else { continue }
                guard payload != delivered else { continue }
                delivered = payload
                view.onScanned(payload)
                return
            }
        }
    }
}
