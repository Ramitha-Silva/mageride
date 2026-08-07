import MageRideShared
import SwiftUI

/// **SCR-DI-016a/b/c · package delivery** — the three sequential bottom sheets (AL-33, US-20.12/13).
///
/// One screen, three sheets, because the wireframe draws one map with a sheet that changes over it:
/// *review the job* → *collect the parcel* → *hand it over*. Which sheet is up is the ride's own business
/// (`package.picked_up` is the `→ InProgress` move), so nothing here counts steps.
///
/// **It is reached through ``DriverRoute/activeRide(rideId:)``, like every other live job.** A delivery
/// is a ride — R-01 keeps one aggregate and ``PushRouter`` already lands `mageride://package/{id}` and
/// `mageride://ride/{id}` on the same destination — so ``ActiveRideScreen`` swaps to this screen once it
/// reads a `RideKind.package`, in the same way Home swaps its sheet for a Mode A/B vehicle (C088).
///
/// - Parameters:
///   - model: The three sheets' one model. Owned by ``ActiveRideScreen``, which is what decides that this
///     job is a parcel.
///   - captures: SCR-DI-005's seam. Observed here rather than inside the model for the reason C087 set:
///     the coordinator is an `ObservableObject` and a `@Published` result reaches a screen through
///     `onChange`, not through a `Flow` collector.
///   - onFinished: The parcel is handed over, or the job was released. Back to standby.
///   - onCaptureRequested: Opens SCR-DI-005 — the app's one camera, presented over this sheet.
///   - onOpenSos: SCR-DI-032 (C093). The alarm is a screen with a confirmation, not a button that fires.
@MainActor
struct DeliveryScreen: View {

    @ObservedObject var model: DeliveryModel
    @ObservedObject var captures: DocumentCaptureCoordinator

    let onFinished: () -> Void
    let onCaptureRequested: () -> Void
    let onOpenSos: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            // Sheet 2's `banner info` — "Navigate to the pickup · 1.2 km". The wireframe also prints a
            // "4 min", and there is no ETA on this surface to print: AL-19 keeps an arrival time off a
            // tier, and `RideDriver.etaSeconds` is dispatch's estimate of the driver for the PASSENGER.
            // A fabricated minute count on a courier's screen is worse than a distance alone.
            if model.state.sheet == .pickup {
                DashboardBanner(
                    text: navigationHint,
                    accent: MageRideColor.secondary,
                    symbolName: "location.north.line.fill"
                )
            }

            if let errorKey = model.state.errorKey {
                DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                    .onTapGesture(perform: model.consume)
            }

            // MAP-10's 100 m circle, drawn where the driver is being sent: the sender's door until the
            // parcel is aboard, the recipient's afterwards.
            DriverHomeMap(
                position: model.state.position,
                token: nil,
                geofence: model.state.geofence
            )
            // Sheets 1 and 3 are `overlay sheet`s in the wireframe — the map behind them is dimmed,
            // because at those two moments the driver is reading the sheet and not the map. Sheet 2 is
            // the one they navigate on, and it is not dimmed.
            .overlay {
                if model.state.sheet != .pickup {
                    Color.black.opacity(DeliveryScreen.scrimOpacity)
                        .allowsHitTesting(false)
                }
            }

            DeliverySheets(model: model, onCaptureRequested: onCaptureRequested)
        }
        // The navigation bar is ``ActiveRideScreen``'s: this is the same destination, drawn differently.
        .background(MageRideColor.background)
        .task {
            model.start()
            await model.refresh()
        }
        .onDisappear(perform: model.stop)
        // SCR-DI-005 hands the proof photograph back here (P-10). The scanner is a takeover presented
        // over this sheet, so the screen is still alive underneath and collects the result itself.
        .onChange(of: captures.result) { result in
            if let result { model.apply(result) }
        }
        .onChange(of: model.state.isSosRequested) { requested in
            guard requested else { return }
            model.consume()
            onOpenSos()
        }
        .onChange(of: model.state.isFinished) { finished in
            if finished { onFinished() }
        }
    }

    private var navigationHint: String {
        guard let metres = model.state.pickupMetres else {
            // SCR-DI-015's own banner copy, because it is the same sentence — a second key with the same
            // words in three languages is what drifts apart.
            return "ride_navigate_pickup".localised
        }
        return "delivery_navigate_pickup_distance".localisedFormat(MoneyFormat.distance(metres: metres))
    }

    /// D2' §0.2's scrim opacity, the same one SCR-DI-010's offline map wears.
    ///
    /// `Color.black` rather than a role: a scrim is a platform primitive, and darkening `onSurface`
    /// would lighten it in dark mode — the opposite of what a scrim is for.
    private static let scrimOpacity: Double = 0.45
}
