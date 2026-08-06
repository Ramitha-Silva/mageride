import SwiftUI

/// **SCR-DI-001 · splash** — the app mark on the brand orange, and the boot decision behind it.
///
/// Full-bleed `primary` with a white rounded mark, the product name and a spinner, exactly as the
/// wireframe draws it. Nothing is tappable: this screen exists for as long as ``SplashModel`` needs
/// and not one frame longer, and the shell replaces it the moment it answers — there is nothing to
/// come back to.
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct SplashScreen: View {

    @StateObject private var model: SplashModel

    private let onResolved: (OnboardingDestination) -> Void

    init(
        sessions: DriverSessions,
        profiles: DriverProfileRepository,
        preferences: OnboardingPreferences,
        onResolved: @escaping (OnboardingDestination) -> Void
    ) {
        _model = StateObject(
            wrappedValue: SplashModel(sessions: sessions, profiles: profiles, preferences: preferences)
        )
        self.onResolved = onResolved
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.md) {
            // The mark: a white rounded square with the launcher glyph, the wireframe's 84pt.
            RoundedRectangle(cornerRadius: MageRideRadius.card, style: .continuous)
                .fill(MageRideColor.onPrimary)
                .frame(width: markSize, height: markSize)
                .overlay {
                    Image(systemName: "car.fill")
                        .font(.system(size: markSize / 2.4))
                        .foregroundStyle(MageRideColor.primary)
                }

            // `CFBundleDisplayName`, not a second copy of it: the wireframe's "MageRide Driver"
            // under the mark is the Home-screen label, and the two must not be able to disagree.
            Text(key: "app_name")
                .mageFont(.headline)
                .foregroundStyle(MageRideColor.onPrimary)

            ProgressView()
                .tint(MageRideColor.onPrimary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.primary)
        .ignoresSafeArea()
        .task {
            await model.route()
        }
        .onChange(of: model.destination) { destination in
            if let destination { onResolved(destination) }
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(key: "app_name"))
    }

    /// The wireframe's 84pt mark. A measurement of one control, so not a spacing token.
    ///
    /// The glyph inside it is an SF Symbol rather than the brand mark: the app-icon slot in
    /// `Assets.xcassets` is empty and C103 owns the artwork, so drawing an "M" here would be a
    /// hard-coded letter on a screen in three scripts. Swap it for the mark when the icon lands.
    private let markSize: CGFloat = 84
}
