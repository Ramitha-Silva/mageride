import MageRideShared
import SwiftUI

/// **SCR-DI-015 · the active ride** — `Accepted → DriverArrived → InProgress → Completed`.
///
/// The wireframe: a navigation banner, the map with the pickup pin, and a sheet carrying the rider, the
/// route and the fare, a **Call rider** / **SOS** pair, and one big CTA whose label is the ride's own
/// next step.
///
/// **The QR confirm sheet is not an extra** (AL-47, US-26.1). A ride the passenger paid by scanning the
/// driver's own bank LankaQR has no gateway callback that could settle it — the money moved
/// bank-to-bank and only the driver's banking app saw it — so *"QR payment received?"* **is** the
/// settlement, and confirming is what posts the earning (R-05).
///
/// - Parameters:
///   - onFinished: The ride reached a terminal state; the driver goes back to standby.
///   - onOpenVoipCall: SCR-DI-031 (C093) — AL-48's chooser answered **Free call**.
///   - onOpenSos: SCR-DI-032 (C093) — the alarm is a screen with a confirmation, not a button that
///     fires.
@MainActor
struct ActiveRideScreen: View {

    @StateObject private var model: ActiveRideModel

    private let rideId: String
    private let onFinished: () -> Void
    private let onOpenVoipCall: (String) -> Void
    private let onOpenSos: (String) -> Void

    init(
        rideId: String,
        model: @autoclosure @escaping () -> ActiveRideModel,
        onFinished: @escaping () -> Void,
        onOpenVoipCall: @escaping (String) -> Void,
        onOpenSos: @escaping (String) -> Void
    ) {
        self.rideId = rideId
        _model = StateObject(wrappedValue: model())
        self.onFinished = onFinished
        self.onOpenVoipCall = onOpenVoipCall
        self.onOpenSos = onOpenSos
    }

    var body: some View {
        content
            .navigationBarTitleDisplayMode(.inline)
            .task {
                model.start()
                await model.refresh()
            }
            .onDisappear(perform: model.stop)
            .sheet(item: Binding(get: { model.state.sheet }, set: { model.open($0) })) { sheet in
                RideSheets(sheet: sheet, model: model)
            }
            // The two screens C093 owns. Both are takeovers about this ride, so both carry its id.
            .onChange(of: model.state.isVoipRequested) { requested in
                guard requested else { return }
                model.consume()
                onOpenVoipCall(rideId)
            }
            .onChange(of: model.state.isSosRequested) { requested in
                guard requested else { return }
                model.consume()
                onOpenSos(rideId)
            }
            .onChange(of: model.state.isFinished) { finished in
                if finished { onFinished() }
            }
    }

    /// A parcel is not a passenger, and **this screen does not yet know what to do about it.**
    ///
    /// ``DriverRoute/activeRide(rideId:)`` is the destination for every live job — R-01 keeps one
    /// aggregate and ``PushRouter`` resolves `mageride://package/{id}` to it — so the kind is not known
    /// until the ride has been read. On Android, `ActiveRideScreen` reads it and hands over to
    /// `DeliveryScreen` on `RideKind.PACKAGE`; **SCR-DI-016a/b/c is C089's** and there is nothing to
    /// hand over to yet.
    ///
    /// Until it lands a package draws this screen, and the model is already honest about it:
    /// ``ActiveRideState/isPackage`` switches the Call button to *"Call sender"* and switches the poll
    /// **off** (``ActiveRideState/isPollable``), so C089's own loop will be the only one folding server
    /// states onto the ride. The hand-over is one `if` at the top of this property — the same shape the
    /// Android twin has — and it is C089's line to add.
    @ViewBuilder
    private var content: some View {
        VStack(spacing: 0) {
            DashboardBanner(
                text: model.state.navigationHintKey.localised,
                accent: MageRideColor.secondary,
                symbolName: "location.north.line.fill"
            )

            if let errorKey = model.state.errorKey {
                DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                    .onTapGesture(perform: model.consume)
            }

            // MAP-10's 100 m circle, drawn where the driver is being sent: the pickup until they are on
            // board, the drop afterwards. It is D5' §7's arrival geofence — the same circle that decides
            // `DriverArrived` — so the driver can see what they have to reach.
            DriverHomeMap(
                position: model.state.position,
                token: nil,
                geofence: model.state.geofence
            )

            sheet
        }
        .background(MageRideColor.background)
    }

    /// The wireframe's sheet: rider, route, the two buttons, and the state's own CTA.
    private var sheet: some View {
        DashboardSheet {
            HStack(spacing: MageRideSpacing.xs) {
                Image(systemName: "person.crop.circle.fill")
                    .font(.system(size: MageRideControl.avatarSmall))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                VStack(alignment: .leading, spacing: 1) {
                    Text(model.state.ride?.riderName ?? "ride_rider".localised)
                        .mageFont(.title)
                        .foregroundStyle(MageRideColor.onSurface)
                    Text(model.state.routeLine)
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
                Spacer(minLength: 0)
            }
            .accessibilityElement(children: .combine)

            HStack(spacing: MageRideSpacing.xs) {
                outlinedAction(
                    labelKey: model.state.isPackage ? "ride_call_sender" : "ride_call",
                    symbolName: "phone.fill",
                    tint: MageRideColor.onSurface,
                    isEnabled: model.state.ride?.counterpartyPhone != nil
                ) {
                    model.open(.callType)
                }

                outlinedAction(
                    labelKey: "ride_sos",
                    symbolName: "shield.lefthalf.filled",
                    tint: MageRideColor.error,
                    // SCR-DI-032 needs a fix to attach to the alarm — `POST /v1/sos` has no
                    // positionless form.
                    isEnabled: model.state.position != nil,
                    action: model.openSos
                )
            }

            forwardCta
        }
    }

    /// D2' §SCR-DI-015's table colours the two ends of the ride by consequence: Start is `success`,
    /// Complete is `error`. Arriving is neither — it is the ordinary primary CTA.
    @ViewBuilder
    private var forwardCta: some View {
        if let actionKey = model.state.forwardActionKey {
            Button { Task { await model.advance() } } label: {
                Text(key: actionKey)
            }
            .buttonStyle(ctaStyle)
            .disabled(model.state.isBusy)
        } else if model.state.awaitingQrConfirm {
            Button { model.open(.qrConfirm) } label: {
                Text(key: "ride_qr_title")
            }
            .buttonStyle(.mageCta(loading: model.state.isBusy))
            .disabled(model.state.isBusy)
        }
    }

    private var ctaStyle: MageRideCtaStyle {
        guard let rideState = model.state.rideState else { return .mageCta(loading: model.state.isBusy) }
        switch rideState {
        case RideState.driverArrived: return .mageCtaStatus(MageRideColor.success, loading: model.state.isBusy)
        case RideState.inProgress: return .mageCtaStatus(MageRideColor.error, loading: model.state.isBusy)
        default: return .mageCta(loading: model.state.isBusy)
        }
    }

    /// The wireframe's `btn-out` — an outlined half-width action.
    private func outlinedAction(
        labelKey: String,
        symbolName: String,
        tint: Color,
        isEnabled: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: symbolName)
                    .font(.footnote)
                Text(key: labelKey)
                    .mageFont(.bodySmall)
            }
            .foregroundStyle(tint)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: 1)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
        .opacity(isEnabled ? 1 : 0.4)
    }
}
