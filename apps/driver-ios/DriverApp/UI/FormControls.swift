import SwiftUI

/// `driver_ios.html`'s field, tile and card primitives.
///
/// Everything here is drawn from the wireframe's own CSS classes — `.field`, `.field.lbl`, `.otp`,
/// `.illus` as a capture slot, `.kv`, `.card.fill`, `.flagchip` — and every measurement comes from
/// ``MageRideSpacing`` / ``MageRideRadius`` / ``MageRideControl``. C087's four document steps draw
/// the same tiles and the same extract card; they belong here rather than in one screen.

// MARK: - Text fields

/// The wireframe's `.field.lbl` — a label above its value, in one filled rounded field.
///
/// **`prefix`, `keyboardType` and `autocapitalisation` are Δ C091**, and each is a property of the
/// *value* rather than of the field: a rupee amount is prefixed `Rs` and typed on a number pad, and a
/// Driver ID is a case-significant identifier that `.words` would capitalise and autocorrect into
/// something the gateway answers `404` to. They are defaulted to what cluster 1 already gets, so no
/// existing caller changes. ``RupeeField`` and ``DriverIdField`` are the two combinations this app
/// actually uses.
struct LabelledTextField: View {

    let labelKey: String
    @Binding var value: String
    var placeholder: String?
    var supportingKey: String?
    var isError: Bool = false
    var prefix: String?
    var keyboardType: UIKeyboardType = .default
    var autocapitalisation: TextInputAutocapitalization = .words

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            VStack(alignment: .leading, spacing: 1) {
                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                HStack(spacing: MageRideSpacing.xxs) {
                    if let prefix {
                        Text(prefix)
                            .mageFont(.bodyEmphasis)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                    }
                    TextField(placeholder ?? "", text: $value)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .keyboardType(keyboardType)
                        .textInputAutocapitalization(autocapitalisation)
                        .autocorrectionDisabled()
                }
            }
            .padding(.horizontal, MageRideSpacing.sm)
            .padding(.vertical, MageRideSpacing.xs)
            .frame(minHeight: MageRideControl.minimumTapTarget)
            .background(
                MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
            .overlay {
                if isError {
                    RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                        .strokeBorder(MageRideColor.error, lineWidth: 1)
                }
            }

            if let supportingKey {
                Text(key: supportingKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
    }
}

/// The wireframe's `Rs 2,000` amount field (C091).
///
/// A number pad and the `Rs` prefix, over a binding the caller has already put through
/// ``WalletInput/rupeeDigits(_:)`` — the field renders what it is given and decides nothing, which is
/// what keeps *"what is a valid amount"* in one testable place rather than in four screens.
struct RupeeField: View {

    let labelKey: String
    @Binding var value: String
    var supportingKey: String?
    var isError: Bool = false

    var body: some View {
        LabelledTextField(
            labelKey: labelKey,
            value: $value,
            supportingKey: supportingKey,
            isError: isError,
            prefix: MoneyFormat.prefix,
            keyboardType: .numberPad,
            autocapitalisation: .never
        )
    }
}

/// The wireframe's `Driver ID` field (C091).
///
/// **Never autocapitalised and never autocorrected.** A platform id is case-significant — a ULID is
/// upper-case and a UUID lower-case — so `.words` would break one of the two forms, and autocorrect
/// on a 26-character string of consonants does worse than that. See ``PlatformId``.
struct DriverIdField: View {

    let labelKey: String
    @Binding var value: String
    var supportingKey: String?
    var isError: Bool = false

    var body: some View {
        LabelledTextField(
            labelKey: labelKey,
            value: $value,
            supportingKey: supportingKey,
            isError: isError,
            keyboardType: .asciiCapable,
            autocapitalisation: .never
        )
    }
}

/// The wireframe's `+94` field — a fixed prefix and the national number beside it.
///
/// `+94` is a prefix on the field rather than a country picker because Sri Lanka is the only
/// country MageRide operates in; see ``PhoneNumber``.
struct PhoneNumberField: View {

    @Binding var value: String
    var isEnabled: Bool = true
    var isError: Bool = false

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Text(PhoneNumber.countryCode)
                .mageFont(.bodyEmphasis)
                .foregroundStyle(MageRideColor.onSurface)

            TextField(PhoneNumber.placeholder, text: $value)
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
                .keyboardType(.numberPad)
                .textContentType(.telephoneNumber)
                .disabled(!isEnabled)
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.minimumTapTarget)
        .background(MageRideColor.surfaceVariant, in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
        .overlay {
            if isError {
                RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                    .strokeBorder(MageRideColor.error, lineWidth: 1)
            }
        }
        .opacity(isEnabled ? 1 : 0.6)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(key: "login_title"))
    }
}

/// The wireframe's `.otp` — six boxes over one hidden field.
///
/// **One `TextField`, not six.** Six would each need their own focus, their own backspace handling
/// and their own paste behaviour, and none of them would get `.oneTimeCode`: QuickType offers the
/// code from the SMS to a *single* field, which is D2's own iOS note for this screen and the whole
/// reason a driver here never types the six digits at all.
struct OtpField: View {

    @Binding var value: String
    let length: Int
    var isEnabled: Bool = true
    var isError: Bool = false

    @FocusState private var isFocused: Bool

