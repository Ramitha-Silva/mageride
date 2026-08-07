import SwiftUI

/// `driver_ios.html`'s dashboard shapes — the inline sheet, the big online bar, the banner strip, the
/// countdown ring, the solid badge and the vehicle chip.
///
/// The same six shapes appear across SCR-DI-010, 011, 013, 014, 015 and again on C089's delivery
/// sheets, so they belong beside ``FormControls`` and ``VehicleControls`` rather than inside whichever
/// screen drew one first. Their Android twins are `ui/component/HomeControls.kt`'s `DashboardSheet` /
/// `OnlineToggle` / `DashboardBanner` / `CountdownRing` / `SolidBadge` / `VehicleChip`; the tones, the
/// radii and the heights are the same on both.

// MARK: - The inline sheet

/// The wireframe's `.sheet` — a rounded, elevated panel pinned to the bottom of a map screen.
///
/// **Deliberately not a `.presentationDetents` sheet.** The wireframe's sheet is always visible and
/// never dismissible: the map behind it is context, not a screen the driver can get back to by
/// swiping this away. A presented sheet would also take the tab bar with it, and SCR-DI-010 draws
/// both at once.
///
/// The grabber is drawn rather than left to the system for the same reason — a `.sheet` draws its own
/// and this is not one.
struct DashboardSheet<Content: View>: View {

    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Capsule()
                .fill(MageRideColor.outline)
                .frame(width: MageRideControl.grabberWidth, height: MageRideControl.grabberHeight)
                .accessibilityHidden(true)

            content
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .padding(.top, MageRideSpacing.xs)
        .padding(.bottom, MageRideSpacing.sm)
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background, in: TopRoundedRectangle(radius: MageRideRadius.card))
        .mageElevated()
    }
}

/// A rectangle rounded at the **top two corners only** — the wireframe's `.sheet` outline.
///
/// SwiftUI's own `UnevenRoundedRectangle` does exactly this and is iOS 17; this app's deployment
/// target is **16.0** (C085), so the shape is drawn here. `UIBezierPath(roundedRect:byRoundingCorners:)`
/// rather than a hand-built `Path`, because it is the same corner geometry UIKit gives every other
/// sheet on the platform.
struct TopRoundedRectangle: Shape {

    let radius: CGFloat

    func path(in rect: CGRect) -> Path {
        Path(
            UIBezierPath(
                roundedRect: rect,
                byRoundingCorners: [.topLeft, .topRight],
                cornerRadii: CGSize(width: radius, height: radius)
            ).cgPath
        )
    }
}

// MARK: - The go-online bar

/// SCR-DI-010's **◉ ONLINE — Mode C** bar (US-6A.1).
///
/// A 64pt bar rather than a `Toggle` in a row: it is the single most-tapped control in the app and
/// the wireframe gives it the whole width. The fill animates between `success` and the neutral
/// surface, which is D2' §SCR-DI-010's *"toggle color transition"* — the state is legible without
/// reading the label.
///
/// **`isEnabled` is US-9.6's gate**, not a styling choice: with no eligible vehicle the bar is inert
/// and the caller draws the *"Add or get assigned a vehicle to go online"* prompt underneath it.
struct OnlineToggle: View {

    let label: String
    let isOnline: Bool
    let isEnabled: Bool
    let isBusy: Bool
    let onToggle: (Bool) -> Void

    var body: some View {
        Button { onToggle(!isOnline) } label: {
            HStack(spacing: MageRideSpacing.xs) {
                Text(label)
                    .mageFont(.subtitle)
                    .frame(maxWidth: .infinity, alignment: .leading)

                if isBusy {
                    ProgressView().tint(content)
                } else {
                    // The wireframe's `.toggle` reads as the platform's switch, and on iOS that is
                    // the system green — §0.2's own note that a genuine HIG system colour stays the
                    // platform's. It is decorative here: the whole bar is the control, so the switch
                    // must not be a second tap target or VoiceOver announces two.
                    Toggle("", isOn: .constant(isOnline))
                        .labelsHidden()
                        .allowsHitTesting(false)
                }
            }
            .foregroundStyle(content)
            .padding(.horizontal, MageRideSpacing.md)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.bigToggle)
            .background(container, in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled || isBusy)
        .animation(.easeOut(duration: 0.2), value: isOnline)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(label))
        .accessibilityAddTraits(isOnline ? [.isButton, .isSelected] : .isButton)
    }

    private var container: Color {
        isEnabled && isOnline ? MageRideColor.success : MageRideColor.surfaceVariant
    }

    private var content: Color {
        isEnabled && isOnline ? MageRideColor.onStatus : MageRideColor.onSurfaceVariant
    }
}

// MARK: - The banner strip

/// The wireframe's `.banner` — a full-bleed strip under the navigation bar in one of four tones.
///
/// The daily-fee chip (`succ`), the GPS-ignition notice (`info`), the low-balance nudge (`warn`) and
/// the failure strip (`err`) are all this control, which is what keeps them the same height and the
/// same padding on four different screens.
///
/// Distinct from ``OfflineBanner``, which is the shell's and is positioned over the whole app.
struct DashboardBanner: View {

