import SwiftUI

/// D2' §0.2's CTA token — the full-width orange bar the wireframes draw at the bottom of almost
/// every screen. "Replaces NY `PrimaryButton`", so it is one shape with one height everywhere.
///
/// A `ButtonStyle` rather than a wrapper view: `Button` already owns the tap target, the
/// accessibility trait, the `.disabled` propagation and the keyboard-navigation focus ring, and a
/// bespoke view would have to re-earn all four. A screen writes
/// `Button(…) { }.buttonStyle(.mageCta)` and the token is applied.
///
/// The loading state is `ProgressView` in place of the label, which is §0.2's own "inline
/// lottie/`ProgressView` loader"; it keeps the button's height so the layout does not jump, and it
/// is the caller's job to also disable the button — a spinner that is still tappable is a double
/// submit waiting to happen.
struct MageRideCtaStyle: ButtonStyle {

    /// Which of §0.2's fills. `tonal` is the wireframe's `.cta.tonal`, `destructive` its `.cta.err`.
    ///
    /// **`status` is Δ C088**, and it is a fill rather than a fourth control. D2' §SCR-DI-011 gives
    /// Start Journey `success` and End Journey `error`, and §SCR-DI-015's table gives *"Start ride"*
    /// `success` and *"Complete ride"* `error` — the wireframe's `.cta.succ` / `.cta.err`. Those are
    /// the **same** bar at the same height, radius and typography with a different colour, so a
    /// separate `StatusCta` view (which is what the Android twin needed, because Compose has no
    /// `ButtonStyle`) would be a second control to keep in step with §0.2's CTA row.
    enum Emphasis {
        case filled
        case tonal
        case destructive
        /// A role colour the *consequence* fixes — `MageRideColor.success` for a Start, `.error`
        /// for an End. The label is always `onStatus`, which meets contrast on either.
        case status(Color)
    }

    var emphasis: Emphasis = .filled
    var isLoading: Bool = false

    func makeBody(configuration: Configuration) -> some View {
        // A nested View rather than the modifier chain inline: `@Environment` does not update
        // inside a `ButtonStyle` — a style is not a View and its properties are read once — so the
        // disabled state has to be read somewhere that has a `body`. This is the standard shape,
        // and getting it wrong produces a CTA that never dims when the form is incomplete.
        Body(configuration: configuration, background: background, foreground: foreground, isLoading: isLoading)
    }

    private struct Body: View {
        let configuration: ButtonStyleConfiguration
        let background: Color
        let foreground: Color
        let isLoading: Bool

        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            ZStack {
                configuration.label
                    .mageFont(.subtitle)
                    .opacity(isLoading ? 0 : 1)

                if isLoading {
                    ProgressView().tint(foreground)
                }
            }
            .frame(maxWidth: .infinity, minHeight: MageRideControl.ctaHeight)
            .foregroundStyle(foreground)
            .background(background, in: RoundedRectangle(cornerRadius: MageRideControl.ctaRadius, style: .continuous))
            // §0.2 pairs the Android ripple with iOS's `.buttonStyle` press state. On this platform
            // that is the standard dim-and-shrink, not a bespoke animation.
            .opacity(configuration.isPressed ? 0.85 : 1)
            .scaleEffect(configuration.isPressed ? 0.99 : 1)
            .opacity(isEnabled ? 1 : 0.4)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
            .contentShape(Rectangle())
        }
    }

    private var background: Color {
        switch emphasis {
        case .filled: return MageRideColor.primary
        case .tonal: return MageRideColor.primaryContainer
        case .destructive: return MageRideColor.error
        case .status(let accent): return accent
        }
    }

    private var foreground: Color {
        switch emphasis {
        case .filled, .destructive: return MageRideColor.onPrimary
        case .tonal: return MageRideColor.onPrimaryContainer
        case .status: return MageRideColor.onStatus
        }
    }
}

extension ButtonStyle where Self == MageRideCtaStyle {

    /// The full-width orange bar. Use this, never a bare `.borderedProminent`.
    static var mageCta: MageRideCtaStyle { MageRideCtaStyle() }

    /// The `primaryContainer` fill — the wireframe's `.cta.tonal`.
    static var mageCtaTonal: MageRideCtaStyle { MageRideCtaStyle(emphasis: .tonal) }

    /// The `error` fill — the wireframe's `.cta.err`, for End Journey and Cancel ride.
    static var mageCtaDestructive: MageRideCtaStyle { MageRideCtaStyle(emphasis: .destructive) }

    /// The bar with its label replaced by a spinner. Disable the button as well.
    static func mageCta(loading: Bool) -> MageRideCtaStyle { MageRideCtaStyle(isLoading: loading) }

    /// The same bar in a role colour the consequence fixes — Δ C088. `MageRideColor.success` is
    /// Start Journey and *"Start ride"*; `.error` is End Journey and *"Complete ride"*.
    static func mageCtaStatus(_ accent: Color, loading: Bool = false) -> MageRideCtaStyle {
        MageRideCtaStyle(emphasis: .status(accent), isLoading: loading)
    }
}
