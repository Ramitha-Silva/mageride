import SwiftUI
import VisionKit

/// SCR-PI-017's *"📷 Scan driver's QR"* — VisionKit's live scanner, presented as a sheet.
///
/// **Δ Section C, and it is the cell's own `Δ iOS` clause** (*"scanner via `AVCaptureSession` /
/// DataScanner"*). `apps/passenger-android` added CameraX plus the reader half of
/// `com.google.zxing:core` behind an `ImageAnalysis`; here the platform ships a scanner that does
/// barcodes, live highlighting and the reticle with no dependency at all — the same call
/// `apps/driver-ios` made for SCR-DI-027, and this is the first decoder linked into *this* target.
///
/// **A sheet rather than a route.** A QR read is a short string that comes straight back to the model
/// underneath, so the amount due and the chosen rail survive; a destination would have to carry both
/// and hand the result back through the back stack. The Android twin draws its viewfinder in a
/// `Dialog` for the same reason.
///
/// **The payload is not interpreted here.** It is the driver's own bank merchant string and goes to
/// `POST /v1/fare/pay/scan-driver-qr` exactly as read — decoding it further would be this app making
/// claims about somebody else's bank.
@MainActor
struct DriverQrScannerSheet: View {

    /// The first payload read. The sheet is closed by the **model**, not from in here: what a payload
    /// means is fare-svc's answer, and a scanner that dismissed itself on a QR the server then
    /// rejected would leave the passenger with no idea why.
    let onScanned: (String) -> Void

    let onDismiss: () -> Void

    var body: some View {
        NavigationStack {
            ZStack {
                if DriverQrScannerView.isSupported {
                    DriverQrScannerView(onScanned: onScanned)
                        .ignoresSafeArea(edges: .bottom)
                } else {
                    unsupported
                }
            }
            .navigationTitle(Text(key: "pay_scan"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(action: onDismiss) { Text(key: "action_cancel") }
                }
            }
            .safeAreaInset(edge: .bottom) {
                Text(key: "pay_scan_explainer")
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
    /// Drawn rather than prevented: the screen's Scan button already checks
    /// ``CameraAuthoriser/isScannerSupported``, and this is the second line of that defence for a
    /// handset whose answer changes underneath a sheet that is already up. The bank-app link is the
    /// path that always works.
    private var unsupported: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "qrcode.viewfinder")
                .font(.system(size: MageRideControl.illustrationIcon))
                .foregroundStyle(MageRideColor.outlineVariant)
            Text(key: "pay_scan_unsupported")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .padding(MageRideSpacing.lg)
    }
}

/// `DataScannerViewController` restricted to QR codes.
///
/// The controller owns the camera session, the viewfinder, the highlight and the reticle; what this
/// wrapper adds is the one restriction that matters — `.barcode(symbologies: [.qr])`, so a scanner
/// that can also read text and every other symbology is not offered the driver's licence on the
/// dashboard.
struct DriverQrScannerView: UIViewControllerRepresentable {

    /// A payload, as printed on the driver's QR. Interpreting it is fare-svc's job.
    let onScanned: (String) -> Void

    /// Whether this device can run the scanner **and** is allowed to.
    ///
    /// Two separate answers and both are needed: `isSupported` is the hardware (A12 and later, so
    /// `false` on every simulator) and `isAvailable` is the camera grant plus Screen Time
    /// restrictions. Neither is something the app can talk its way past — which is why
    /// ``PayFareModel/openScanner()`` asks for the grant *before* this view is presented.
    static var isSupported: Bool {
        DataScannerViewController.isSupported && DataScannerViewController.isAvailable
    }

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let controller = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced,
            // A printed QR is stationary and so is the phone held up to it; asking for high-frame-rate
            // tracking would spend battery on movement that is not happening.
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

        private let view: DriverQrScannerView

        /// What has already been handed over. **The latch is what makes one QR one payment**: the
        /// scanner re-reports a code it is still looking at on every frame, and delivering the same
        /// payload sixty times a second would post `POST /v1/fare/pay/scan-driver-qr` sixty times.
        private var delivered: String?

        init(view: DriverQrScannerView) {
            self.view = view
        }

        func dataScanner(
            _ scanner: DataScannerViewController,
            didAdd addedItems: [RecognizedItem],
            allItems: [RecognizedItem]
        ) {
            deliver(from: addedItems)
        }

        /// A passenger who taps the highlighted code rather than waiting. Same payload, same handler.
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
