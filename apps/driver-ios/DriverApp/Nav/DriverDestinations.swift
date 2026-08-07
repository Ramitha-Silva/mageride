import SwiftUI

/// The one place a ``DriverRoute`` becomes a view.
///
/// **This is the iOS counterpart of `DriverNavHost.kt`, and it has the same rule**: a screen group
/// replaces the body of its own case and touches nothing else. There is exactly one of these in the
/// app; a second `navigationDestination` for the same type would fork the back stack the way a
/// second `NavHost` does on Android.
///
/// Every destination is registered from the day the shell lands, as a placeholder. That is what
/// makes AL-27's `Profile Setup -> Permissions -> Home` a compile-time reference during wave 4b
/// rather than a promise: a screen group can navigate to a screen that has not been written, and
/// what it gets is a labelled placeholder rather than a dead end.
struct DriverDestinationView: View {

    let route: DriverRoute

    var body: some View {
        switch route {
        // ---- C086 · auth / onboarding ------------------------------------------------
        //
        // One arm for all five, and a sub-view rather than five inline screens. `body` is an
        // implicit `@ViewBuilder`, so every arm of this switch becomes another layer of
        // `_ConditionalContent` around every other arm's type — thirty placeholders is a type the
        // compiler already works to infer, and five real screens inlined here would be several
        // orders of magnitude worse. A screen group that owns more than one destination should do
        // the same.
        case .splash, .languageCity, .login, .profileSetup, .permissions:
            OnboardingDestinationView(route: route)

        // ---- C087 · vehicle onboarding -----------------------------------------------
        //
        // One arm for all four, and a sub-view rather than four inline screens — the same reason
        // cluster 1 takes one arm above.
        case .vehicleOnboarding, .documentCapture, .vehicleOnboardingStatus, .vehicles:
            VehicleDestinationView(route: route)

        // ---- C088 · dashboard / dispatch ---------------------------------------------
        //
        // One arm for all four, and a sub-view rather than four inline screens — the same reason
        // clusters 1 and 2 take one arm each above. SCR-DI-014 is deliberately absent: a
        // fifteen-second offer is a takeover Home presents, not a destination.
        case .home, .directional, .activeRide, .menu:
            HomeDestinationView(route: route)

        // ---- C090 · jobs / level / earnings ------------------------------------------
        case .jobs: placeholder("SCR-DI-017 · job board")
        case .scheduledRides: placeholder("SCR-DI-018 · scheduled rides")
        case .driverLevel: placeholder("SCR-DI-019 · driver level")
        case .earnings: placeholder("SCR-DI-020 · earnings")

        // ---- C091 · wallet / daily fee -----------------------------------------------
        case .wallet: placeholder("SCR-DI-021 · wallet & fee")
        case .walletTopUp: placeholder("SCR-DI-022 · top up")
        case .walletRequestCredit: placeholder("SCR-DI-023 · request credit")
        case .walletTransfer: placeholder("SCR-DI-024 · credit transfer")
        case .walletHistory: placeholder("SCR-DI-025 · payment history")

        // ---- menu and what hangs off it ----------------------------------------------
        //
        // `.menu` is SCR-DI-036 and is C088's — see the arm above; it is the Menu tab's root and the
        // whole of AL-31's replacement for the hamburger.
        case .documents: placeholder("driver documents")
        case .profile: placeholder("SCR-DI-029 · driver profile")
        case .support: placeholder("SCR-DI-033 · support")
        case .trackerPairing: placeholder("SCR-DI-027 · tracker pairing")
        case .sharing: placeholder("SCR-DI-028 · sharing management")
        case .rideHistory: placeholder("SCR-DI-030 · ride history")
        case .notifications: placeholder("SCR-DI-034 · alerts")

        // ---- C093 · the two takeovers ------------------------------------------------
        case .voipCall(let rideId): placeholder("SCR-DI-031 · call on ride \(rideId)")
        case .sos(let rideId): placeholder("SCR-DI-032 · SOS on ride \(rideId)")
        }
    }

    private func placeholder(_ screen: String) -> some View {
        PlaceholderScreen(screen: screen, route: route)
    }
}

/// What a registered-but-unwritten destination draws.
///
/// Trilingual, because it renders on a real device during wave 4b exactly like anything else. It
/// names the screen id rather than the component, because the screen id is what
/// `specs/wireframes/driver_ios.html` and D2' §B are indexed by.
struct PlaceholderScreen: View {

    let screen: String
    let route: DriverRoute

    var body: some View {
        VStack(spacing: MageRideSpacing.sm) {
            Image(systemName: "square.dashed")
                .font(.largeTitle)
                .foregroundStyle(MageRideColor.outlineVariant)
            Text("route_placeholder_title", bundle: MageRideColor.bundle)
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
            Text(
                String(
                    format: NSLocalizedString(
                        "route_placeholder_body",
                        bundle: MageRideColor.bundle,
                        comment: "the screen a route will get"
                    ),
                    screen
                )
            )
            .mageFont(.bodySmall)
            .foregroundStyle(MageRideColor.onSurfaceVariant)
            .multilineTextAlignment(.center)
        }
        .padding(MageRideSpacing.lg)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(route.path)
        .navigationBarTitleDisplayMode(.inline)
    }
}
