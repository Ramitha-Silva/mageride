import MageRideShared
import SwiftUI

/// SCR-PI-014 — *"Finding a driver…"*.
///
/// The cell: a full-bleed map with the radar pulse centred on it, over a drawn sheet carrying
/// *"Finding a driver…"*, *"Sedan · usually under 2 min · **1:34** left"* and **Cancel (free)**. Two
/// minutes with nothing assigned is US-6A.11's *"No drivers available"* plus a retry.
///
/// **Cancel is free here and says so on the control.** US-6A.9 — nothing has been accepted, so
/// nothing is owed, and there is nothing to confirm. The moment a driver accepts, this screen is
/// **replaced** by SCR-PI-015, where the same action costs Rs 50 and carries a confirm. The two are
/// different screens precisely so the two cancels can never be confused, and the replace is what
/// stops an edge-swipe walking back into "finding" for a ride that has been found.
///
/// **Δ the cell draws no navigation bar**, so this screen hides the system's — the same call C097
/// made on SCR-PI-009 and for the same reason: the frame gives the map its full height and the sheet
/// carries every control.
@MainActor
struct FindingDriverScreen: View {

    @StateObject private var model: ActiveRideModel

    /// A driver accepted — SCR-PI-015 replaces this screen.
    let onAssigned: () -> Void

    /// Cancelled, or the ride ended some other way. Back to the map.
    let onFinished: () -> Void

    /// US-6A.11's retry. Back to the map, which is where a booking starts.
    let onRetry: () -> Void

    init(
        rideId: String,
        rides: RideRepository,
        live: PassengerLiveMap,
        onAssigned: @escaping () -> Void,
        onFinished: @escaping () -> Void,
        onRetry: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ActiveRideModel(rideId: rideId, rides: rides, live: live))
        self.onAssigned = onAssigned
        self.onFinished = onFinished
        self.onRetry = onRetry
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
        .onChange(of: model.state.isAssigned) { assigned in
            if assigned { onAssigned() }
        }
        .onChange(of: model.state.handOff) { handOff in
            // A ride that was cancelled or expired has nowhere else to be. A ride that reached
            // payment before this screen ever saw a driver — possible, because the poll can land
            // after several transitions — is still the same journey, and SCR-PI-015 is what routes
            // it on; handing it there keeps one screen responsible for the hand-off.
            guard let handOff else { return }
            if handOff == .finished {
                onFinished()
            } else {
                onAssigned()
            }
        }
    }

    private var map: some View {
        MageRideMap(
            pins: pins,
            // The parentheses are the fix. `pickup` is a non-optional `Place`, so in
            // `ride?.pickup.map { }` the `map` binds to the **Place** rather than to the optional
            // chain — and a Kotlin `Place` has no `map`. Parenthesising makes the whole chain the
            // `Place?` being mapped, which is what the `?? .colombo` fallback already assumed.
            camera: (model.state.ride?.pickup).map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? .colombo
        )
        .allowsHitTesting(false)
        .overlay { RadarPulse() }
    }

    private var pins: [MapPin] {
        guard let ride = model.state.ride else { return [] }
        return [
            MapPin(kind: VehicleLayers.pinPickup, lat: ride.pickup.lat, lng: ride.pickup.lng),
            MapPin(kind: VehicleLayers.pinDropoff, lat: ride.dropoff.lat, lng: ride.dropoff.lng),
        ]
    }

    private var sheet: some View {
        VStack(spacing: MageRideSpacing.sm) {
            SheetGrabber()

            Text(key: model.state.noDriver ? "finding_none" : "finding_title")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.center)

            Text(caption)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }

            if model.state.noDriver {
                Button(action: onRetry) { Text(key: "finding_retry") }
                    .buttonStyle(.mageCta)
            }

            // "Cancel (free)" — the word is on the control rather than in a dialog, because before
            // acceptance there is nothing to confirm and nothing to warn about (US-6A.9).
            OutlinedAction(titleKey: "finding_cancel_free") { model.confirmCancel() }
                .disabled(model.state.isCancelling)
        }
        .padding(MageRideSpacing.md)
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background, in: TopRoundedRectangle(radius: MageRideRadius.card))
    }

    /// `Sedan · Usually under 2 minutes · 1:34 left`.
    ///
    /// The tier is prepended rather than folded into the format string, because the cell shows it and
    /// `apps/passenger-android`'s `finding_caption` does not carry it — one composed line keeps both
    /// apps on the same key and the same translations, and the vehicle name is already a trilingual
    /// key of its own (``VehicleToken/nameKey``).
    private var caption: String {
        guard !model.state.noDriver else { return "finding_none_caption".localised }
        let countdown = "finding_caption".localisedFormat(model.state.countdown)
        guard let tier = tierName else { return countdown }
        return tier + MageRideSymbols.separator + countdown
    }

    private var tierName: String? {
        guard let type = model.state.ride?.vehicleType else { return nil }
        return VehicleToken.forType(type.toVehicleType())?.nameKey.localised
    }
}
