import SwiftUI

/// `passenger_ios.html`'s grouped-list and carousel primitives, as views.
///
/// The wireframe draws the same shapes on every cluster-1 screen — an uppercase section label, a
/// white rounded group of rows, a selectable language row, and the paged carousel with its dots —
/// and D2' §C maps them onto `List`/`Form`/`Section`. They are hand-built here rather than taken
/// from `List` for one reason that is visible in the frames: **SCR-PI-002's Get Started is pinned to
/// the bottom under a spacer**, which is a fence this component was given, and a `List` owns its own
/// scrolling, its own insets and its own background. C096–C102 draw the same shapes; take them from
/// here rather than redrawing one.
///
/// The same file exists in `apps/driver-ios/DriverApp/UI/` and the two are near-identical, which is
/// the C094 handoff's gap (b) — the shared SwiftUI package that is deferred until both apps build.

// MARK: - Section label

/// The wireframe's `.t-label` — a caption above a group.
///
/// SCR-PI-002 draws it **centred** (`text-align:center`) over the language rows where the driver
/// wireframe left-aligns it, which is why the alignment is a parameter rather than a constant.
struct SectionLabel: View {

    let key: String
    var alignment: HorizontalAlignment = .leading

    var body: some View {
        HStack(spacing: MageRideSpacing.xxs) {
            if alignment != .leading { Spacer(minLength: 0) }
            Text(key: key)
                .mageFont(.label)
                .textCase(.uppercase)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            if alignment != .trailing { Spacer(minLength: 0) }
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
/// **`label` and `secondary` are values, not keys.** What this row is used for on SCR-PI-002 is a
/// language endonym, which is data rather than copy: `සිංහල` is the same string in all three
/// locales, so putting it in the three `.strings` files would be three identical values —
/// `LocalizationTests` reads that (correctly) as a translation nobody did. See ``LanguageDisplay``.
///
/// **The selected row is drawn as the wireframe draws it** — `border:1.5px solid var(--primary)`
/// over a tinted fill — rather than with the driver app's `✓`/`○` glyph pair. The two wireframes
/// genuinely differ here: `driver_ios.html` puts a checkmark at the trailing edge and
/// `passenger_ios.html` outlines the whole row and centres its label.
struct SelectionRow: View {

    let label: String
    var secondary: String?
    let isSelected: Bool
    let onSelect: () -> Void

    var body: some View {
        Button(action: onSelect) {
            HStack(spacing: MageRideSpacing.xxs) {
                Text(label)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                if let secondary {
                    Text(verbatim: "·")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.outlineVariant)
                    Text(secondary)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
            }
            .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
            .background(
                isSelected ? MageRideColor.primaryContainer : MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(
                        isSelected ? MageRideColor.primary : .clear,
                        lineWidth: MageRideControl.selectedBorder
                    )
            }
            .contentShape(Rectangle())
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
/// A tinted panel with an icon rather than artwork, because that is what it is: a placeholder for
/// an illustration content-svc *references* and this build does not ship. See ``FeatureSlides``.
///
/// **`caption` is a value, not a key** (Δ C095). SCR-PI-002's carousel is served by
/// `GET /v1/content/onboarding/passenger`, so the caption is whatever `illustrationRef` the server
/// sent — naming the asset under the panel is more use to whoever wires the real illustrations than
/// a blank box. The bundled fallback passes a resolved string through the same parameter.
struct IllustrationPanel: View {

    let symbolName: String
    let caption: String
    var height: CGFloat = 96

    var body: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            Image(systemName: symbolName)
                .font(.title)
                .foregroundStyle(MageRideColor.primary)
            Text(caption)
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
