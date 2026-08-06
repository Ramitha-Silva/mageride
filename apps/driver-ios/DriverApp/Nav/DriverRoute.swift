import Foundation

/// Every destination the Driver App has, as one enum.
///
/// **The shell owns the route table; the screen groups own the screens.** C086–C093 each register
/// their views against the cases below rather than inventing destinations, which is what makes a
/// cross-group navigation (`Profile Setup -> Permissions -> Home`, AL-27) a compile-time reference
/// instead of a string two components have to spell the same way.
///
/// ``path`` is the same string as `apps/driver-android/.../nav/DriverRoute.kt`'s. SwiftUI needs no
/// route pattern — a `NavigationStack` pushes values, not URLs — so the property exists for two
/// other reasons and both matter: it is the stable identity a deep link resolves to, and it is what
/// lets the two apps' route tables be **diffed**. The C067 handoff asks for exactly that ("keep both
/// in step — a divergence shows up as two apps that look almost the same"), and
/// `NavigationShellTests` is where the ask is enforced.
///
/// `Hashable` because that is `NavigationPath`'s requirement; the associated `rideId` values are
/// what make two active rides two destinations rather than one.
enum DriverRoute: Hashable {

    // MARK: - C086 · auth / onboarding

    /// SCR-DI-001 — boot + driver-info router. The start destination; it decides where to go.
    case splash

    /// SCR-DI-002 — language + operating city, first run only.
    case languageCity

    /// SCR-DI-003 — +94 phone then SMS OTP. Phone-OTP only, no Sign in with Apple (US-11.5, AL-07).
    case login

    /// SCR-DI-003a — driver profile setup. Precedes Home and needs NO vehicle (AL-27).
    case profileSetup

    /// SCR-DI-007 — runtime permissions, with the Settings deep link on denial.
    case permissions

    // MARK: - C087 · vehicle onboarding

    /// SCR-DI-004…004c — the optional Mode-C-only four-step wizard.
    case vehicleOnboarding

    /// SCR-DI-005 — document capture with the drag-corner crop. Shared by C086 and C087.
    case documentCapture

    /// SCR-DI-006 — the four-document verdict list.
    case vehicleOnboardingStatus

    /// SCR-DI-026 / 026a — My Vehicles, and its no-vehicles empty state.
    case vehicles

    // MARK: - C088 · dashboard / dispatch

    /// SCR-DI-010 — the dashboard. Tab 1, and where a `ride_offer` push lands.
    case home

    /// SCR-DI-013 — the Directional Travel filter (DT-01..DT-08).
    ///
    /// A pushed destination rather than a sheet on Home: the wireframe draws it with a `‹` back
    /// button over a full screen. SCR-DI-014 is the opposite and is deliberately **not** here — a
    /// fifteen-second offer is a takeover the dashboard owns, which is why ``PushRouter`` routes a
    /// `ride_offer` to ``home``.
    case directional

    /// The ride in progress — accepted through to payment.
    ///
    /// `mageride://ride/{id}` and `mageride://package/{id}` both resolve here; see ``PushRouter``.
    case activeRide(rideId: String)

    // MARK: - C090 · jobs / level / earnings

    /// SCR-DI-017 — the Job Board. Tab 2, and **post-intent only** (US-6A.5).
    case jobs

    /// SCR-DI-018 — the driver's own upcoming scheduled rides. US-6A.15's 30-minute reminder
    /// opens it; see ``PushRouter``.
    case scheduledRides

    /// SCR-DI-019 — Driver Level, points and the US-6A.14 stats. Opened by SCR-DI-010's `L3` badge.
    case driverLevel

    /// SCR-DI-020 — the earnings dashboard. Opened by SCR-DI-010's *"Today: 4 trips · Rs 3,180"*.
    case earnings

    // MARK: - C091 · wallet / daily fee

    /// SCR-DI-021 — wallet and daily fee. Tab 3, and `mageride://wallet`.
    case wallet

    /// SCR-DI-022 — top up by card / OnePay wallet / LankaQR, and the bulk-voucher ladder.
    case walletTopUp

    /// SCR-DI-023 — ask another driver for credit **by Driver ID** (US-9.10, AL-34: no QR scan).
    case walletRequestCredit

    /// SCR-DI-024 — the approval inbox and the direct send (US-9.11/9.12, US-9.20/9.21).
    case walletTransfer

    /// SCR-DI-025 — the ledger, filtered, with US-9A.19's statement download.
    case walletHistory

    // MARK: - menu, profile, documents, support

    /// The Menu tab — **the navigation entry point that replaces the hamburger** (AL-31).
    ///
    /// Tab 4. Everything not reachable from the other three hangs off it.
    case menu

    /// Driver documents and their expiry state. `mageride://documents` lands here (E-03).
    case documents

    /// SCR-DI-029 — the driver profile, its emergency contact and log out (C092).
    case profile

    /// SCR-DI-033 — support and the daily-fee refund request (C093).
    case support

    /// SCR-DI-027 — GPS tracker pairing (C092).
    case trackerPairing

    /// SCR-DI-028 — Mode B sharing management (C092).
    case sharing