    let text: String
    let accent: Color
    var symbolName: String?

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            if let symbolName {
                Image(systemName: symbolName)
                    .font(.caption)
                    .foregroundStyle(accent)
            }
            Text(text)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurface)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .padding(.vertical, MageRideSpacing.xs)
        .frame(maxWidth: .infinity)
        .background(accent.opacity(Self.tint))
        .accessibilityElement(children: .combine)
    }

    /// How much of the accent a banner keeps. Light enough to read `onSurface` text over.
    private static let tint: Double = 0.14
}

// MARK: - The countdown

/// SCR-DI-014's fifteen-second ring.
///
/// - Parameters:
///   - progress: `1.0` at the moment of the offer down to `0.0` at the deadline — `RideOffer`'s own
///     `progress(now:)`, derived from the **deadline** rather than from when this device saw the
///     push. A push that took two seconds to arrive shows thirteen seconds of ring.
///   - seconds: What the middle reads.
///   - isUrgent: The last five seconds, which D2' colours red and the iOS cell pulses.
///   - labelColour: What the middle is drawn in. SCR-DI-014's takeover is off the semantic scheme —
///     see ``MageRideOfferColor`` — so the number's colour is the caller's rather than a role this
///     control picks and gets wrong on a `#15171B` background.
struct CountdownRing: View {

    let progress: Double
    let seconds: Int
    let isUrgent: Bool
    var labelColour: Color = MageRideColor.onSurface

    var body: some View {
        ZStack {
            Circle()
                .stroke(MageRideColor.surfaceVariant, lineWidth: MageRideControl.countdownStroke)
            Circle()
                .trim(from: 0, to: max(0, min(progress, 1)))
                .stroke(
                    isUrgent ? MageRideColor.error : MageRideColor.primary,
                    style: StrokeStyle(lineWidth: MageRideControl.countdownStroke, lineCap: .round)
                )
                // Zero is at three o'clock on a `trim`; a countdown starts at the top.
                .rotationEffect(.degrees(-90))

            Text(String(seconds))
                .mageFont(.headline)
                .foregroundStyle(labelColour)
        }
        .frame(width: MageRideControl.countdownRing, height: MageRideControl.countdownRing)
        // The cell's own `Δ iOS` clause: "ring last 5s pulse". A scale rather than an opacity, so it
        // still reads for a driver who has turned Reduce Transparency on.
        .scaleEffect(isUrgent ? Self.pulse : 1)
        .animation(
            isUrgent ? .easeInOut(duration: 0.5).repeatForever(autoreverses: true) : .default,
            value: isUrgent
        )
        .animation(.linear(duration: 0.25), value: progress)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("offer_countdown".localisedFormat(seconds)))
    }

    private static let pulse: CGFloat = 1.06
}

// MARK: - Badges and chips

/// The solid badges SCR-DI-014 stacks above the fare — *"Third-party booking"* (P-05),
/// *"Package · M"* (US-20.3) and *"Directional"* (DT-08) — and SCR-DI-010's `L3` level badge.
///
/// Solid rather than tinted, like ``ModeCBadge``: each is an identity the driver has to read in a
/// glance at a phone in a windscreen mount, not a status they can go and look up.
struct SolidBadge: View {

    let label: String
    let accent: Color

    var body: some View {
        Text(label)
            .mageFont(.label)
            .foregroundStyle(MageRideColor.onStatus)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs)
            .background(accent, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))
    }
}

/// The wireframe's `chip sm` with a coloured `cdot` — *"Three-wheeler · ABC-1234"* (US-9.6).
///
/// The dot is MAP-03's per-type colour, so the chip on the dashboard is the colour that vehicle is on
/// the map. Same table as My Vehicles' row (``VehicleToken/forVehicleType(_:)``), on purpose.
struct VehicleChip: View {

    let label: String
    let token: VehicleToken

    var body: some View {
        HStack(spacing: MageRideSpacing.xxs) {
            VehicleTypeDot(token: token)
            Text(label)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .padding(.horizontal, MageRideSpacing.xs)
        .padding(.vertical, MageRideSpacing.xs)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.lg, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }
}

/// The wireframe's centred `card fill` metric — a caption over a value, in a filled rounded box.
///
/// SCR-DI-013 draws two of them side by side (*"Uses left · 1 of 2"*, *"Max · 2h"*) and SCR-DI-011's
/// route card draws the same pair left-aligned, which is what ``alignment`` is for.
struct MetricCard: View {

    let labelKey: String
    let value: String
    var alignment: HorizontalAlignment = .center

    var body: some View {
        VStack(alignment: alignment, spacing: 1) {
            Text(key: labelKey)
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(value)
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .frame(maxWidth: .infinity, alignment: alignment == .center ? .center : .leading)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }
}
