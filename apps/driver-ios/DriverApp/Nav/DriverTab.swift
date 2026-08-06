import SwiftUI

/// The four tabs, in the order `driver_ios.html` prints them: `[Home][Jobs][Wallet][≡]`.
///
/// **AL-31: the dashboard has NO top-left hamburger.** ``menu`` is the whole navigation drawer's
/// replacement — a peer of Home rather than a corner affordance — and this enum is the only place
/// the app declares a top-level destination. `NavigationShellTests` asserts there are exactly four
/// and that Menu is one of them, so a later component cannot quietly reintroduce a drawer.
///
/// D2' §C maps Android's `NavigationBar` onto SwiftUI's `TabView`; this is that row.
enum DriverTab: String, CaseIterable, Identifiable {
    case home
    case jobs
    case wallet
    case menu

    var id: String { rawValue }

    /// The tab's root destination.
    var route: DriverRoute {
        switch self {
        case .home: return .home
        case .jobs: return .jobs
        case .wallet: return .wallet
        case .menu: return .menu
        }
    }

    /// The trilingual label key. Same key as `values/strings.xml` — see `Localizable.strings`.
    var labelKey: String {
        switch self {
        case .home: return "nav_home"
        case .jobs: return "nav_jobs"
        case .wallet: return "nav_wallet"
        case .menu: return "nav_menu"
        }
    }

    /// The SF Symbol, D2' §C's "Icons: Material Symbols / SF Symbols".
    ///
    /// `line.3.horizontal` for Menu rather than an ellipsis: it is the glyph the wireframe draws
    /// (`≡`) and, on this platform, the one that reads as "everything else" without implying the
    /// hamburger AL-31 removed — it is a tab, and a tab that looks like a drawer trigger is the
    /// confusion that rule exists to prevent.
    var symbolName: String {
        switch self {
        case .home: return "house.fill"
        case .jobs: return "list.bullet"
        case .wallet: return "creditcard.fill"
        case .menu: return "line.3.horizontal"
        }
    }
}
