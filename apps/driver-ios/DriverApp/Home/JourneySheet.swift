import MageRideShared
import SwiftUI

/// **SCR-DI-011 · the Mode A/B home dashboard — Start / End Journey.**
///
/// *"This screen IS the driver's home dashboard whenever the active vehicle is a Mode A bus or a Mode
/// B private vehicle"* — it replaces SCR-DI-010's standby map rather than sitting beside it. It carries
/// **only** Start Journey (green) and End Journey (red), and the **vehicle type and number show below
/// the route card**, which is the layout D2' spells out and the one thing that distinguishes it from an
/// ordinary tracking screen.
///
/// **AL-32 — the dashboard can override the device.** A paired GPS tracker that saw ignition ON has
/// already opened the session, and the banner above says so; the driver may still End it here, and may
/// Start one by hand that the tracker's ignition-OFF will not close. Neither button is ever disabled
/// because of what the device did.
///
/// **No fee** on Mode A (a bus journey is free); Mode B is a monthly subscription rather than a
/// per-trip charge, which is why neither draws SCR-DI-010's daily-fee chip.
struct JourneySheet: View {

    let state: HomeState
    let onStart: () -> Void
    let onEndOrRestart: () -> Void
    let onChooseRoute: (String) -> Void
    let onAutoEndChanged: (Bool) -> Void

    @State private var isRoutePromptVisible = false
    @State private var typedRoute = ""

    var body: some View {
        DashboardSheet {
            routeCard

            HStack(spacing: MageRideSpacing.xs) {
                Text(key: "journey_vehicle")
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                if let label = state.liveVehicleLabel, let token = state.liveToken {
                    VehicleChip(label: label, token: token)
                }
            }

            Toggle(isOn: Binding(get: { state.autoEndAtDestination }, set: onAutoEndChanged)) {
                Text(key: "journey_auto_end_at_destination")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
            .disabled(state.journey.isRunning)

            actions
        }
        // The route chooser behind **Change ›**.
        //
        // A typed route number rather than a picker, because there is no *"routes I drive"* read on
        // `transit.yaml` — `GET /v1/transit/routes/{routeId}` resolves one by id and nothing lists them
        // for a driver. What is typed is resolved against the active GTFS feed for its long name and
        // remembered on this handset, so it is typed once rather than every morning. The same spec gap
        // the C070 handoff records.
        .alert("journey_route_dialog_title".localised, isPresented: $isRoutePromptVisible) {
            TextField("journey_route_hint".localised, text: $typedRoute)
                .keyboardType(.numbersAndPunctuation)
            Button(role: .cancel) { } label: { Text(key: "action_dismiss") }
            Button {
                onChooseRoute(typedRoute.trimmingCharacters(in: .whitespacesAndNewlines))
            } label: {
                Text(key: "action_continue")
            }
            .disabled(typedRoute.nilIfBlank == nil)
        }
    }

    /// The wireframe's route card — the route, the live duration and the distance (US-5.6).
    private var routeCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(key: "journey_route")
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                    Text(state.journey.routeLabel ?? "journey_no_route".localised)
                        .mageFont(.title)
                        .foregroundStyle(MageRideColor.onSurface)
                }
                Spacer(minLength: MageRideSpacing.xs)
                Button {
                    typedRoute = state.journey.route?.routeShortName ?? ""
                    isRoutePromptVisible = true
                } label: {
                    HStack(spacing: 2) {
                        Text(key: "journey_change")
                            .mageFont(.bodySmall)
                        Image(systemName: "chevron.right")
                            .font(.footnote)
                    }
                    .foregroundStyle(MageRideColor.primary)
                }
                .buttonStyle(.plain)
            }

            HStack(spacing: MageRideSpacing.xs) {
                MetricCard(
                    labelKey: "journey_duration",
                    value: MoneyFormat.clock(seconds: state.journeyElapsedSeconds),
                    alignment: .leading
                )
                MetricCard(
                    labelKey: "journey_distance",
                    value: MoneyFormat.distance(metres: state.journeyDistanceM),
                    alignment: .leading
                )
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// The two buttons, and **only** the two.
    ///
    /// A running journey offers End; a stopped one offers Start; an auto-ended one inside US-5.10's
    /// five-minute grace offers **Restart** in End's place, because that is the only state a restart is
    /// legal from — `restartableUntil` is present on an `AUTO_ENDED` session and on no other.
    private var actions: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Button(action: onStart) {
                Text(key: "journey_start")
            }
            .buttonStyle(.mageCtaStatus(MageRideColor.success))
            .disabled(state.isBusy || state.journey.isRunning || !state.canGoOnline)

            Button(action: onEndOrRestart) {
                Text(key: state.journey.isRestartable ? "journey_restart" : "journey_end")
            }
            .buttonStyle(
                .mageCtaStatus(state.journey.isRestartable ? MageRideColor.primary : MageRideColor.error)
            )
            .disabled(state.isBusy || !(state.journey.isRunning || state.journey.isRestartable))
        }
    }
}
