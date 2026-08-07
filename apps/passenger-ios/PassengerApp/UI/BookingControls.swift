import SwiftUI

/// `passenger_ios.html`'s cluster-3 shapes — the status pill, the solid badge, the info banner, the
/// tier card and the dotted route field.
///
/// Same rule as ``FormControls``, ``OnboardingControls`` and ``MapControls``: every measurement is a
/// token, and a later screen group takes a shape from here rather than redrawing one. C098's
/// SCR-PI-014…019 want the pill and the banner; C099's history cards want the pill.

// MARK: - Pills and badges

/// The wireframe's `.pill-status` — a small rounded label whose colour carries the meaning.
///
/// Five tones because the CSS declares five, and each is a *state* rather than a decoration: `ok` is
/// a direct route, `mut` is a transfer, `warn` is a countdown, `err` is a refusal, `info` is a
/// neutral fact.
struct StatusPill: View {

    enum Tone {
        case ok
        case warning
        case error
        case info
        case muted
    }

    let titleKey: String
    var tone: Tone = .muted

    var body: some View {
        Text(key: titleKey)
            .mageFont(.label)
            .foregroundStyle(foreground)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs / 2)
            .background(background, in: Capsule())
    }

    private var foreground: Color {
        switch tone {
        case .ok: return MageRideColor.success
        case .warning: return MageRideColor.warning
        case .error: return MageRideColor.error
        case .info: return MageRideColor.secondary
        case .muted: return MageRideColor.onSurfaceVariant
        }
    }

    /// The CSS tints each pill's own colour; on this platform the same effect is the foreground at a
    /// low opacity, which also survives Increase Contrast and a dark appearance without a second
    /// asset per tone.
    private var background: Color {
        tone == .muted ? MageRideColor.surfaceVariant : foreground.opacity(0.14)
    }
}

/// The wireframe's `.badge` — a solid, uppercase tag. `PUBLIC` on a GTFS row, `A`/`B`/`C` on a mode.
///
/// The label is a **value**, not a key, when it is a proper noun or a letter; a caller with copy
/// passes a resolved string through the same parameter.
struct SolidBadge: View {

    let text: String
    var color: Color = MageRideColor.primary

    var body: some View {
        Text(text)
            .mageFont(.label)
            .textCase(.uppercase)
            .foregroundStyle(MageRideColor.onStatus)
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs / 2)
            .background(color, in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous))
    }
}

// MARK: - Banners

/// The wireframe's inline `.banner` — a tinted strip inside the body, not the shell's own.
///
/// SCR-PI-032's connection banner is `PassengerShell`'s `.safeAreaInset`; this is the other kind:
/// SCR-PI-009's walk hint, SCR-PI-011's expiry warning and SCR-PI-013's reminder note, each of which
/// belongs to the content it sits in and scrolls with it.
struct InfoBanner: View {

    let messageKey: String
    var tone: StatusPill.Tone = .info
    var symbolName: String?

    var body: some View {
        HStack(alignment: .top, spacing: MageRideSpacing.xs) {
            if let symbolName {
                Image(systemName: symbolName)
                    .font(.system(size: MageRideControl.chipIcon))
                    .foregroundStyle(accent)
            }
            Text(key: messageKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurface)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .padding(.vertical, MageRideSpacing.xs)
        .background(accent.opacity(0.12), in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }

    private var accent: Color {
        switch tone {
        case .ok: return MageRideColor.success
        case .warning: return MageRideColor.warning
        case .error: return MageRideColor.error
        case .info, .muted: return MageRideColor.secondary
        }
    }
}

/// The same shape with a value in it — SCR-PI-009's *"Walk 250 m to Pamankada halt"*.
///
/// A second type rather than a `String` parameter on ``InfoBanner`` for ``CountdownLink``'s reason:
/// the sentence is a *format* with positional specifiers, and `LocalizationTests` asserts they
/// survive into Sinhala and Tamil. A caller that interpolated them itself would defeat that.
struct FormattedBanner: View {

    let messageKey: String
    let arguments: [CVarArg]
    var tone: StatusPill.Tone = .info
    var symbolName: String?

    var body: some View {
        HStack(alignment: .top, spacing: MageRideSpacing.xs) {
            if let symbolName {
                Image(systemName: symbolName)
                    .font(.system(size: MageRideControl.chipIcon))
                    .foregroundStyle(MageRideColor.secondary)
            }
            Text(messageKey.localisedFormat(arguments: arguments))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurface)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .padding(.vertical, MageRideSpacing.xs)
        .background(
            MageRideColor.secondary.opacity(0.12),
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}

// MARK: - Rows

/// The wireframe's `.tier` — a rounded row with a coloured vehicle square, two lines and a trailing
/// slot, drawn with a `primary` ring when it is the chosen one.
///
/// One container for both of SCR-PI-009's lists, because the cell draws them identically: a GTFS
/// option and a Mode C tier differ in what goes in the trailing slot, not in the shape.
struct TierCard<Trailing: View>: View {

    let symbolName: String
    let symbolColor: Color
    let title: String
    let subtitle: String?
    let isSelected: Bool
    let action: () -> Void
    @ViewBuilder let trailing: Trailing

    var body: some View {
        Button(action: action) {
            HStack(spacing: MageRideSpacing.sm) {
                Image(systemName: symbolName)
                    .font(.system(size: MageRideControl.rowIcon))
                    .foregroundStyle(MageRideColor.onStatus)
                    .frame(width: MageRideControl.tierIcon, height: MageRideControl.tierIcon)
                    .background(
                        symbolColor,
                        in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                    )

                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .mageFont(.subtitle)
                        .foregroundStyle(MageRideColor.onSurface)
                        .multilineTextAlignment(.leading)
                    if let subtitle, !subtitle.isEmpty {
                        Text(subtitle)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .multilineTextAlignment(.leading)
                    }
                }

                Spacer(minLength: MageRideSpacing.xs)
                trailing
            }
            .padding(MageRideSpacing.sm)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                isSelected ? MageRideColor.background : MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(
                        isSelected ? MageRideColor.primary : .clear,
                        lineWidth: MageRideControl.selectionRing
                    )
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

/// The wireframe's `.field` with a leading `.cdot` — SCR-PI-013's pickup and destination rows.
///
/// Tappable when it has an action: the destination row opens the picker, and the pickup row is
/// editable for the reason SCR-PI-013's own state line gives (*"pickup defaults to current location
/// but is editable"*).
struct RouteFieldRow: View {

    let dotColor: Color
    let value: String
    var isPlaceholder = false
    var isHighlighted = false
    var symbolName: String?
    var action: (() -> Void)?

    var body: some View {
        Button {
            action?()
        } label: {
            HStack(spacing: MageRideSpacing.xs) {
                Circle()
                    .fill(dotColor)
                    .frame(width: MageRideControl.routeDot, height: MageRideControl.routeDot)
                Text(value)
                    .mageFont(.body)
                    .foregroundStyle(isPlaceholder ? MageRideColor.onSurfaceVariant : MageRideColor.onSurface)
                    .lineLimit(1)
                Spacer(minLength: 0)
                if let symbolName {
                    Image(systemName: symbolName)
                        .font(.system(size: MageRideControl.chipIcon))
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .frame(minHeight: MageRideControl.minimumTapTarget)
            .background(
                isHighlighted ? MageRideColor.primaryContainer : MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(
                        isHighlighted ? MageRideColor.primary : .clear,
                        lineWidth: MageRideControl.selectedBorder
                    )
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(action == nil)
        .accessibilityElement(children: .combine)
    }
}