    var body: some View {
        ZStack {
            TextField("", text: $value)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .focused($isFocused)
                .disabled(!isEnabled)
                // Invisible rather than hidden: a `.hidden()` field cannot take focus, and a field
                // that cannot take focus never receives the QuickType code.
                .opacity(0.001)
                .onChange(of: value) { typed in
                    let digits = typed.filter { $0.isASCII && $0.isNumber }
                    let trimmed = String(digits.prefix(length))
                    if trimmed != value { value = trimmed }
                }

            HStack(spacing: MageRideSpacing.xs) {
                ForEach(0..<length, id: \.self) { index in
                    cell(at: index)
                }
            }
            .allowsHitTesting(false)
        }
        .contentShape(Rectangle())
        .onTapGesture { isFocused = isEnabled }
        .opacity(isEnabled ? 1 : 0.6)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(key: "login_enter_code"))
        .accessibilityValue(Text(value))
    }

    private func cell(at index: Int) -> some View {
        let digits = Array(value)
        let filled = index < digits.count

        return Text(filled ? String(digits[index]) : " ")
            .mageFont(.title)
            .foregroundStyle(MageRideColor.onSurface)
            .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget + 4)
            .background(
                filled ? MageRideColor.background : MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
                    .strokeBorder(borderColour(filled: filled), lineWidth: filled ? 2 : 0)
            }
    }

    private func borderColour(filled: Bool) -> Color {
        if isError { return MageRideColor.error }
        return filled ? MageRideColor.primary : MageRideColor.outline
    }
}

// MARK: - Capture

/// The wireframe's `📷 Tap to capture` slot, and its `Done ✓` state.
///
/// Tapping it opens **SCR-DI-005** (AL-43) — this tile photographs nothing itself. It shows what is
/// captured rather than a thumbnail of it: the scanner returns a de-skewed document, and a 74pt
/// preview of a licence is neither legible nor a check anybody can make.
struct CaptureTile: View {

    let labelKey: String
    let isCaptured: Bool
    var height: CGFloat = 74
    let onTap: () -> Void

    var body: some View {
        Button(action: onTap) {
            VStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: isCaptured ? "checkmark.circle.fill" : "camera")
                    .font(.title3)
                    .foregroundStyle(isCaptured ? MageRideColor.success : MageRideColor.primary)
                Text(key: isCaptured ? "status_done" : "action_tap_to_capture")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurface)
            }
            .frame(maxWidth: .infinity, minHeight: height)
            .padding(.vertical, MageRideSpacing.xs)
            .background(
                MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.lg, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: MageRideRadius.lg, style: .continuous)
                    .strokeBorder(
                        isCaptured ? MageRideColor.success : MageRideColor.outline,
                        style: StrokeStyle(lineWidth: 1, dash: isCaptured ? [] : [4, 4])
                    )
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

// MARK: - Cards

/// The wireframe's `.card.fill` with a coloured title — the "✦ AI-extracted" and "⚑ needs
/// checking" panels.
///
/// **`titleKey` is optional (Δ C087) and the header row disappears with it.** Several of the same
/// `.card.fill` panels carry no title in the wireframe — SCR-DI-004's Mode-C explainer, SCR-DI-004c's
/// plate-match note, SCR-DI-006's two banners — and an untitled card drawn as a second shape would
/// be two card styles for one CSS class. Existing callers pass a `String` and are unaffected.
struct NoticeCard<Content: View>: View {

    var titleKey: String?
    let symbolName: String
    let accent: Color
    var fill: Color = MageRideColor.surfaceVariant
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: symbolName)
                    .font(.caption)
                if let titleKey {
                    Text(key: titleKey)
                        .mageFont(.label)
                }
            }
            .foregroundStyle(accent)

            content
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(fill, in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }
}

/// The wireframe's `.flagchip` — "⚑ Admin verify".
///
/// It says who will look at the value, not that anything is wrong: a field the driver typed is
/// pending **by design** (BR-25.2, AL-29), and the driver continues either way.
struct AdminVerifyChip: View {

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: "flag.fill")
                .font(.caption2)
            Text(key: "flag_admin_verify")
                .mageFont(.caption)
        }
        .foregroundStyle(MageRideColor.warning)
        .padding(.horizontal, MageRideSpacing.xs)
        .padding(.vertical, 3)
        .background(
            MageRideColor.warning.opacity(0.14),
            in: RoundedRectangle(cornerRadius: MageRideRadius.sm, style: .continuous)
        )
    }
}

/// One `.kv` row of the extract card: the field's label, what was read or typed, an optional ✎ and
/// the ⚑ chip when a Verification Officer will see it.
struct ExtractedFieldRow: View {

    let labelKey: String
    let value: String?
    let isFlagged: Bool
    var onEdit: (() -> Void)?

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Text(key: labelKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            Spacer(minLength: MageRideSpacing.xs)

            if let value, !value.isEmpty {
                Text(value)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurface)
                    .multilineTextAlignment(.trailing)
            } else {
                Text(key: "profile_setup_field_unread")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.error)
            }

            if isFlagged {
                AdminVerifyChip()
            }

            if let onEdit {
                Button(action: onEdit) {
                    Image(systemName: "pencil")
                        .font(.footnote)
                        .foregroundStyle(MageRideColor.primary)
                        .frame(minWidth: MageRideControl.minimumTapTarget / 2, minHeight: MageRideControl.minimumTapTarget / 2)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text(key: "action_edit"))
            }
        }
        .padding(.vertical, 2)
    }
}

/// The one place a screen puts a failure. Always resolved copy, never a `ProblemDetails` string
/// (D-26) — see ``OnboardingErrors``.
struct FormErrorText: View {

    let messageKey: String

    var body: some View {
        Text(key: messageKey)
            .mageFont(.bodySmall)
            .foregroundStyle(MageRideColor.error)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}
