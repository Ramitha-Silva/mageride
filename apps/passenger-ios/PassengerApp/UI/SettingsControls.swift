import SwiftUI

/// `passenger_ios.html`'s settings vocabulary — the shapes SCR-PI-026, SCR-PI-027, SCR-PI-027b and
/// SCR-PI-033 draw between them.
///
/// Four screens and one row language. ``GroupedList`` and ``GroupedRow`` (C095) already draw the
/// wireframe's `.glist` / `.gr`, and everything here either sits *inside* one of those rows or is a
/// row whose text is **data rather than copy** — a passenger's own address label, their name, a
/// contact's number. That is the whole reason ``GroupedValueRow`` exists beside ``GroupedRow``
/// instead of gaining an overload: `GroupedRow` takes localisation *keys*, and a key is exactly what
/// a passenger-typed string is not.
///
/// Append here rather than putting a shape or a number at a call site — the rule
/// `UI/OnboardingControls.swift` sets and C096–C100 followed.

// MARK: - Row furniture

/// The wireframe's `.chev` — an optional value, then `›`.
///
/// `English ›`, `Cash ›`, or a bare chevron. The value is a **resolved string**, because the two
/// callers pass a language endonym (data — see ``LanguageDisplay``) and a rail's translated label.
struct RowChevron: View {

    var value: String?

    var body: some View {
        HStack(spacing: MageRideSpacing.xxs) {
            if let value {
                Text(value)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .lineLimit(1)
            }
            Image(systemName: "chevron.right")
                .font(.system(size: MageRideControl.chipIcon, weight: .semibold))
                .foregroundStyle(MageRideColor.outlineVariant)
        }
        // One announcement per row: the value is read as part of the row's label, and a lone `›`
        // announced as "chevron right" is noise a VoiceOver user cannot act on.
        .accessibilityHidden(true)
    }
}

/// One `.glist .gr` whose title and subtitle are **values**, not keys.
///
/// SCR-PI-026's address rows (`Home` / `221 Galle Rd, Dehiwala`) and SCR-PI-027b's contact rows
/// (`Amma` / `+94 77 000 1111`) are the two callers, and in both the second line is something the
/// passenger typed. ``GroupedRow`` is the same shape for copy; this one takes strings so a caller
/// never has to resolve a key that does not exist.
///
/// The leading glyph is tinted like `GroupedRow`'s — the wireframe's own per-row `.ic` background —
/// so an address row and a settings row are recognisably the same list.
struct GroupedValueRow<Trailing: View>: View {

    let title: String
    var subtitle: String?
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
                        .background(
                            symbolTint,
                            in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                        )
                }

                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .lineLimit(1)
                    if let subtitle {
                        Text(subtitle)
                            .mageFont(.caption)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .lineLimit(1)
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

/// A `.gr` whose whole content is one centred, coloured word.
///
/// The wireframe draws four of them and all four are actions rather than navigation: SCR-PI-027's
/// **Log out** (`var(--primary)`) and **Delete account** (`var(--error)`), and SCR-PI-027b's
/// **＋ Add SOS contact**. Drawn as a row inside a `glist` rather than as a `TextLink` under one,
/// because that is where the cells put them — a grouped list is how iOS spells a list of actions.
struct CentredActionRow: View {

    let titleKey: String
    var tint: Color = MageRideColor.primary
    var isEnabled: Bool = true
    var showsSeparator: Bool = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 0) {
                Text(key: titleKey)
                    .mageFont(.bodyEmphasis)
                    .foregroundStyle(isEnabled ? tint : MageRideColor.outlineVariant)
                    .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)

                if showsSeparator {
                    Rectangle()
                        .fill(MageRideColor.surfaceVariant)
                        .frame(height: MageRideControl.hairline)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
    }
}

// MARK: - The identity card

/// The wireframe's `card` at the top of SCR-PI-027 and SCR-PI-033 — an avatar disc, a name, and
/// `ID · phone`.
///
/// **The id is the account's ULID, and that is a contract gap rather than a layout choice.** Both
/// cells print `PAX-90431`, and no contract carries a human-readable passenger number: `UserProfile`
/// has `userId` and nothing else that identifies the account to its owner. Drawing the ULID is the
/// honest answer — it is what support would ask for — and a made-up `PAX-` prefix over it would be a
/// client inventing an identifier. C083 recorded it from the Android side; the C100 handoff made the
/// same call about a Vehicle ID.
///
/// **It renders brand-only until there is a profile, and never a placeholder name.** A greyed-out
/// *"Your name"* in the shape of the real thing is how a half-loaded screen ships looking finished.
///
/// The chevron is SCR-PI-027's and not SCR-PI-033's: that cell's card opens ``EditProfileScreen``
/// and the Menu's is an identity block with nothing behind it, which is exactly what the two frames
/// draw.
struct IdentityCard: View {

    /// `nil` before the first read — see the type's note.
    let name: String?

    /// `ID: 01J… · +94 77 123 4567`, already formatted. `nil` draws the name alone.
    let identity: String?

    var showsChevron: Bool = false
    var action: (() -> Void)?

    var body: some View {
        if let action {
            Button(action: action) { card }
                .buttonStyle(.plain)
                .accessibilityElement(children: .combine)
                .accessibilityAddTraits(.isButton)
        } else {
            card.accessibilityElement(children: .combine)
        }
    }

    private var card: some View {
        HStack(spacing: MageRideSpacing.sm) {
            Circle()
                .fill(MageRideColor.background)
                .frame(width: MageRideControl.avatarSmall, height: MageRideControl.avatarSmall)
                .overlay {
                    Image(systemName: "person.fill")
                        .font(.system(size: MageRideControl.rowIcon))
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

            VStack(alignment: .leading, spacing: 1) {
                Text(displayName)
                    .mageFont(.subtitle)
                    .foregroundStyle(MageRideColor.onSurface)
                    .lineLimit(1)

                if let identity {
                    Text(identity)
                        .mageFont(.caption)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .lineLimit(1)
                }
            }

            Spacer(minLength: MageRideSpacing.xs)

            if showsChevron {
                RowChevron()
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .contentShape(Rectangle())
    }

    /// The passenger's name, or the wireframe's stand-in. A whitespace-only `firstName` is the same
    /// as none — `iam.users.first_name` is optional and nothing trims it on the way in.
    private var displayName: String {
        guard let name, !name.trimmed.isEmpty else { return "settings_unnamed_passenger".localised }
        return name
    }
}
