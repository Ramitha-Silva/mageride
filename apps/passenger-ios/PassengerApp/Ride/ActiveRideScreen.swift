import MageRideShared
import SwiftUI

/// SCR-PI-015 — the ride, while it is happening.
///
/// The cell: a map with the driver's live marker and the pickup pin, a `Driver arriving · 3 min`
/// pill over its top-left corner, then a drawn sheet with the driver row, the Start-code card and
/// **three** controls — `📞 Call ▾`, `⛨ SOS` and `✕`.
///
/// **Three, not four.** `apps/passenger-android`'s screen draws a fourth (share-trip) that **neither
/// wireframe draws**; D-34's link belongs to SCR-PI-029, which C084 already moved the alarm to and
/// which mints the share link beside it. Recorded in the C098 handoff — the drawing is the baseline
/// and the extra control needs a micro-change-set or removal on the other side.
///
/// **Cancelling here costs Rs 50 and the dialog says so before the tap** (US-6A.10, D-05). The debt
/// settles on the *next* trip rather than being charged now, which is precisely why it has to be
/// stated: nothing appears on a statement today, and an unwarned passenger meets it weeks later.
///
/// **`📞 Call ▾` opens SCR-PI-015a rather than dialling.** Free VoIP and a direct cellular call are
/// genuinely different things — one costs the passenger minutes and shows their number — so the
/// choice is theirs and is remembered (AL-48).
///
/// **`⛨ SOS` navigates to SCR-PI-029 rather than raising the alarm here.** The confirm, the
/// countdown, the contact list and the dispatched state are that screen's, and `POST /v1/sos` having
/// **one** caller is what stops one emergency arriving on the operator's live feed as two events.
///
/// **Payment is not a button on this screen.** The ride moves to `Completed` server-side and the
/// state change is what carries the passenger to SCR-PI-016 — a *"pay now"* control on a moving
/// vehicle would be an invitation to settle a fare that is not final yet.
@MainActor
struct ActiveRideScreen: View {

    @StateObject private var model: ActiveRideModel

    let choice: CallChoice
    let contact: RideContact

    /// The ride ended with nothing to pay — cancelled, or a no-show. Back to the map.
    let onFinished: () -> Void

    /// `Completed` / `PaymentPending` — SCR-PI-016.
    let onPayFare: () -> Void

    /// Already settled — SCR-PI-018.
    let onReceipt: () -> Void

    /// SCR-PI-028's WebRTC session (C102). VoIP failure prompts a direct dial there (US-26.4).
    let onFreeCall: () -> Void

    /// SCR-PI-029 (C102).
    let onSos: () -> Void

    @State private var isCallOpen = false

