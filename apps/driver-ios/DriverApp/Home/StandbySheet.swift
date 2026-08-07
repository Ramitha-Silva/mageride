import MageRideShared
import SwiftUI

/// **SCR-DI-010 · the Mode C standby sheet.**
///
/// The wireframe, top to bottom: the `◉ ONLINE — Mode C` bar with its switch, the *"Live vehicle"* row
/// carrying the reg no selected in SCR-DI-026 (US-9.6), and the row that pairs the `⮕ Directional`
/// chip with today's trips and earnings.
///
/// **No hamburger** (AL-31). Nothing on this sheet opens a drawer; navigation is the tab bar's **Menu**
/// tab, which the shell draws.
///
/// - Parameters:
///   - onOpenVehicles: US-9.6's empty state — *"Add or get assigned a vehicle to go online"*, which
///     routes to My Vehicles and raises SCR-DI-026a when the list is empty.
///   - onOpenEarnings: SCR-DI-020 (C090). The *"Today: 4 trips · Rs 3,180"* line **is** the earnings
///     dashboard's Today figure — `EarningsSummary.netMinor` for `?period=today`, read through the
///     same query-svc endpoint — so tapping it opens the screen it is a summary of.
struct StandbySheet: View {

    let state: HomeState
    let onToggleOnline: (Bool) -> Void
    let onOpenDirectional: () -> Void
    let onOpenVehicles: () -> Void
    let onOpenEarnings: () -> Void

    var body: some View {
        DashboardSheet {
            // SCR-DI-035 (Δ C093). Draws nothing while the handset is online or the backlog is
            // empty — see its own documentation.
            BufferedSamplesCard()

            OnlineToggle(
                label: (state.isOnline ? "home_online" : "home_offline").localised,
                isOnline: state.isOnline,
                isEnabled: state.canGoOnline,
                isBusy: state.isBusy,
                onToggle: onToggleOnline
            )

            if state.needsVehicle {
                noVehicleNotice
            } else {
                liveVehicleRow
            }

            HStack(spacing: MageRideSpacing.xs) {
                directionalChip

                Button(action: onOpenEarnings) {
                    Text(todayStats)
                        .mageFont(.label)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                }
                .buttonStyle(.plain)
            }
        }
    }

    /// *"Live vehicle · Three-wheeler · ABC-1234"* (US-9.6, SCR-DI-026).
    ///
    /// The reg no is the single active publisher's — the vehicle whose id is the MQTT username, so
    /// what this row names is literally what the broker will see at CONNECT.
    @ViewBuilder
    private var liveVehicleRow: some View {
        if let label = state.liveVehicleLabel, let token = state.liveToken {
            HStack(spacing: MageRideSpacing.xs) {
                Text(key: "home_live_vehicle")
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: MageRideSpacing.xs)
                VehicleChip(label: label, token: token)
            }
        }
    }

    /// US-9.6's gate, said out loud. The tap goes to My Vehicles, whose empty state is SCR-DI-026a.
    private var noVehicleNotice: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            Text(key: "home_needs_vehicle")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Button(action: onOpenVehicles) {
                HStack(spacing: 2) {
                    Text(key: "home_add_vehicle")
                        .mageFont(.label)
                    Image(systemName: "chevron.right")
                        .font(.footnote)
                }
                .foregroundStyle(MageRideColor.primary)
            }
            .buttonStyle(.plain)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The `⮕ Directional` chip — SCR-DI-012's *"Directional entry chip showing active filter status"*,
    /// merged into this sheet with the rest of that screen.
    private var directionalChip: some View {
        let filter = state.standing.directional
        let isActive = filter?.active == true

        return Button(action: onOpenDirectional) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: "arrow.turn.up.right")
                    .font(.system(size: MageRideControl.chipIcon))
                Text(isActive ? (filter?.label ?? "home_directional".localised) : "home_directional".localised)
                    .mageFont(.label)
            }
            .foregroundStyle(isActive ? MageRideColor.onPrimaryContainer : MageRideColor.onSurfaceVariant)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xs)
            .background(
                isActive ? MageRideColor.primaryContainer : MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.lg, style: .continuous)
            )
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    /// *"Today: 4 trips · Rs 3,180"*. A placeholder line while the earnings read is still in flight.
    private var todayStats: String {
        guard let earnings = state.standing.earnings else { return "home_today_pending".localised }
        return "home_today_stats".localisedFormat(Int(earnings.trips), MoneyFormat.rupees(earnings.netMinor))
    }
}
