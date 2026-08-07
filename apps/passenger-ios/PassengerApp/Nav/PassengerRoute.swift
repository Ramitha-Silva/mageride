import Foundation

/// Every destination the Passenger App has, as one enum.
///
/// **The shell owns the route table; the screen groups own the screens.** C095–C102 each register
/// their views against the cases below rather than inventing destinations, which is what makes a
/// cross-group navigation (`Live map -> Mode B request`, AL-23; `Ride -> Pay fare`, AL-47) a
/// compile-time reference instead of a string two components have to spell the same way.
///
/// ``path`` is the same string as `apps/passenger-android/.../nav/PassengerRoute.kt`'s. SwiftUI
/// needs no route pattern — a `NavigationStack` pushes values, not URLs — so the property exists for
/// two other reasons and both matter: it is the stable identity a deep link resolves to, and it is
/// what lets the two apps' route tables be **diffed**. `NavigationShellTests` types the Kotlin table
/// out and compares, which is what turns "keep the two in step" into a build failure.
///
/// **What is deliberately NOT here.** The wireframe's sheets, popups and overlays are not
/// destinations, and registering one would put a back-stack entry behind something the passenger
/// dismisses with a swipe: SCR-PI-006 (the mode filter), SCR-PI-007 (the Mode A marker popup),
/// SCR-PI-012a (paste-link), SCR-PI-015a (the call-type chooser), SCR-PI-026a (save-address),
/// SCR-PI-030a (raise-ticket), SCR-PI-031 (the D-31 update alert, which the shell hosts) and
/// SCR-PI-032 (the offline banner, likewise). Each belongs to the screen that opens it.
///
/// `Hashable` because that is `NavigationPath`'s requirement; the associated ride and subscription
/// ids are what make two rides two destinations rather than one.
enum PassengerRoute: Hashable {

    // MARK: - C095 · auth / onboarding

    /// SCR-PI-001 — boot + session router. The start destination; it decides where to go.
    case splash

    /// SCR-PI-002 — the three-slide carousel and the language boxes, first run only (AL-26/AL-36).
    case onboarding

    /// SCR-PI-003 — +94 phone then SMS OTP. Phone-OTP only, no Sign in with Apple (AL-07).
    case login

    /// SCR-PI-004 — first profile: name and an optional photo (US-1.5).
    case profileSetup

    /// SCR-PI-005 — the location-permission rationale, with the Settings deep link on denial.
    case locationPermission

    // MARK: - C096 · the live map and search

    /// SCR-PI-010 — the live map. Tab 1 and the app's home.
    ///
    /// This is the one screen that owns the R-06 subscription: 19 res-7 cells around the passenger,
    /// joined through the shell's ``PassengerLiveMap``.
    case liveMap

    /// SCR-PI-008 — destination search. **Geo only** — a route number returns places (AL-17).
    case searchLocation

    // MARK: - C097 · booking

    /// SCR-PI-009 — the multimodal results and the booking itself (AL-18, AL-19).
    case rideBooking

    /// SCR-PI-010b — who the ride is for, when it is not the booker (P-01, US-8.19).
    case proxyRider

    /// SCR-PI-012 — a parcel instead of a person (US-20.1/20.2, P-06).
    case packageBooking

    /// SCR-PI-013 — a ride in the future. Destination is mandatory (AL-36).
    case scheduleRide

    /// SCR-PI-011 — the **rider's** side of a proxy pickup (P-02, P-13).
    ///
    /// Reached from a `location_request` push, which is a **silent data message carrying no deep
    /// link at all** — `{kind, requestId, bookerName, ttl}` and nothing else. ``PushRouter``
    /// therefore builds this destination from `data.requestId`; see its documentation.
    case confirmPickup(requestId: String)

    // MARK: - C098 · the ride and its payment

    /// SCR-PI-014 — matching, with the two-minute no-driver timeout (US-6A.11).
    ///
    /// Parameterised even though it precedes assignment: the ride exists from `POST /v1/rides` on,
    /// and an app killed during the search has to come back to *this* ride rather than to the
    /// booking form.
    case findingDriver(rideId: String)

    /// SCR-PI-015 — the ride in progress, Accepted through to Completed.
    ///
    /// `mageride://ride/{id}` resolves here — see ``PushRouter``. A **package** ride does not:
    /// R-01 keeps one aggregate, but this side has two distinct screens for it (SCR-PI-020/021), so
    /// `mageride://package/{id}` resolves to ``packageTracking(rideId:)`` instead.
    case activeRide(rideId: String)

