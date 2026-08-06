import SwiftUI

/// `driver_ios.html`'s grouped-list and carousel primitives, as views.
///
/// The wireframe draws the same four shapes on every cluster-1 screen — an uppercase section label,
/// a white rounded group of rows, a radio row, and the paged carousel with its dots — and D2' §C
/// maps them onto `List`/`Form`/`Section`. They are hand-built here rather than taken from `List`
/// for one reason that is visible in the frames: cluster 1's screens are **one scrolling column**
/// with a CTA pinned under a spacer, and a `List` owns its own scrolling, its own insets and its own
/// background. C087–C093 draw the same shapes; take them from here rather than redrawing one.

// MARK: - Section label

/// The wireframe's `.t-label` — an uppercase caption above a group.
///
/// The city label's trailing "· from config.operating_cities" is deliberately not rendered: it is
/// the wireframe telling a reader where the list comes from, not copy for a driver. The Android
/// screen does not render it either.
struct SectionLabel: View {

    let key: String

    var body: some View {
        HStack(spacing: MageRideSpacing.xxs) {
            Text(key: key)
                .mageFont(.label)
                .textCase(.uppercase)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Spacer(minLength: 0)
        }
        .accessibilityAddTraits(.isHeader)
    }
}

// MARK: - Grouped list

/// The wireframe's `.glist` — a white rounded group whose rows are separated by hairlines.
///
/// The separators are drawn by the group rather than by the row so that the last one is absent
/// without every row having to know whether it is last.
struct GroupedList<Content: View>: View {

    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 0) {
            content
        }
        .background(MageRideColor.background, in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }
}

/// One `.glist .gr` row: a leading glyph, a label, and whatever the row puts on the right.
struct GroupedRow<Trailing: View>: View {

    let titleKey: String
    var subtitleKey: String?
    var symbolName: String?
    var symbolTint: Color = MageRideColor.primary
    var showsSeparator: Bool = true
    @ViewBuilder let trailing: Trailing

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: MageRideSpacing.sm) {
                if let symbolName {
                    Image(systemName: symbolName)
                        .font(.body)
                        .foregroundStyle(MageRideColor.onPrimary)
                        .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
                        .background(symbolTint, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))
                }

                VStack(alignment: .leading, spacing: 1) {
                    Text(key: titleKey)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                    if let subtitleKey {
                        Text(key: subtitleKey)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }
                }

                Spacer(minLength: MageRideSpacing.xs)
                trailing
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(minHeight: MageRideControl.minimumTapTarget)

            if showsSeparator {
                Rectangle()
                    .fill(MageRideColor.surfaceVariant)
                    .frame(height: MageRideControl.hairline)
                    .padding(.leading, MageRideSpacing.sm)
            }
        }
    }
}

// MARK: - Selection row

/// A radio row — the wireframe's `✓` / `○` pair, as one tappable row.
///
/// **`label` and `secondary` are values, not keys.** The two things this row is used for on
/// SCR-DI-002 are a language endonym and a city name, and both are data rather than copy: an
/// endonym is the same string in all three locales (see ``LanguageDisplay``) and a city's Sinhala
/// name comes out of `config.operating_cities`, not out of a strings file.
struct SelectionRow: View {

    let label: String
    var secondary: String?
    let isSelected: Bool
    var showsSeparator: Bool = true
    let onSelect: () -> Void

    var body: some View {
        Button(action: onSelect) {
            VStack(spacing: 0) {
                HStack(spacing: MageRideSpacing.xs) {
                    Text(label)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                    if let secondary {
                        Text(secondary)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }
                    Spacer(minLength: MageRideSpacing.xs)
                    Image(systemName: isSelected ? "checkmark" : "circle")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(isSelected ? MageRideColor.primary : MageRideColor.outlineVariant)
                }
                .padding(.horizontal, MageRideSpacing.sm)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())

                if showsSeparator {
                    Rectangle()
                        .fill(MageRideColor.surfaceVariant)
                        .frame(height: MageRideControl.hairline)
                        .padding(.leading, MageRideSpacing.sm)
                }
            }
        }
        .buttonStyle(.plain)
        // One row, one announcement: VoiceOver reads "සිංහල, Sinhala, selected" rather than three
        // elements the reader has to assemble (US-19.1/19.2, D2' §C).
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

// MARK: - Carousel

/// The wireframe's `.illus` panel — where a slide's artwork goes until there is any.
///
/// Dashed rather than filled, because that is what it is: a placeholder for an illustration
/// content-svc references and this build does not ship. See ``FeatureSlides``.
struct IllustrationPanel: View {

    let symbolName: String
    let captionKey: String
    var height: CGFloat = 96

    var body: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            Image(systemName: symbolName)
                .font(.title)
                .foregroundStyle(MageRideColor.primary)
            Text(key: captionKey)
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        .frame(maxWidth: .infinity, minHeight: height)
        .background(MageRideColor.surfaceVariant, in: RoundedRectangle(cornerRadius: MageRideRadius.lg, style: .continuous))
        .accessibilityHidden(true)
    }
}

/// The wireframe's `.dots` — a dot per page, the current one drawn as a pill.
///
/// Drawn rather than left to `.tabViewStyle(.page)`'s own indicator: the wireframe's active dot is
/// an 18pt `primary` pill and the system's is a circle in a colour a `TabView` decides.
struct PageDots: View {

    let count: Int
    let current: Int

    var body: some View {
        HStack(spacing: MageRideSpacing.xxs + 2) {
            ForEach(0..<count, id: \.self) { index in
                Capsule()
                    .fill(index == current ? MageRideColor.primary : MageRideColor.outline)
                    .frame(width: index == current ? 18 : 7, height: 7)
            }
        }
        .animation(.easeOut(duration: 0.15), value: current)
        .accessibilityHidden(true)
    }
}
