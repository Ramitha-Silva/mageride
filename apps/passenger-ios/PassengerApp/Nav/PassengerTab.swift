import SwiftUI

/// The four tabs, in the order `passenger_ios.html` prints them:
/// `[􀙊 Map][􀋲 Trips][􀌤 Support][􀍡 Menu]`.
///
/// **Δ Section C — the passenger app has a navigation drawer on Android and a Menu TAB here**, and
/// that is the wireframe's own decision rather than a simplification. `passenger_android.html`'s
/// SCR-PA-033 is a Material 3 modal drawer opened from a `≡` in every app bar, with the map still
/// visible behind a scrim; `passenger_ios.html`'s SCR-PI-033 draws a **selected Menu tab** over a
/// large-title `List`, and its own `Δ iOS` clause is *"`List` with `NavigationLink` rows"*. So:
///
/// - ``menu`` carries a route here and carries `null` on Android.
/// - No screen in this app has a `≡`, and there is no `LocalDrawerControl` counterpart.
/// - Nothing hosts a drawer, so there is nothing for a screen to open.
///
/// The Android app's *reason* for the drawer still holds on its own platform — AL-31 is a rule about
/// the *driver* dashboard, not a ban on hamburgers — and this is not a repeal of it. It is the same
/// call the driver app made for SCR-DI-036, arrived at from the other side.
///
/// D2' §C maps Android's `NavigationBar` onto SwiftUI's `TabView`; this is that row.
/// `NavigationShellTests` asserts there are exactly four and that Menu is one of them.
enum PassengerTab: String, CaseIterable, Identifiable {
    case map
    case trips
    case support
    case menu

    var id: String { rawValue }

    /// The tab's root destination. Unlike the Android enum's, none of these is `nil`.
    var route: PassengerRoute {
        switch self {
        case .map: return .liveMap
        case .trips: return .trips
        case .support: return .support
        case .menu: return .menu
        }
    }

    /// The trilingual label key. Same key as `values/strings.xml` — see `Localizable.strings`.
    var labelKey: String {
        switch self {
        case .map: return "nav_map"
        case .trips: return "nav_trips"
        case .support: return "nav_support"
        case .menu: return "nav_menu"
        }
    }

    /// The SF Symbol, D2' §C's "Icons: Material Symbols / SF Symbols".
    ///
    /// The four the wireframe draws, as their SF Symbol names: `􀙊` is `map.fill`, `􀋲` is
    /// `list.bullet`, `􀌤` is `bubble.left.and.bubble.right.fill` and `􀍡` is `ellipsis`. `ellipsis`
    /// rather than `line.3.horizontal` for Menu — the driver app uses the latter because AL-31 makes
    /// its Menu tab the *replacement* for a hamburger and the glyph has to say so; here the drawer
    /// exists on the other platform and this tab is simply "everything else".
    var symbolName: String {
        switch self {
        case .map: return "map.fill"
        case .trips: return "list.bullet"
        case .support: return "bubble.left.and.bubble.right.fill"
        case .menu: return "ellipsis"
        }
    }
}