    /// SCR-PI-016 — Cash / Wallet / Driver QR. No surcharge on any rail (AL-57).
    case paymentMethod(rideId: String)

    /// SCR-PI-017 — **the passenger scans the driver's QR**; the app renders none (AL-22, AL-47).
    case payFare(rideId: String)

    /// SCR-PI-018 — the receipt, and the "Pay now" CTA while payment is pending.
    case tripSummary(rideId: String)

    /// SCR-PI-019 — stars, reason chips and an optional comment (US-18.1).
    case rateDriver(rideId: String)

    // MARK: - C099 · packages and history

    /// SCR-PI-020 **and** SCR-PI-021 — package tracking, sender or recipient.
    ///
    /// **One destination for two screens, because the link is the same one for both.**
    /// notification-svc mints `mageride://package/{rideId}` and sends it to the sender on
    /// `package_delivered` and to the recipient on `package_picked_up`; which of the two screens a
    /// given handset should draw is a fact about *the ride* (`passengerId` versus `recipientPhone`),
    /// not about the URI. C099 reads the ride and picks.
    case packageTracking(rideId: String)

    /// SCR-PI-022 — Past / Scheduled / Packages. Tab 2.
    case trips

    /// SCR-PI-023 — one trip, its route and its receipt.
    case tripDetails(tripId: String)

    // MARK: - C100 · Mode B

    /// SCR-PI-024 — ask a private vehicle's owner for access (US-4.6, AL-23, AL-25).
    ///
    /// **The vehicle id is optional, and that is the whole of AL-23.** Opened from a Mode B marker
    /// it arrives pre-filled; opened from the Menu tab's *"Private transport"* row there is no
    /// marker to read one from and the passenger types it. On Android this is an optional
    /// navigation-compose query argument; here it is simply an optional associated value, which is
    /// one of the places SwiftUI's value-based navigation is straightforwardly better.
    case modeBRequest(vehicleId: String?)

    /// SCR-PI-025 — the passenger's own Mode B subscriptions, one card per vehicle.
    case subscriptions

    /// SCR-PI-025a — pay a subscription. `payTo` is the **owner's** account, never MageRide (AL-59).
    case subscriptionPayment(subscriptionId: String)

    /// SCR-PI-025b — that subscription's own statement (US-NEW.payments h).
    case subscriptionPayments(subscriptionId: String)

    // MARK: - C101 · addresses and settings

    /// SCR-PI-026 — Home / Work / labelled addresses on an OSM pin (AL-14, US-22.1/22.2).
    case savedAddresses

    /// SCR-PI-027 — profile, language, notifications, default payment method.
    case settings

    /// SCR-PI-027b — name, photo, SOS contacts. **No language selector** (AL-26).
    case editProfile

    // MARK: - C102 · comms, safety, support, and the Menu tab

    /// SCR-PI-028 — the in-app VoIP call (US-6A.16, D-24).
    ///
    /// Reached from SCR-PI-015a's *"Free call"*. *"Normal call"* never comes here: AL-48 removed
    /// masking, so it is a direct dial to the driver's real number placed where it was chosen.
    case voipCall(rideId: String)

    /// SCR-PI-029 — the passenger SOS (US-12.1, D-33).
    ///
    /// Parameterised by the ride even though `safety.yaml`'s `POST /v1/sos` marks `rideId` optional
    /// — the wireframe's only door to this screen is SCR-PI-015's `⛨ SOS` button, and the screen's
    /// own copy is *"Sending GPS + trip to emergency contacts"*.
    case sos(rideId: String)

    /// SCR-PI-030 — FAQ, ticket list and thread. Tab 3.
    case support

    /// SCR-PI-033 — the Menu. **Tab 4, and iOS has no counterpart on the Android side.**
    ///
    /// This is the one route in the table with no `PassengerRoute.kt` twin, and it is a Section C
    /// delta the wireframe states rather than a divergence: `passenger_ios.html`'s SCR-PI-033 cell
    /// draws a **selected Menu tab** over a large-title `List`, and its own `Δ iOS` clause is
    /// *"`List` with `NavigationLink` rows"*. The Android screen is a `ModalNavigationDrawer` over
    /// whatever was on screen, which is why `PassengerTab.MenuTab` carries no route there.
    ///
    /// Nothing in this app hosts a drawer, and no screen has a `≡` in its toolbar. See
    /// ``PassengerTab/menu`` and ``MenuScreen``.
    case menu
}

extension PassengerRoute {

