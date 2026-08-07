import SwiftUI
import UIKit

/// SCR-PI-006 — the mode and type filter.
///
/// A presented `.sheet` over the map, exactly as the cell draws it: a scrim, a grabber, *"Show on
/// map"*, three mode rows with their A/B/C badges, a `vehicle types` divider, the chip row, and
/// **Apply**. Its `Δ iOS` clause is *"`Toggle` + `Toggle(.button)` chips; `.impact(.light)`"*, and
/// this is all three.
///
/// **Every chip carries its own vehicle glyph tinted with its MAP-03 colour**, which the cell calls
/// out in as many words — *"each type shows its small vehicle icon tinted with the canonical AL-09
/// colour (not a plain dot)"*. The colour comes from the same ``VehicleToken`` the map's
/// `MLNSymbolStyleLayer` is tinted from, so the two cannot drift.
///
/// **Apply closes the sheet and nothing else.** The filter is *"instant"* (the cell again) — every
/// toggle has already redrawn the map underneath — so the CTA is a dismiss, not a commit. Making it
/// a commit would mean a passenger could not see what they were choosing.
struct ModeFilterSheet: View {

    let filter: MapFilter
    let onModeChanged: (ModeToken, Bool) -> Void
    let onTypeChanged: (VehicleToken, Bool) -> Void
    let onDismiss: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            Text(key: "filter_title")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
                .padding(.bottom, MageRideSpacing.xxs)

            GroupedList {
                ForEach(VehicleLabels.modeRows, id: \.self) { mode in
                    ModeRow(
                        mode: mode,
                        isOn: filter.modes.contains(mode),
                        // Key the collection and ask it whether this is the last row, rather than
                        // enumerating: Swift has no key paths into tuples, so `Array(_:enumerated())`
                        // cannot supply a `ForEach` id.
                        showsSeparator: mode != VehicleLabels.modeRows.last,
                        onChange: { onModeChanged(mode, $0) }
                    )
                }
            }

            LabelledDivider(key: "filter_vehicle_types")
                .padding(.top, MageRideSpacing.xxs)

            ChipGrid(types: VehicleLabels.chipTypes, selected: filter.types, onChange: onTypeChanged)

            Button(action: onDismiss) {
                Text(key: "filter_apply")
            }
            .buttonStyle(.mageCta)
            .padding(.top, MageRideSpacing.xs)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }
}

/// One mode row — the badge, the name, and iOS's green switch.
///
/// Drawn here rather than through ``GroupedRow``, and the reason is the badge: that control's
/// leading slot is a 28pt rounded square holding an **SF Symbol**, and the wireframe's `.badge` is a
/// small pill holding a **letter**. A row is cheaper than bending a shared control into a shape its
/// other four call sites do not want.
private struct ModeRow: View {

    let mode: ModeToken
    let isOn: Bool
    let showsSeparator: Bool
    let onChange: (Bool) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: MageRideSpacing.sm) {
                Text(mode.badge)
                    .mageFont(.label)
                    .foregroundStyle(MageRideColor.onStatus)
                    .padding(.horizontal, MageRideSpacing.xs)
                    .padding(.vertical, MageRideSpacing.xxs / 2)
                    .background(mode.color, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))

                Text(key: mode.nameKey)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)

                Spacer(minLength: MageRideSpacing.xs)

                Toggle("", isOn: Binding(get: { isOn }, set: onChange))
                    .labelsHidden()
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
        .accessibilityElement(children: .combine)
    }
}

/// The chip row. It wraps, because eight chips do not fit on one line at any Dynamic Type size.
///
/// A `LazyVGrid` with an `.adaptive` column rather than a hand-written `Layout`: the chips are a
/// fixed, short list, and an adaptive grid is what SwiftUI already ships for exactly this. (The
/// driver app needed a `Layout` because its rows hold labels of wildly different widths.)
private struct ChipGrid: View {

    let types: [VehicleToken]
    let selected: Set<VehicleToken>
    let onChange: (VehicleToken, Bool) -> Void

    var body: some View {
        LazyVGrid(
            columns: [
                GridItem(
                    .adaptive(minimum: MageRideControl.filterChipMinimum),
                    spacing: MageRideSpacing.xs,
                    alignment: .leading
                ),
            ],
            alignment: .leading,
            spacing: MageRideSpacing.xs
        ) {
            ForEach(types, id: \.self) { type in
                TypeChip(type: type, isOn: selected.contains(type)) { onChange(type, $0) }
            }
        }
    }
}

/// One type chip — the wireframe's `.chip` with its `.vico` swatch.
///
/// `Toggle(isOn:).toggleStyle(.button)` is the cell's own `Δ iOS` clause, and it is what gives the
/// chip its selected fill, its `.isSelected` accessibility trait and its announcement for free. A
/// hand-drawn button would have to earn all three.
private struct TypeChip: View {

    let type: VehicleToken
    let isOn: Bool
    let onChange: (Bool) -> Void

    var body: some View {
        Toggle(isOn: Binding(get: { isOn }, set: onChange)) {
            HStack(spacing: MageRideSpacing.xxs + 2) {
                // The swatch IS the marker colour — a disc of the legend tint with the type's own
                // glyph on it, so a chip and the pins it controls are recognisably the same thing.
                type.image
                    .font(.system(size: MageRideControl.chipIcon))
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.chipSwatch, height: MageRideControl.chipSwatch)
                    .background(type.color, in: Circle())

                Text(key: type.nameKey)
                    .mageFont(.bodySmall)
                    .lineLimit(1)
            }
        }
        .toggleStyle(.button)
        .buttonStyle(.bordered)
        .tint(MageRideColor.primary)
        // The cell's own `.impact(.light)`. A chip that redraws every marker underneath it is worth
        // one; the mode switches already have the system's own.
        .onChange(of: isOn) { _ in UIImpactFeedbackGenerator(style: .light).impactOccurred() }
    }
}
