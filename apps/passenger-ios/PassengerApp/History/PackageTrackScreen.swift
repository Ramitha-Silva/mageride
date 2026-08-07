import MageRideShared
import SwiftUI

/// SCR-PI-020 and SCR-PI-021 — the parcel, from whichever end is holding the phone.
///
/// Both cells: a short map with the driver's live marker, the **four-step bar** (`Pickup pending ·
/// Picked · Transit · Delivered`), this party's own handover code in a `card fill`, and the driver
/// row underneath.
///
/// **Which of the two this is, is a fact about the ride and not about the link.**
/// `mageride://package/{rideId}` is the same URI for both parties — the recipient gets it on
/// `package_picked_up`, the sender on `package_delivered` — so the model reads the ride and decides.
/// See ``PackageParty``.
///
/// **The two cells differ in three places and each difference is drawn.** The sender's header is
/// *"Package · sending"* and the recipient's is *"Incoming package"*; the sender's code is the
/// **pickup** OTP and the recipient's is the **delivery** OTP; and the sender's driver row carries a
/// **📞 Call** where the recipient's carries the **ETA**. The last is the wireframe's own decision and
/// is followed here — `apps/passenger-android` draws a Call on both, which is a difference recorded
/// in the C099 handoff rather than reproduced.
@MainActor
struct PackageTrackScreen: View {

    @StateObject private var model: PackageTrackModel

    /// SCR-PI-015a's *"Free call"* — the VoIP screen (C102).
    let onFreeCall: () -> Void

    private let choice: CallChoice
    private let contact: RideContact

    @State private var isCallOpen = false

    init(
        rideId: String,
        history: HistoryRepository,
        live: PassengerLiveMap,
        otps: PackageOtps,
        signedInUserId: String?,
        choice: CallChoice,
        contact: RideContact,
        onFreeCall: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: PackageTrackModel(
                rideId: rideId,
                history: history,
                live: live,
                otps: otps,
                signedInUserId: signedInUserId
            )
        )
        self.choice = choice
        self.contact = contact
        self.onFreeCall = onFreeCall
    }

    var body: some View {
        VStack(spacing: 0) {
            map

            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                StepperBar(titleKeys: PackageTrackState.stepKeys, current: model.state.step)

                if let headline = statusHeadline {
                    Text(headline)
                        .mageFont(.bodyEmphasis)
                        .foregroundStyle(MageRideColor.onSurface)
                }

                // Nothing to read out once the parcel has changed hands — the card would be a code
                // for a handover that already happened.
                if !model.state.isDelivered {
                    HandoverCodeCard(titleKey: codeTitleKey, code: model.state.otp)
                }

                driverRow

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                Spacer(minLength: 0)
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: titleKey))
        .navigationBarTitleDisplayMode(.inline)
        .task { model.start() }
        .onDisappear { model.stop() }
        .sheet(isPresented: $isCallOpen) {
            CallChooserSheet(
                driverName: model.state.ride?.driver?.name,
                driverRating: model.state.ride?.driver?.rating?.doubleValue,
                // AL-48's clear number. **On a package ride this field is the far *party*, not the
                // driver** — see ``PackageTrackModel`` and the gap the C099 handoff records; it is
                // what the contract gives a participant and what the Android twin dials.
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

    // MARK: -

    /// The cell's `.map` strip — the driver's marker, and the two ends of the delivery.
    ///
    /// The camera follows the **driver** while there is one, because that is the thing that moves;
    /// before the first `DriverPosition` it opens on the drop-off, which is where the parcel is going.
    private var map: some View {
        MageRideMap(
            vehicles: driverMarker,
            pins: pins,
            camera: camera
        )
        .allowsHitTesting(false)
        .frame(height: MageRideControl.packageMapHeight)
    }

    /// SCR-PI-021's `In transit · ~12 min`.
    ///
    /// Drawn only where the cell draws it — on the recipient's screen, and only once there is an ETA
    /// to state. The sender's cell puts the step name in the bar and nothing above the card.
    private var statusHeadline: String? {
        guard model.state.party == .recipient, let minutes = model.state.etaMinutes else { return nil }
        let step = PackageTrackState.stepKeys[min(model.state.step, PackageTrackState.deliveredStep)]
        return step.localised + MageRideSymbols.separator + "ride_arriving_minutes".localisedFormat(Int(minutes))
    }

    /// The driver, and what each party may do about them.
    @ViewBuilder
    private var driverRow: some View {
        if let driver = model.state.ride?.driver {
            HStack(spacing: MageRideSpacing.xs) {
                DriverIdentityRow(
                    name: driver.name,
                    // The sender's cell draws `K. Fernando · Tuk ★4.7`; the recipient's draws
                    // `K. Fernando · Tuk · ABC-1234` over `ETA 12 min`.
                    rating: model.state.party == .sender ? driver.rating?.doubleValue : nil,
                    vehicle: VehicleToken.forType(driver.vehicleType)?.nameKey.localised,
                    plate: model.state.party == .recipient ? driver.registrationNumber : nil,
                    etaMinutes: model.state.party == .recipient ? model.state.etaMinutes : nil
                )

                // **The Call is the sender's only** — the recipient's cell draws none. See the
                // type's note.
                if model.state.party == .sender {
                    TextLink(key: "ride_call") { isCallOpen = true }
                }
            }
        }
    }

    private var titleKey: String {
        model.state.party == .sender ? "package_track_sending" : "package_track_incoming"
    }

    private var codeTitleKey: String {
        model.state.party == .sender ? "package_pickup_otp_label" : "package_delivery_otp_label"
    }

    /// The driver's marker, as one frame the map tweens (MAP-04).
    ///
    /// The vehicle's *type* is passed so the marker is drawn in §0.2's legend colour rather than in
    /// the fallback grey — the same rule MAP-03 makes about every other marker in this app.
    private var driverMarker: [MapVehicle] {
        guard let position = model.state.driverPosition else { return [] }
        return [
            MapVehicle(
                vehicleId: PackageTrackScreen.driverMarkerId,
                lat: position.lat,
                lng: position.lng,
                type: model.state.ride?.driver?.vehicleType?.wire
            ),
        ]
    }

    private var pins: [MapPin] {
        guard let ride = model.state.ride else { return [] }
        return [
            MapPin(kind: VehicleLayers.pinPickup, lat: ride.pickup.lat, lng: ride.pickup.lng),
            MapPin(kind: VehicleLayers.pinDropoff, lat: ride.dropoff.lat, lng: ride.dropoff.lng),
        ]
    }

    private var camera: MapCamera {
        if let position = model.state.driverPosition {
            return MapCamera(lat: position.lat, lng: position.lng)
        }
        guard let dropoff = model.state.ride?.dropoff else { return .colombo }
        return MapCamera(lat: dropoff.lat, lng: dropoff.lng)
    }

    /// The one marker this screen ever draws. `MageRideMap` keys its source on the id, and a ride's
    /// driver has no `vehicleId` on `RideDriver` at all.
    private static let driverMarkerId = "package-driver"
}