    /// The stable identity of this destination — the Android route pattern's value, for every route
    /// that has one.
    var path: String {
        switch self {
        case .splash: return "splash"
        case .onboarding: return "onboarding"
        case .login: return "login"
        case .profileSetup: return "profile-setup"
        case .locationPermission: return "permissions/location"
        case .liveMap: return "map"
        case .searchLocation: return "search"
        case .rideBooking: return "booking"
        case .proxyRider: return "booking/proxy-rider"
        case .packageBooking: return "booking/package"
        case .scheduleRide: return "booking/schedule"
        case .confirmPickup(let requestId): return "pickup-confirm/\(requestId)"
        case .findingDriver(let rideId): return "ride/\(rideId)/finding"
        case .activeRide(let rideId): return "ride/\(rideId)"
        case .paymentMethod(let rideId): return "ride/\(rideId)/payment-method"
        case .payFare(let rideId): return "ride/\(rideId)/pay"
        case .tripSummary(let rideId): return "ride/\(rideId)/summary"
        case .rateDriver(let rideId): return "ride/\(rideId)/rate"
        case .packageTracking(let rideId): return "package/\(rideId)"
        case .trips: return "trips"
        case .tripDetails(let tripId): return "trips/\(tripId)"
        case .modeBRequest(let vehicleId):
            // The Android pattern is `private-transport?vehicleId={vehicleId}` and an instance with
            // no id is the bare `private-transport`. Reproduced exactly, because this string is what
            // the two tables are compared on.
            guard let vehicleId, !vehicleId.isEmpty else { return "private-transport" }
            return "private-transport?vehicleId=\(vehicleId)"
        case .subscriptions: return "subscriptions"
        case .subscriptionPayment(let id): return "subscriptions/\(id)/pay"
        case .subscriptionPayments(let id): return "subscriptions/\(id)/payments"
        case .savedAddresses: return "addresses"
        case .settings: return "settings"
        case .editProfile: return "settings/profile"
        case .voipCall(let rideId): return "call/\(rideId)"
        case .sos(let rideId): return "sos/\(rideId)"
        case .support: return "support"
        case .menu: return "menu"
        }
    }

    /// Every destination that carries no argument — what the coverage test walks and what the
    /// placeholder registry is keyed on.
    ///
    /// The parameterised cases are absent because there is no single value to list;
    /// `NavigationShellTests` compares their *shapes* against `PassengerRoute.kt`'s `Patterns`.
    static let staticRoutes: [PassengerRoute] = [
        .splash,
        .onboarding,
        .login,
        .profileSetup,
        .locationPermission,
        .liveMap,
        .searchLocation,
        .rideBooking,
        .proxyRider,
        .packageBooking,
        .scheduleRide,
        .trips,
        .subscriptions,
        .savedAddresses,
        .settings,
        .editProfile,
        .support,
        .menu,
    ]

    /// Which tab this destination belongs under.
    ///
    /// A `TabView` of `NavigationStack`s has one stack per tab, so pushing a destination means
    /// first knowing which stack — and the answer is a property of the destination rather than of
    /// the caller. Cluster 1 and the two full-screen takeovers answer ``PassengerTab/map``: they are
    /// presented over everything (see ``PassengerShell``), and the map is where the app returns to.
    var tab: PassengerTab {
        switch self {
        case .trips, .tripDetails, .packageTracking:
            return .trips
        case .support:
            return .support
        case .menu, .modeBRequest, .subscriptions, .subscriptionPayment, .subscriptionPayments,
             .savedAddresses, .settings, .editProfile:
            return .menu
        default:
            return .map
        }
    }

    /// Whether this destination is drawn over the whole screen, tab bar included.
    ///
    /// Two are, and both for the same reason the driver app gives: SCR-PI-028 and SCR-PI-029 are
    /// takeovers with neither a back button nor a tab bar — a passenger on an alarm screen must not
    /// be one tap from their trip history. `passenger_ios.html`'s SCR-PI-029 cell draws no tab bar.
    ///
    /// Cluster 1 is full-screen for a different reason (there is no session yet) and is handled by
    /// ``PassengerShell`` presenting it in place of the whole `TabView`.
    var isFullScreenTakeover: Bool {
        switch self {
        case .voipCall, .sos: return true
        default: return false
        }
    }

    /// Whether this destination is reached before there is a signed-in passenger.
    ///
    /// The five cluster-1 screens replace the tab bar entirely rather than sitting inside it —
    /// `passenger_ios.html` draws no tab bar on any of them.
    var isPreSession: Bool {
        switch self {
        case .splash, .onboarding, .login, .profileSetup, .locationPermission: return true
        default: return false
        }
    }
}
