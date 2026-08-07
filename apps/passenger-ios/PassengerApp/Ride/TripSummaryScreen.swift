import MageRideShared
import SwiftUI

/// SCR-PI-018 — the receipt.
///
/// The cell: `✕ Trip complete`, a centred *Total paid* over the amount and a settlement pill, a short
/// map, a `card` of key/value rows with an emphasised Total, and a tonal *"Rate your driver ★"*. An
/// unsettled ride gets a **Pay now** CTA instead — the cell's own state line.
///
/// **Δ the breakdown is a `DisclosureGroup`**, which is the cell's own clause, and it is **empty of
/// itemisation** because no operation returns the parts of a settled fare — see
/// ``TripSummaryState/hasBreakdown``. The total is what the passenger was charged and is drawn from
/// the ride; the four lines the drawing shows would have to be re-derived from a quote, which is the
/// one thing R-05 says a client must not do.
///
/// **The map carries the two pins and no line.** MAP-08's trip polyline lives on query-svc's
/// `TripDetail` — C099's SCR-PI-023 — and **no Mode C track exists anywhere to serve as one** (C042's
/// finding, restated by C079). A straight line between the pins would be a claim about a route the
/// vehicle did not necessarily take.
@MainActor
struct TripSummaryScreen: View {

    @StateObject private var model: TripSummaryModel

    /// The `✕`. Back to the map, which is where the next ride starts.
    let onClose: () -> Void

    /// *"Pay now"* — SCR-PI-016, for a ride that reached the receipt unsettled.
    let onPay: () -> Void

    /// SCR-PI-019.
    let onRate: () -> Void

    init(
        rideId: String,
        rides: RideRepository,
        ratings: RideRatings,
        onClose: @escaping () -> Void,
        onPay: @escaping () -> Void,
        onRate: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: TripSummaryModel(rideId: rideId, rides: rides, ratings: ratings))
        self.onClose = onClose
        self.onPay = onPay
        self.onRate = onRate
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                header
                map
                breakdown

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                cta
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "summary_title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarLeading) {
                Button(action: onClose) {
                    Image(systemName: "xmark")
                }
                .accessibilityLabel(Text(key: "action_close"))
            }
        }
        // Re-read on every appear rather than once: SCR-PI-019 pops back here, and *"Rate your
        // driver"* has to stop being offered once it has been answered.
        .onAppear { model.load() }
    }

    private var header: some View {
        VStack(spacing: MageRideSpacing.xs) {
            AmountHeadline(captionKey: "summary_total_paid", amountMinor: model.state.totalMinor)
            StatusPill(
                titleKey: model.state.isSettled ? "summary_settled" : "summary_unpaid",
                tone: model.state.isSettled ? .ok : .warning
            )
        }
    }

    private var map: some View {
        MageRideMap(pins: pins, camera: camera)
            .allowsHitTesting(false)
            .frame(height: MageRideControl.receiptMapHeight)
            .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }

    /// The cell's `card` of `.kv` rows, behind the `DisclosureGroup` its own clause asks for.
    private var breakdown: some View {
        DisclosureGroup {
            VStack(spacing: MageRideSpacing.xs) {
                if !model.state.hasBreakdown {
                    MutedRow(messageKey: "summary_breakdown_unavailable")
                }
                KeyValueRow(
                    titleKey: "summary_total",
                    value: model.state.totalMinor.map(MoneyFormat.rupees) ?? MoneyFormat.pending,
                    isTotal: true
                )
            }
            .padding(.top, MageRideSpacing.xs)
        } label: {
            Text(key: "summary_breakdown")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .tint(MageRideColor.primary)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.background,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    @ViewBuilder
    private var cta: some View {
        if !model.state.isSettled {
            Button(action: onPay) { Text(key: "summary_pay_now") }
                .buttonStyle(.mageCta)
        } else if model.state.canRate {
            Button(action: onRate) {
                Label { Text(key: "summary_rate") } icon: { Image(systemName: "star.fill") }
            }
            .buttonStyle(.mageCtaTonal)
        }
    }

    private var pins: [MapPin] {
        guard let ride = model.state.ride else { return [] }
        return [
            MapPin(kind: VehicleLayers.pinPickup, lat: ride.pickup.lat, lng: ride.pickup.lng),
            MapPin(kind: VehicleLayers.pinDropoff, lat: ride.dropoff.lat, lng: ride.dropoff.lng),
        ]
    }

    private var camera: MapCamera {
        guard let dropoff = model.state.ride?.dropoff else { return .colombo }
        return MapCamera(lat: dropoff.lat, lng: dropoff.lng)
    }
}
