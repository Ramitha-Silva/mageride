import SwiftUI
import UIKit

/// `passenger_ios.html`'s cluster-2 shapes — the sheet grabber, the search bar, the chips, the
/// metric tile, the map FAB, the outlined action and the notice capsule.
///
/// Same rule as ``FormControls`` and ``OnboardingControls``: every measurement comes from
/// ``MageRideSpacing`` / ``MageRideRadius`` / ``MageRideControl``, and a later screen group takes a
/// shape from here rather than redrawing one. C097's booking screens want the chip and the tile;
/// C101's want the outlined action.

// MARK: - Sheets

/// The wireframe's `.grabber`, drawn.
///
/// A presented `.sheet` gets the system's through `.presentationDragIndicator(.visible)`;
/// SCR-PI-010's bottom panel is not one, so it draws its own.
struct SheetGrabber: View {

    var body: some View {
        Capsule()
            .fill(MageRideColor.outline)
            .frame(width: MageRideControl.grabberWidth, height: MageRideControl.grabberHeight)
            .accessibilityHidden(true)
    }
}

/// A rectangle rounded at the **top two corners only** — the wireframe's `.sheet` outline.
///
/// SwiftUI's own `UnevenRoundedRectangle` does exactly this and is iOS 17; this app's deployment
/// target is **16.0**, so the shape is drawn here. `UIBezierPath(roundedRect:byRoundingCorners:)`
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

// MARK: - The search entry

/// SCR-PI-010's *"🔍 Where to?"* bar (`.searchbar`).
///
/// **A button, not a field.** The typing happens on SCR-PI-008, which is a destination with its own
/// back stack and its own predictions list; a field here would be a second search UI one tap away
/// from the first.
struct SearchBarButton: View {

    let titleKey: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.xs) {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: MageRideControl.rowIcon * 0.8))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Text(key: titleKey)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.searchBar)
            .background(
                MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Chips

/// The wireframe's `.chip` — a pill with a leading SF Symbol.
///
/// `label` is a **value**, not a key: what it carries on SCR-PI-010 is a saved address's own label,
/// which a passenger typed in their own language. The `＋ Add` chip passes a resolved string through
/// the same parameter.
struct PlaceChip: View {

    let label: String
    let symbolName: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.xxs + 2) {
                Image(systemName: symbolName)
                    .font(.system(size: MageRideControl.chipIcon))
                Text(label)
                    .mageFont(.bodySmall)
                    .lineLimit(1)
            }
            .foregroundStyle(MageRideColor.onSurface)
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(minHeight: MageRideControl.minimumTapTarget * 0.7)
            .background(MageRideColor.surfaceVariant, in: Capsule())
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Tiles and notices

/// One of SCR-PI-007's two `.card.fill` tiles — a caption over a value.
struct MetricTile: View {

    let titleKey: String
    let value: String

    var body: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            Text(key: titleKey)
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(value)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity)
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .accessibilityElement(children: .combine)
    }
}

/// A rounded caption floating over the map — SCR-PI-032's *"Reconnecting…"* and US-7.14's
/// *"nothing nearby"*.
///
/// A capsule on the map's own surface rather than a banner: SCR-PI-032's banner is the **shell's**
/// (`.safeAreaInset`), and a second full-width bar under it would push the map down twice.
struct MapNotice: View {

    let messageKey: String

    var body: some View {
        Text(key: messageKey)
            .mageFont(.bodySmall)
            .foregroundStyle(MageRideColor.onSurfaceVariant)
            .multilineTextAlignment(.center)
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.vertical, MageRideSpacing.xs)
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .mageElevated()
            .padding(MageRideSpacing.sm)
    }
}

// MARK: - Buttons

/// The wireframe's `.fab` — a circular control on the map's own surface.
///
/// ``MapRecentreButton`` is the `⊕` one and belongs to ``MageRideMap`` because only that view holds
/// the `MLNMapView` it animates; this is the shape for every other one a screen adds over it.
struct MapOverlayButton: View {

    let symbolName: String
    let accessibilityKey: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbolName)
                .font(.system(size: MageRideControl.rowIcon, weight: .semibold))
                .foregroundStyle(MageRideColor.primary)
                .frame(width: MageRideControl.mapControl, height: MageRideControl.mapControl)
                .background(MageRideColor.background, in: Circle())
                .mageElevated()
        }
        .accessibilityLabel(Text(key: accessibilityKey))
    }
}

/// The wireframe's `.btn-out` — an outlined, `primary`-labelled action with an optional glyph.
struct OutlinedAction: View {

    let titleKey: String
    var symbolName: String?
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.xxs) {
                if let symbolName {
                    Image(systemName: symbolName)
                        .font(.system(size: MageRideControl.chipIcon))
                }
                Text(key: titleKey)
                    .mageFont(.bodySmall)
            }
            .foregroundStyle(MageRideColor.primary)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.outlinedAction)
            .background(
                MageRideColor.background,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.outline, lineWidth: MageRideControl.hairline * 2)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
