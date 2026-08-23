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

    /// Δ MCS-28 — the first arm in this switch that builds a model directly rather than delegating
    /// to a cluster's own destination view. `.documents` belongs to no screen group, which is why
    /// it was the last placeholder standing.
    @EnvironmentObject private var graph: DriverGraph

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
        //
        // One arm for all four, and a sub-view rather than four inline screens — the same reason
        // clusters 1, 2 and 3 take one arm each above.
        case .jobs, .scheduledRides, .driverLevel, .earnings:
            JobsDestinationView(route: route)

        // ---- C091 · wallet / daily fee -----------------------------------------------
        //
        // One arm for all five, and a sub-view rather than five inline screens — the same reason
        // clusters 1, 2, 3 and 5 take one arm each above.
        case .wallet, .walletTopUp, .walletRequestCredit, .walletTransfer, .walletHistory:
            WalletDestinationView(route: route)

        // ---- C092 · tracker, sharing, profile and history -----------------------------
        //
        // One arm for all four, and a sub-view rather than four inline screens — the same reason
        // clusters 1, 2, 3, 5 and 6 take one arm each above.
        case .trackerPairing, .sharing, .profile, .rideHistory:
            ProfileDestinationView(route: route)

        // ---- C093 · comms, safety, support and the alerts ----------------------------
        //
        // One arm for all four, and a sub-view rather than four inline screens — the same reason
        // every cluster above takes one arm. Two of the four are full-screen takeovers rather than
        // pushed destinations; see ``CommsDestinationView``.
        case .support, .notifications, .voipCall, .sos:
            CommsDestinationView(route: route)

        // ---- menu and what hangs off it ----------------------------------------------
        //
        // `.menu` is SCR-DI-036 and is C088's — see the arm above; it is the Menu tab's root and the
        // whole of AL-31's replacement for the hamburger.
        //
        // Δ MCS-28 — `.documents` was the last placeholder on either platform, and it stood because
        // there was nothing to draw: `VehicleDocument` carried a kind, a status and an expiry and no
        // image. The signed image links exist now.
        case .documents: DocumentsScreen(model: graph.makeDocumentsModel())
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
