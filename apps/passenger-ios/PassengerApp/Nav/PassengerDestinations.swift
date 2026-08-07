import SwiftUI

/// The one place a ``PassengerRoute`` becomes a view.
///
/// **This is the iOS counterpart of `PassengerNavHost.kt`, and it has the same rule**: a screen group
/// replaces the body of its own case and touches nothing else. There is exactly one of these in the
/// app; a second `navigationDestination` for the same type would fork the back stack the way a
/// second `NavHost` does on Android.
///
/// Every destination is registered from the day the shell lands, as a placeholder. That is what
/// makes a cross-group navigation — SCR-PI-010's Mode B marker opening SCR-PI-024 (AL-23),
/// SCR-PI-015 opening SCR-PI-016 (AL-47) — a compile-time reference during wave 4b rather than a
/// promise: a screen group can navigate to a screen that has not been written, and what it gets is a
/// labelled placeholder rather than a dead end.
///
/// **Add a sub-view, not an inline screen.** `body` is an implicit `@ViewBuilder`, so every arm of
/// this switch becomes another layer of `_ConditionalContent` around every other arm's type —
/// thirty-two placeholders is a type the compiler already works to infer, and several real screens
/// inlined here would be orders of magnitude worse. A group that owns more than one destination
/// should give them one arm and one `…DestinationView`, exactly as C086–C093 did on the driver side.
struct PassengerDestinationView: View {

    let route: PassengerRoute

    var body: some View {
        switch route {
        // ---- C095 · auth / onboarding -------------------------------------------------
        //
        // One arm for all five, and a sub-view rather than five inline screens — see
        // ``OnboardingDestinationView`` for why, and do the same for your own cluster.
        case .splash, .onboarding, .login, .profileSetup, .locationPermission:
            OnboardingDestinationView(route: route)

        // ---- C096 · the live map and search --------------------------------------------
        //
        // SCR-PI-010 is the app's home and the one screen that owns the R-06 subscription; SCR-PI-006
        // (the mode filter), SCR-PI-007 (the Mode A popup) and SCR-PI-032 (the offline state) are
        // states and sheets of it rather than destinations of their own.
        case .liveMap, .searchLocation:
            HomeDestinationView(route: route)

        // ---- C097 · booking ------------------------------------------------------------
        case .rideBooking: placeholder("SCR-PI-009 ride booking")
        case .proxyRider: placeholder("SCR-PI-010b proxy rider")
        case .packageBooking: placeholder("SCR-PI-012 package booking")
        case .scheduleRide: placeholder("SCR-PI-013 schedule ride")
        case .confirmPickup: placeholder("SCR-PI-011 confirm pickup")

        // ---- C098 · the ride and its payment -------------------------------------------
        case .findingDriver: placeholder("SCR-PI-014 finding driver")
        case .activeRide: placeholder("SCR-PI-015 active ride")
        case .paymentMethod: placeholder("SCR-PI-016 payment method")
        case .payFare: placeholder("SCR-PI-017 pay fare")
        case .tripSummary: placeholder("SCR-PI-018 trip summary")
        case .rateDriver: placeholder("SCR-PI-019 rate driver")

        // ---- C099 · packages and history ------------------------------------------------
        case .packageTracking: placeholder("SCR-PI-020/021 package tracking")
        case .trips: placeholder("SCR-PI-022 trips")
        case .tripDetails: placeholder("SCR-PI-023 trip details")

        // ---- C100 · Mode B ---------------------------------------------------------------
        case .modeBRequest: placeholder("SCR-PI-024 private transport")
        case .subscriptions: placeholder("SCR-PI-025 my subscriptions")
        case .subscriptionPayment: placeholder("SCR-PI-025a subscription payment")
        case .subscriptionPayments: placeholder("SCR-PI-025b subscription payments")

        // ---- C101 · addresses, settings and the Menu tab ----------------------------------
        //
        // `.menu` is SCR-PI-033 and is C101's (`build/screen_coverage.md` is the authority). Its row
        // table is already written — see ``PassengerMenuDestination`` — so what that component adds
        // is the `List`, the `NavigationLink`s and the identity card above them.
        case .savedAddresses: placeholder("SCR-PI-026 saved addresses")
        case .settings: placeholder("SCR-PI-027 profile & settings")
        case .editProfile: placeholder("SCR-PI-027b edit profile")
        case .menu: placeholder("SCR-PI-033 menu")

        // ---- C102 · comms, safety and support ---------------------------------------------
        //
        // The first two are full-screen takeovers rather than pushed destinations — see
        // ``PassengerRoute/isFullScreenTakeover``.
        case .voipCall: placeholder("SCR-PI-028 VoIP call")
        case .sos: placeholder("SCR-PI-029 SOS")
        case .support: placeholder("SCR-PI-030 support")
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
/// `specs/wireframes/passenger_ios.html` and D2' §A are indexed by.
struct PlaceholderScreen: View {

    let screen: String
    let route: PassengerRoute

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