    init(
        rideId: String,
        rides: RideRepository,
        live: PassengerLiveMap,
        choice: CallChoice,
        contact: RideContact,
        onFinished: @escaping () -> Void,
        onPayFare: @escaping () -> Void,
        onReceipt: @escaping () -> Void,
        onFreeCall: @escaping () -> Void,
        onSos: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ActiveRideModel(rideId: rideId, rides: rides, live: live))
        self.choice = choice
        self.contact = contact
        self.onFinished = onFinished
        self.onPayFare = onPayFare
        self.onReceipt = onReceipt
        self.onFreeCall = onFreeCall
        self.onSos = onSos
    }

    var body: some View {
        VStack(spacing: 0) {
            map
            sheet
        }
        .background(MageRideColor.surface)
        .toolbar(.hidden, for: .navigationBar)
        .task { model.start() }
        .onDisappear { model.stop() }
        .onChange(of: model.state.handOff) { handOff in
            // Unwrapped before the switch rather than matching `.none` alongside the three cases:
            // one exhaustive switch over the enum is what makes a fourth hand-off a compile error
            // here, which is the whole point of the type.
            guard let handOff else { return }
            switch handOff {
            case .payment: onPayFare()
            case .receipt: onReceipt()
            case .finished: onFinished()
            }
        }
        .confirmationDialog(
            Text(key: "ride_cancel_title"),
            isPresented: cancelBinding,
            titleVisibility: .visible
        ) {
            Button(role: .destructive) { model.confirmCancel() } label: { Text(key: "ride_cancel_yes") }
            Button(role: .cancel) { model.dismissCancel() } label: { Text(key: "ride_cancel_no") }
        } message: {
            // **The number is in the dialog, not in a help article.** D-05 carries the debt to the
            // next trip, so a passenger who cancels today sees nothing on a statement — and this is
            // the only moment they can be told what it costs.
            if model.state.cancelIsFree {
                Text(key: "ride_cancel_free")
            } else {
                Text("ride_cancel_penalty".localisedFormat(
                    MoneyFormat.rupees(ActiveRideModel.cancellationPenaltyMinor)
                ))
            }
        }
        .sheet(isPresented: $isCallOpen) {
            CallChooserSheet(
                driverName: model.state.ride?.driver?.name,
                driverRating: model.state.ride?.driver?.rating?.doubleValue,
                driverPhone: model.state.driverPhone,
                choice: choice,
                contact: contact,
                onFreeCall: {
                    isCallOpen = false
                    onFreeCall()
                },
                onDismiss: { isCallOpen = false }
            )
        }
    }

    private var map: some View {
        MageRideMap(
            // The assigned driver, from `DriverPosition` on the `ride:{rideId}` group (US-6A.12).
            // One marker and one id: this map draws the ride, not the neighbourhood.
            vehicles: model.state.driverPosition.map {
                [MapVehicle(vehicleId: ActiveRideScreen.driverMarker, lat: $0.lat, lng: $0.lng)]
            } ?? [],
            pins: pins,
            camera: camera
        )
        // The cell's `pill-status info` — *"Driver arriving · 3 min"*, over the map's top-left
        // corner. Drawn here rather than through ``StatusPill`` because that control takes a **key**
        // and this one is a format with the ETA in it, which is the same split ``FormattedBanner``
        // exists for.
        .overlay(alignment: .topLeading) {
            if let minutes = model.state.etaMinutes {
                Text("ride_arriving".localisedFormat(Int(minutes)))
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.secondary)
                    .padding(.horizontal, MageRideSpacing.xs)
                    .padding(.vertical, MageRideSpacing.xxs / 2)
                    .background(MageRideColor.secondary.opacity(0.14), in: Capsule())
                    .padding(MageRideSpacing.sm)
            }
        }
    }

    private var sheet: some View {
        VStack(spacing: MageRideSpacing.sm) {
            SheetGrabber()

            DriverIdentityRow(
                name: model.state.ride?.driver?.name,
                rating: model.state.ride?.driver?.rating?.doubleValue,
                vehicle: vehicleName,
                plate: model.state.ride?.driver?.registrationNumber,
                etaMinutes: model.state.etaMinutes
            )

            // The wireframe's *"Start OTP — give to driver"*. Read out, never typed here: the driver
            // enters it on SCR-DI-015, which is what proves the right passenger got in. No operation
            // returns one to a passenger — see ``StartCodeCard``.
            StartCodeCard(code: nil)

            actions

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background, in: TopRoundedRectangle(radius: MageRideRadius.card))
    }

    /// `📞 Call ▾ | ⛨ SOS | ✕`, at the cell's own widths (the `✕` is `flex:.5`).
    private var actions: some View {
        HStack(spacing: MageRideSpacing.xs) {
            OutlinedAction(titleKey: "ride_call", symbolName: "phone.fill") { isCallOpen = true }

            // The cell draws this one `color:var(--error)` — the only outlined action in the app
            // that is not `primary`.
            OutlinedAction(titleKey: "ride_sos", symbolName: "shield.fill", tint: MageRideColor.error, action: onSos)

            Button { model.askToCancel() } label: {
                Image(systemName: "xmark")
                    .font(.system(size: MageRideControl.chipIcon))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(width: MageRideControl.minimumTapTarget, minHeight: MageRideControl.outlinedAction)
                    .background(
                        MageRideColor.background,
                        in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    )
                    .overlay {
                        RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                            .strokeBorder(MageRideColor.outline, lineWidth: MageRideControl.hairline * 2)
                    }
            }
            .buttonStyle(.plain)
            .disabled(model.state.isCancelling)
            .accessibilityLabel(Text(key: "ride_cancel"))
        }
    }

    private var pins: [MapPin] {
        guard let ride = model.state.ride else { return [] }
        return [
            MapPin(kind: VehicleLayers.pinPickup, lat: ride.pickup.lat, lng: ride.pickup.lng),
            MapPin(kind: VehicleLayers.pinDropoff, lat: ride.dropoff.lat, lng: ride.dropoff.lng),
        ]
    }

    /// Follows the driver once there is one, and rests on the pickup until then.
    private var camera: MapCamera {
        if let driver = model.state.driverPosition {
            return MapCamera(lat: driver.lat, lng: driver.lng)
        }
        guard let pickup = model.state.ride?.pickup else { return .colombo }
        return MapCamera(lat: pickup.lat, lng: pickup.lng)
    }

    private var vehicleName: String? {
        guard let type = model.state.ride?.driver?.vehicleType else { return nil }
        return VehicleToken.forType(type)?.nameKey.localised
    }

    /// A binding rather than `isPresented:` on a `@State`, so the dialog and the model hold one
    /// answer between them — a dismissal by swipe has to reach ``ActiveRideModel/dismissCancel()``
    /// or the next `✕` opens nothing.
    private var cancelBinding: Binding<Bool> {
        Binding(
            get: { model.state.isPendingCancel },
            set: { if !$0 { model.dismissCancel() } }
        )
    }

    /// The single marker id the driver's live position is drawn under.
    private static let driverMarker = "driver"
}
