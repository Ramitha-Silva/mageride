import SwiftUI

/// The one place a ``PassengerRoute`` becomes a view.
///
/// **This is the iOS counterpart of `PassengerNavHost.kt`, and it has the same rule**: a screen group
/// replaces the body of its own case and touches nothing else. There is exactly one of these in the
/// app; a second `navigationDestination` for the same type would fork the back stack the way a
/// second `NavHost` does on Android.
///
/// **Every route in this file now draws a real screen** (Δ C102). The shell registered all
/// thirty-two from the day it landed, as labelled placeholders, which is what made a cross-group
/// navigation — SCR-PI-010's Mode B marker opening SCR-PI-024 (AL-23), SCR-PI-015 opening
/// SCR-PI-016 (AL-47) — a compile-time reference during wave 4b rather than a promise. C102 took the
/// last three, so `PlaceholderScreen` and the two `route_placeholder_*` strings are gone with them:
/// re-adding either would be a way to register a route with nothing behind it. `apps/passenger-android`
/// deleted its own `RoutePlaceholder` at C084 for the same reason.
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
        //
        // Five destinations and one draft. SCR-PI-012a (the paste sheet) and the map picker are
        // sheets rather than destinations — the first because the wireframe presents it as one, the
        // second because no SCR-PI id names a map picker at all.
        case .rideBooking, .proxyRider, .packageBooking, .scheduleRide, .confirmPickup:
            BookingDestinationView(route: route)

        // ---- C098 · the ride and its payment -------------------------------------------
        //
        // Six destinations and one forward direction. SCR-PI-015a (the call-type chooser) and the QR
        // scanner are sheets rather than destinations — the first because ``PassengerRoute``'s own
        // note lists it among the overlays, the second because a scan is a string that comes straight
        // back to the model that asked for it.
        case .findingDriver, .activeRide, .paymentMethod, .payFare, .tripSummary, .rateDriver:
            RideDestinationView(route: route)

        // ---- C099 · packages and history ------------------------------------------------
        //
        // Three destinations and two screens for one of them: `.packageTracking` draws SCR-PI-020 or
        // SCR-PI-021 depending on which end of the parcel is holding the phone, because
        // `mageride://package/{rideId}` is the same link for both parties (see the route's own note).
        case .packageTracking, .trips, .tripDetails:
            HistoryDestinationView(route: route)

        // ---- C100 · Mode B ---------------------------------------------------------------
        //
        // Four destinations and two doors onto the first of them: SCR-PI-024 is reached from a Mode B
        // marker with the vehicle id pre-filled (AL-23) and from the Menu tab's *"Private transport"*
        // row with nothing, which is why the route's associated value is optional.
        case .modeBRequest, .subscriptions, .subscriptionPayment, .subscriptionPayments:
            SubscriptionDestinationView(route: route)

        // ---- C101 · addresses, settings and the Menu tab ----------------------------------
        //
        // Four destinations and one of them is a **tab root**: `.menu` is SCR-PI-033, and its row
        // table stays in the shell — see ``PassengerMenuDestination`` — because it is a statement
        // about the route table. SCR-PI-026a (the save-address sheet) and the SOS-contact editor are
        // sheets of their screens rather than destinations, the same call C097 and C098 made about
        // theirs.
        case .savedAddresses, .settings, .editProfile, .menu:
            SettingsDestinationView(route: route)

        // ---- C102 · comms, safety and support ---------------------------------------------
        //
        // Three destinations, and the first two are full-screen takeovers rather than pushed
        // destinations — see ``PassengerRoute/isFullScreenTakeover``. SCR-PI-030a (the raise-ticket
        // sheet) is a sheet of SCR-PI-030 and SCR-PI-031 (D-31's update gate) is the shell's, which
        // is why neither is a case here.
        case .voipCall, .sos, .support:
            CommsDestinationView(route: route)
        }
    }
}

/// The arm a cluster's own `switch` cannot reach, and what it draws if it ever does.
///
/// ``PassengerDestinationView`` routes exactly the cases each sub-view handles, so every
/// `…DestinationView`'s `default:` is unreachable — but `PassengerRoute` has thirty-two cases and
/// Swift wants them all accounted for, so eight files need *something* there.
///
/// **Deliberately not copy.** This is not `PlaceholderScreen`'s successor: that view existed to
/// tell a passenger during wave 4b that a route had no screen yet, and after C102 no route is in
/// that position. A translated *"coming soon"* for a state the app can no longer be in would be
/// three `.strings` entries describing an impossibility — which is exactly what `LocalizationTests`
/// exists to prevent. So this draws the app's own background and nothing else, and trips an
/// assertion in a debug build so a routing mistake fails on the machine that made it rather than on
/// a handset.
struct UnreachableRoute: View {

    let route: PassengerRoute

    init(route: PassengerRoute) {
        self.route = route
        assertionFailure("no destination view claims \(route.path)")
    }

    var body: some View {
        MageRideColor.background
            .ignoresSafeArea()
    }
}
