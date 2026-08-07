import SwiftUI

/// SCR-PI-033's rows, as a table.
///
/// The wireframe's `States:` line is the source and it is exhaustive: *"Routes to: **Private
/// transport → SCR-PI-024**, **My subscriptions → SCR-PI-025**, **Saved addresses → SCR-PI-026**,
/// **Profile & settings → SCR-PI-027** (plus Help & support)"*. Five destinations in two groups,
/// which is what the cell draws — a `glist` of four, then a second `glist` of one.
///
/// **The table is the shell's and the screen is C101's.** `build/screen_coverage.md` gives SCR-PI-033
/// to C101; what lives here is the row → route mapping, because it is a statement about the *route
/// table* and belongs beside it. `NavigationShellTests` pins the count and the order, so a later
/// screen group cannot quietly add a sixth row or point one at the nearest existing screen. C101
/// renders these as `NavigationLink`s inside a `List` — the cell's own `Δ iOS` clause — and adds the
/// identity card above them.
///
/// *Help & support* is the same destination as tab 3, and that is the wireframe's own choice rather
/// than an oversight: a passenger looking for help finds it in both places.
///
/// ``PassengerRoute/modeBRequest(vehicleId:)`` is entered here with **no vehicle id**: from the menu
/// there is no marker to read one from and the passenger types it (AL-23).
///
/// **Log out is deliberately not a row.** The Android drawer has one because the drawer is the only
/// place it can be; here SCR-PI-027 is a full screen with room for it, and `AuthSessionManager`'s
/// `RouteToLogin` has exactly one subscriber (``PassengerShellModel``) whichever door is used.
enum PassengerMenuDestination: String, CaseIterable, Identifiable {

    /// SCR-PI-024 — 🚐 Private transport.
    case privateTransport

    /// SCR-PI-025 — 🔑 My subscriptions.
    case subscriptions

    /// SCR-PI-026 — ★ Saved addresses.
    case savedAddresses

    /// SCR-PI-027 — ⚙ Profile & settings.
    case settings

    /// SCR-PI-030 — 💬 Help & support, in the second group.
    case support

    var id: String { rawValue }

    var route: PassengerRoute {
        switch self {
        case .privateTransport: return .modeBRequest(vehicleId: nil)
        case .subscriptions: return .subscriptions
        case .savedAddresses: return .savedAddresses
        case .settings: return .settings
        case .support: return .support
        }
    }

    /// The trilingual label key. Same key as `values/strings.xml`.
    var labelKey: String {
        switch self {
        case .privateTransport: return "menu_private_transport"
        case .subscriptions: return "menu_subscriptions"
        case .savedAddresses: return "menu_saved_addresses"
        case .settings: return "menu_settings"
        case .support: return "menu_support"
        }
    }

    /// The SF Symbol for the row's leading glyph.
    ///
    /// The wireframe draws emoji in a tinted square (`🚐 🔑 ★ ⚙ 💬`); these are the SF Symbols that
    /// say the same thing, which is what D2' §C's icon row asks for on this platform.
    var symbolName: String {
        switch self {
        case .privateTransport: return "bus.doubledecker.fill"
        case .subscriptions: return "key.fill"
        case .savedAddresses: return "star.fill"
        case .settings: return "gearshape.fill"
        case .support: return "bubble.left.and.bubble.right.fill"
        }
    }

    /// The glyph's tint — the wireframe's own per-row background colour.
    var tint: Color {
        switch self {
        case .privateTransport: return MageRideVehicleColor.privateHire
        case .subscriptions: return MageRideColor.primary
        case .savedAddresses: return MageRideColor.secondary
        case .settings: return MageRideColor.outlineVariant
        case .support: return MageRideColor.success
        }
    }

    /// The rows in the first `glist`.
    static let primary: [PassengerMenuDestination] = [.privateTransport, .subscriptions, .savedAddresses, .settings]

    /// The rows in the second, below the gap.
    static let secondary: [PassengerMenuDestination] = [.support]
}
