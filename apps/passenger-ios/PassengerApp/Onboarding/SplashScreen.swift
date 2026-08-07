import SwiftUI

/// **SCR-PI-001 · splash** — the app mark on the brand orange, and the boot decision behind it.
///
/// Full-bleed `primary` with a white rounded mark, the product name and a spinner, exactly as the
/// wireframe draws it. Its own `Δ iOS` clause is *"`ZStack`+`ProgressView`; `LaunchScreen.storyboard`.
/// KMP auth routes after token check"* — the launch surface is `Info.plist`'s `UILaunchScreen` on
/// `primary` (C094), so this screen continues that colour rather than flashing white between the two.
///
/// Nothing is tappable: this screen exists for as long as ``SplashModel`` needs and not one frame
/// longer, and the shell replaces it the moment it answers — there is nothing to come back to.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when `SWIFT_STRICT_CONCURRENCY` is raised.
@MainActor
struct SplashScreen: View {

    @StateObject private var model: SplashModel

    private let onResolved: (PassengerRoute) -> Void

    init(
        sessions: PassengerSessions,
        profiles: PassengerProfileRepository,
        rides: RideApi,
        preferences: AppPreferences,
        onResolved: @escaping (PassengerRoute) -> Void
    ) {
        _model = StateObject(
            wrappedValue: SplashModel(
                sessions: sessions,
                profiles: profiles,
                rides: rides,
                preferences: preferences
            )
        )
        self.onResolved = onResolved
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.md) {
            // The mark: a white rounded square with the launcher glyph, the wireframe's 84pt.
            RoundedRectangle(cornerRadius: MageRideRadius.card, style: .continuous)
                .fill(MageRideColor.onPrimary)
                .frame(width: MageRideControl.avatarLarge, height: MageRideControl.avatarLarge)
                .overlay {
                    Image(systemName: "map.fill")
                        .font(.system(size: MageRideControl.avatarLarge / 2.4))
                        .foregroundStyle(MageRideColor.primary)
                }

            // The same key `CFBundleDisplayName` carries, not a second copy of it: the wireframe's
            // "MageRide" under the mark is the Home-screen label, and the two must not be able to
            // disagree. Transliterated in si/ta rather than translated — C076's call, kept.
            Text(key: "app_name")
                .mageFont(.headline)
                .foregroundStyle(MageRideColor.onPrimary)

            ProgressView()
                .tint(MageRideColor.onPrimary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.primary)
        .ignoresSafeArea()
        .task { await model.decide() }
        .onChange(of: model.route) { route in
            if let route { onResolved(route) }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(key: "app_name"))
    }
}

// The glyph is an SF Symbol rather than the brand mark: the app-icon slot in `Assets.xcassets` is
// empty and C124 owns the artwork, so drawing the wireframe's "M" here would be a hard-coded Latin
// letter on a screen that ships in three scripts. Swap it for the mark when the icon lands.
