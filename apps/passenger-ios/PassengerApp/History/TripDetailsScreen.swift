import MageRideShared
import SwiftUI

/// SCR-PI-023 — one trip, its route and its receipt.
///
/// The cell: `‹ Trips · Trip details`, a short rounded map with the track on it, a `glist` of
/// Date / Vehicle / Driver / Total, and a `btn-row` of `⬇ Receipt` and `⚑ Report`.
///
/// **The distance shown is the one the fare was computed from** — `TripDetail.distanceKm`, the
/// Kalman-filtered figure (E-04) — and never a sum over the decoded polyline. When the geometry came
/// from the 1/min operational sample rather than full telemetry the contract calls that distance a
/// **lower bound**, and the screen says so rather than presenting a floor as a measurement. The row
/// is drawn because `passenger_android.html`'s cell for the same `KEEP` screen draws it and
/// `apps/passenger-android` shows it; the iOS cell's four-row `glist` omits it, which is a difference
/// between the two drawings rather than between the two apps (C099 handoff).
///
/// **`⬇ Receipt` is drawn and disabled, and that is a contract gap rather than an omission.** No
/// passenger-facing operation on the app surface produces a trip receipt: `public-bff.yaml`'s
/// `GET /public/track/{token}/receipt` is the *web tracking token's* and `fleet-billing.yaml`'s is a
/// fleet invoice's. So the control is drawn where the wireframe draws it, refuses the tap, and says
/// why — the same call C095 made on SCR-PI-004's avatar badge, which draws an upload the platform has
/// no route for. `apps/passenger-android` omits the control entirely; recorded in the C099 handoff.
@MainActor
struct TripDetailsScreen: View {

    @StateObject private var model: TripDetailsModel

    /// `⚑ Report` — SCR-PI-030.
    let onReportIssue: () -> Void

    init(
        tripId: String,
        history: HistoryRepository,
        sessions: PassengerSessions,
        onReportIssue: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: TripDetailsModel(tripId: tripId, history: history, sessions: sessions))
        self.onReportIssue = onReportIssue
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                map
                receipt

                if model.state.isApproximate {
                    // The contract's own caveat, surfaced. A lower bound presented as a measurement
                    // is how a passenger ends up arguing with a receipt that never claimed to be
                    // exact.
                    Text(key: "trip_details_approximate")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }

                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                actions
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "trip_details_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.load() }
    }

    // MARK: -

    /// MAP-08's trip line. A track with fewer than two points draws nothing, which is what a Mode C
    /// ride whose `geometrySource` is `none` actually has.
    private var map: some View {
        MageRideMap(routePolyline: model.state.route, camera: camera)
            .allowsHitTesting(false)
            .frame(height: MageRideControl.tripDetailMapHeight)
            .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }

    /// The cell's `.glist` — four rows and an emphasised total.
    ///
    /// Every row draws the dash rather than disappearing when its value is absent: a receipt with a
    /// missing line reads as a receipt that is still loading, and each of these is missing for a
    /// documented reason (see ``TripDetailsState``).
    private var receipt: some View {
        VStack(spacing: MageRideSpacing.xs) {
            KeyValueRow(titleKey: "trip_details_date", value: model.state.startedAt ?? MageRideSymbols.unknown)
            KeyValueRow(titleKey: "trip_details_vehicle", value: model.state.vehicle ?? MageRideSymbols.unknown)
            KeyValueRow(titleKey: "trip_details_driver", value: model.state.driver ?? MageRideSymbols.unknown)
            KeyValueRow(titleKey: "trip_details_distance", value: model.state.distance ?? MageRideSymbols.unknown)
            KeyValueRow(titleKey: "summary_total", value: model.state.total ?? MoneyFormat.pending, isTotal: true)
        }
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.background,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .redacted(reason: model.state.isLoading ? .placeholder : [])
    }

    /// The cell's `btn-row`, and the one sentence that stops the left-hand button being a mystery.
    private var actions: some View {
        VStack(spacing: MageRideSpacing.xs) {
            HStack(spacing: MageRideSpacing.xs) {
                // Drawn, refused, and explained underneath. See the type's note.
                OutlinedAction(
                    titleKey: "trip_details_receipt",
                    symbolName: "arrow.down.circle",
                    tint: MageRideColor.outline
                ) { }
                    .disabled(true)

                OutlinedAction(titleKey: "trip_details_report", symbolName: "flag.fill", action: onReportIssue)
            }

            Text(key: "trip_details_receipt_unavailable")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// Opens on the start of the track, or on the pickup when there is no track at all.
    private var camera: MapCamera {
        if let first = model.state.route.first {
            return MapCamera(lat: first.lat, lng: first.lng)
        }
        guard let pickup = model.state.trip?.pickup else { return .colombo }
        return MapCamera(lat: pickup.lat, lng: pickup.lng)
    }
}
