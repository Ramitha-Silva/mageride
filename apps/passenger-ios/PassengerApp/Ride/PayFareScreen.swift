import MageRideShared
import SwiftUI

/// SCR-PI-017 — paying, and waiting for the driver to say it arrived.
///
/// The cell: `‹ Back · Pay fare`, then a centred body — *Amount due*, the amount as an `h-display`,
/// a dashed panel saying *"Scan driver's QR to pay"*, the `📷` CTA, the `🏦 Pay with my bank app`
/// tonal CTA, *"Awaiting confirmation… (88s)"* and a `Retry | Switch to Cash` link pair.
///
/// **This app renders no MageRide QR** (AL-22). The passenger *scans the driver's* printed or
/// on-screen LankaQR, or opens their own bank app through a LankaQR link (AL-15). Both move money
/// bank to bank, which is exactly why **no callback ever reaches fare-svc** and why settlement is
/// AL-47's attestation: *"I've paid"* → the driver confirms → `DriverConfirmedQR`, terminal.
///
/// **Unconfirmed past five minutes offers help rather than a longer spinner.** The driver is
/// re-pushed at +5 min; if nothing comes back the route is Support → the Finance dispute queue, and
/// there is no money for the platform to reverse either way.
@MainActor
struct PayFareScreen: View {

    @StateObject private var model: PayFareModel

    let onBack: () -> Void

    /// SCR-PI-030's *"Get help"* (C102).
    let onSupport: () -> Void

    /// Settled — SCR-PI-018, replacing this screen.
    let onSettled: () -> Void

    init(
        rideId: String,
        method: PaymentMethod,
        rides: RideRepository,
        camera: CameraAuthoriser,
        bank: BankAppHandoff,
        onBack: @escaping () -> Void,
        onSupport: @escaping () -> Void,
        onSettled: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: PayFareModel(
                rideId: rideId,
                method: method,
                rides: rides,
                camera: camera,
                bank: bank
            )
        )
        self.onBack = onBack
        self.onSupport = onSupport
        self.onSettled = onSettled
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                AmountHeadline(captionKey: "pay_amount_due", amountMinor: model.state.amountMinor)

                content(for: model.state)

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                if !model.state.isConfirmed {
                    links
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "pay_title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) {
                Button(action: onBack) { Text(key: "action_back") }
            }
        }
        .task { model.start() }
        .onChange(of: model.state.isConfirmed) { confirmed in
            if confirmed { onSettled() }
        }
        .sheet(isPresented: scannerBinding) {
            DriverQrScannerSheet(
                onScanned: { model.onQrScanned($0) },
                onDismiss: { model.closeScanner() }
            )
        }
    }

    /// The five states the cell's own line names: scan, deep link, claim, wait, confirmed.
    @ViewBuilder
    private func content(for state: PayFareState) -> some View {
        if state.isConfirmed {
            confirmed
        } else if state.isClaimed {
            waiting
        } else if state.isDriverQr {
            driverQr
        } else {
            settling
        }
    }

    /// The dashed panel and the two ways to pay a driver's own QR, plus AL-47's claim.
    private var driverQr: some View {
        VStack(spacing: MageRideSpacing.sm) {
            scanPanel

            Button { model.openScanner() } label: {
                Label { Text(key: "pay_scan") } icon: { Image(systemName: "camera.fill") }
            }
            .buttonStyle(.mageCta)

            Button { model.openBankApp() } label: {
                Label { Text(key: "pay_bank_app") } icon: { Image(systemName: "building.columns.fill") }
            }
            .buttonStyle(.mageCtaTonal)

            if model.state.isCameraBlocked {
                // Not an error: AL-15's link and AL-47's claim both still work. What this offers is
                // the one thing the app cannot do for them.
                VStack(spacing: MageRideSpacing.xxs) {
                    Text(key: "pay_scan_no_camera")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                    TextLink(key: "permission_open_settings") { model.openCameraSettings() }
                }
            }

            // AL-47's claim. Offered whether or not the scan went through this app, because the
            // passenger may well have paid from their bank app instead — the platform saw neither.
            OutlinedAction(titleKey: "pay_ive_paid") { model.claimPaid() }
                .disabled(model.state.isBusy)
        }
    }

    /// The cell's `card` with `border:1.5px dashed var(--primary)` — *"Scan driver's QR to pay"*.
    private var scanPanel: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "qrcode.viewfinder")
                .font(.system(size: MageRideControl.illustrationIcon))
                .foregroundStyle(MageRideColor.primary)
            Text(key: "pay_scan_title")
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)
            Text(key: "pay_scan_explainer")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, minHeight: MageRideControl.scanPanel)
        .padding(MageRideSpacing.sm)
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(
                    MageRideColor.primary,
                    style: StrokeStyle(
                        lineWidth: MageRideControl.selectedBorder,
                        dash: [MageRideControl.scanPanelDash]
                    )
                )
        }
        .accessibilityElement(children: .combine)
    }

    /// *"Awaiting confirmation… (88s)"*, and Support once the nudge window has passed.
    private var waiting: some View {
        VStack(spacing: MageRideSpacing.xs) {
            HStack(spacing: MageRideSpacing.xs) {
                ProgressView()
                Text("pay_waiting".localisedFormat(model.state.waiting))
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
            if model.state.offersSupport {
                TextLink(key: "pay_get_help", action: onSupport)
            }
        }
    }

    /// A cash or wallet rail, in flight. A wallet fare is `Succeeded` the moment `POST /v1/fare/pay`
    /// returns (AL-57), so this is on screen for one round trip.
    private var settling: some View {
        HStack(spacing: MageRideSpacing.xs) {
            if model.state.isBusy { ProgressView() }
            Text(key: "pay_settling")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
    }

    /// `Confirmed ✓` — terminal, and what releases the driver's earning (R-05).
    private var confirmed: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: MageRideControl.rowIcon))
                .foregroundStyle(MageRideColor.success)
            Text(key: "pay_confirmed")
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.success)
        }
        .accessibilityElement(children: .combine)
    }

    /// The cell's `Retry | Switch to Cash` pair.
    private var links: some View {
        HStack(spacing: MageRideSpacing.md) {
            TextLink(key: "action_retry") { model.retry() }
            // US-8.15 — a rail that will not settle becomes cash, without losing the history.
            TextLink(key: "pay_switch_to_cash", isEnabled: model.state.paymentId != nil) {
                model.switchToCash()
            }
        }
    }

    private var scannerBinding: Binding<Bool> {
        Binding(
            get: { model.state.isScanning },
            set: { if !$0 { model.closeScanner() } }
        )
    }
}