    /// SCR-DI-030 — ride history, and the rate-passenger sheet on it (C092).
    case rideHistory

    /// SCR-DI-034 — the alerts list (C093). `mageride://` has no host for it; it is menu-reached.
    case notifications

    // MARK: - C093 · the two screens the ride opens

    /// SCR-DI-031 — the in-app VoIP call over CallKit (US-6A.16, P-05, D-24).
    ///
    /// Reached from SCR-DI-015's **Call** once the driver picks *"Free call"* on AL-48's chooser.
    /// *"Normal call"* never comes here: it is a `tel:` dial placed where it was chosen.
    case voipCall(rideId: String)

    /// SCR-DI-032 — the driver SOS (US-12.8, AL-13, D-33).
    ///
    /// **Active trip only**, which is why it carries a ride: `POST /v1/sos` names the trip so the
    /// SMS and the admin live feed say *which* one.
    case sos(rideId: String)
}

extension DriverRoute {

    /// The stable identity of this destination — byte-for-byte the Android route pattern's value.
    var path: String {
        switch self {
        case .splash: return "splash"
        case .languageCity: return "onboarding/lang-city"
        case .login: return "login"
        case .profileSetup: return "profile-setup"
        case .permissions: return "permissions"
        case .vehicleOnboarding: return "vehicle/onboard"
        case .documentCapture: return "document/capture"
        case .vehicleOnboardingStatus: return "vehicle/onboard/status"
        case .vehicles: return "vehicles"
        case .home: return "home"
        case .directional: return "standby/directional"
        case .activeRide(let rideId): return "ride/\(rideId)"
        case .jobs: return "jobs"
        case .scheduledRides: return "jobs/scheduled"
        case .driverLevel: return "driver/level"
        case .earnings: return "earnings"
        case .wallet: return "wallet"
        case .walletTopUp: return "wallet/top-up"
        case .walletRequestCredit: return "wallet/request-credit"
        case .walletTransfer: return "wallet/transfer"
        case .walletHistory: return "wallet/history"
        case .menu: return "menu"
        case .documents: return "documents"
        case .profile: return "profile"
        case .support: return "support"
        case .trackerPairing: return "vehicle/tracker"
        case .sharing: return "sharing"
        case .rideHistory: return "history"
        case .notifications: return "notifications"
        case .voipCall(let rideId): return "call/\(rideId)"
        case .sos(let rideId): return "sos/\(rideId)"
        }
    }

    /// Every destination that carries no argument — what the coverage test walks and what the
    /// placeholder registry is keyed on.
    ///
    /// ``activeRide(rideId:)``, ``voipCall(rideId:)`` and ``sos(rideId:)`` are absent because they
    /// are parameterised; there is no single value to list.
    static let staticRoutes: [DriverRoute] = [
        .splash,
        .languageCity,
        .login,
        .profileSetup,
        .permissions,
        .vehicleOnboarding,
        .documentCapture,
        .vehicleOnboardingStatus,
        .vehicles,
        .home,
        .directional,
        .jobs,
        .scheduledRides,
        .driverLevel,
        .earnings,
        .wallet,
        .walletTopUp,
        .walletRequestCredit,
        .walletTransfer,
        .walletHistory,
        .menu,
        .documents,
        .profile,
        .support,
        .trackerPairing,
        .sharing,
        .rideHistory,
        .notifications,
    ]

    /// Which tab this destination belongs under.
    ///
    /// A `TabView` of `NavigationStack`s has one stack per tab, so pushing a destination means
    /// first knowing which stack — and the answer is a property of the destination rather than of
    /// the caller. Cluster 1 and the two full-screen takeovers answer ``DriverTab/home``: they are
    /// presented over everything (see ``DriverShell``), and Home is where the app returns to.
    var tab: DriverTab {
        switch self {
        case .jobs, .scheduledRides, .driverLevel, .earnings:
            return .jobs
        case .wallet, .walletTopUp, .walletRequestCredit, .walletTransfer, .walletHistory:
            return .wallet
        case .menu, .documents, .profile, .support, .trackerPairing, .sharing, .rideHistory,
             .notifications, .vehicles, .vehicleOnboarding, .vehicleOnboardingStatus:
            return .menu
        default:
            return .home
        }
    }

    /// Whether this destination is drawn over the whole screen, tab bar included.
    ///
    /// Three are: the document camera is a viewfinder, and SCR-DI-031 and SCR-DI-032 are takeovers
    /// with neither a back button nor a tab bar — a driver on an alarm screen must not be one tap
    /// from their wallet. Cluster 1 is full-screen for a different reason (there is no session yet)
    /// and is handled by ``DriverShell`` presenting it in place of the whole `TabView`.
    var isFullScreenTakeover: Bool {
        switch self {
        case .documentCapture, .voipCall, .sos: return true
        default: return false
        }
    }

    /// Whether this destination is reached before there is a signed-in driver.
    ///
    /// The five cluster-1 screens replace the tab bar entirely rather than sitting inside it —
    /// `driver_ios.html` draws no tab bar on any of them.
    var isPreSession: Bool {
        switch self {
        case .splash, .languageCity, .login, .profileSetup, .permissions: return true
        default: return false
        }
    }
}
