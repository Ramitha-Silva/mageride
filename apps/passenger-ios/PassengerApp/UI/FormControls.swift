import SwiftUI

/// `passenger_ios.html`'s field primitives.
///
/// Everything here is drawn from the wireframe's own CSS classes — `.field`, `.field .pre`, `.otp`,
/// `.textlink`, `.avatar.lg` with its `.cam` badge — and every measurement comes from
/// ``MageRideSpacing`` / ``MageRideRadius`` / ``MageRideControl``. C096–C102 draw the same shapes;
/// take them from here rather than redrawing one, and append yours rather than putting a number at
/// a call site.

// MARK: - Text fields

/// The wireframe's `.field` with a label above its value.
///
/// SCR-PI-004's Name row uses it; so will every later form. The label is a key and the value is a
/// binding, which is the split every field in this app makes: copy is translated, a passenger's
/// own name is not.
struct LabelledTextField: View {

    let labelKey: String
    @Binding var value: String
    var placeholder: String?
    var supportingKey: String?
    var isError: Bool = false
    var keyboardType: UIKeyboardType = .default
    var autocapitalisation: TextInputAutocapitalization = .words
    var textContentType: UITextContentType?

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            VStack(alignment: .leading, spacing: 1) {
                Text(key: labelKey)
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                TextField(placeholder ?? "", text: $value)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                    .keyboardType(keyboardType)
                    .textInputAutocapitalization(autocapitalisation)
                    .textContentType(textContentType)
                    .autocorrectionDisabled()
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

/// The wireframe's `.field` with a `+94` prefix — SCR-PI-003's number.
///
/// The prefix is drawn rather than typed, because ``PhoneNumber`` normalises everything else away:
/// Sri Lanka is the only country this platform operates in, so a country picker would be a control
/// with one option.
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
                // D2' §SCR-PI-003's own `Δ iOS` clause: `.keyboardType(.phonePad)`.
                .keyboardType(.phonePad)
                .textContentType(.telephoneNumber)
                .disabled(!isEnabled)
        }
        .padding(.horizontal, MageRideSpacing.sm)
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
        .opacity(isEnabled ? 1 : 0.6)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text(key: "login_phone_label"))
    }
}

/// The wireframe's `.otp` — six boxes over one hidden field.
///
/// **One `TextField`, not six.** Six would each need their own focus, their own backspace handling
/// and their own paste behaviour, and none of them would get `.oneTimeCode`: QuickType offers the
/// code from the SMS to a *single* field, which is D2' §SCR-PI-003's own iOS note for this screen
/// (*"`.oneTimeCode` QuickType"*) and the whole reason a passenger here never types the six digits
/// at all.
///
/// This is also what closes the gap C077 recorded from the other side: Android's SMS Retriever
/// cannot be wired without a release signing certificate, so that app gets the keyboard's
/// suggestion strip and nothing more. iOS needs no hash and no signing config — the OS matches the
/// message to the field by content type.
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

// MARK: - Text and links

/// The wireframe's `.textlink` — a `primary` label that is a button and nothing else.
///
/// A `Button` with a plain style rather than `.borderless`: the wireframe draws no chrome at all,
/// and `.borderless` still applies the tint's own pressed treatment on some iOS versions.
struct TextLink: View {

    let key: String
    var isEnabled: Bool = true
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(key: key)
                .mageFont(.bodySmall)
                .foregroundStyle(isEnabled ? MageRideColor.primary : MageRideColor.outlineVariant)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
    }
}

/// The same shape with a value in it — SCR-PI-003's `Resend (54s)`.
///
/// A second type rather than a `String` parameter on ``TextLink`` because the countdown is a
/// *format* with a positional specifier, and `LocalizationTests` asserts that `%1$d` survives into
/// Sinhala and Tamil. A caller that interpolated the seconds itself would defeat that.
struct CountdownLink: View {

    let key: String
    let seconds: Int
    var isEnabled: Bool = true
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(key.localisedFormat(seconds))
                .mageFont(.bodySmall)
                .foregroundStyle(isEnabled ? MageRideColor.primary : MageRideColor.outlineVariant)
                .frame(minHeight: MageRideControl.minimumTapTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
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

/// The wireframe's `.divider` — a hairline with a caption sitting on it (`enter code`).
struct LabelledDivider: View {

    let key: String

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            line
            Text(key: key)
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.outlineVariant)
            line
        }
        .accessibilityHidden(true)
    }

    private var line: some View {
        Rectangle()
            .fill(MageRideColor.outline)
            .frame(height: MageRideControl.hairline)
    }
}

// MARK: - Avatar

/// The wireframe's `.avatar.lg` with its `＋` badge — SCR-PI-004's profile disc.
///
/// **The badge does nothing yet, and that is a contract gap rather than an omission.** D2'
/// §SCR-PI-004 names `PhotosPicker` and `UpdateProfileRequest.photoUrl` is a **URL** — but nothing
/// on the app-facing surface mints one for a passenger. `POST /v1/support/screenshots` and the
/// driver's document routes are the only uploads in the sixteen contracts and neither is this, so a
/// picker here would hand the passenger an image the app cannot send. C077 recorded the same gap
/// from the Android side; landing it needs an upload route first, and then this control grows an
/// `onTap`.
///
/// **`badgeSymbol` is a parameter because the two cells genuinely differ** (Δ C101): SCR-PI-004
/// draws a `＋` on the badge and SCR-PI-027b draws a `📷`, which is the difference between *"add
/// one"* and *"change it"*. Same disc, same disabled treatment, same missing route.
struct ProfileAvatar: View {

    var isEnabled: Bool = false
    var badgeSymbol: String = "plus"

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            Circle()
                .fill(MageRideColor.surfaceVariant)
                .frame(width: MageRideControl.avatarLarge, height: MageRideControl.avatarLarge)
                .overlay {
                    Image(systemName: "person.fill")
                        .font(.system(size: MageRideControl.illustrationIcon))
                        .foregroundStyle(MageRideColor.outlineVariant)
                }

            Image(systemName: badgeSymbol)
                .font(.footnote.weight(.bold))
                .foregroundStyle(MageRideColor.onPrimary)
                .frame(width: MageRideControl.avatarBadge, height: MageRideControl.avatarBadge)
                .background(isEnabled ? MageRideColor.primary : MageRideColor.outlineVariant, in: Circle())
                .overlay { Circle().strokeBorder(MageRideColor.background, lineWidth: 2) }
        }
        .opacity(isEnabled ? 1 : 0.6)
        // Not a button and not announced as one: there is nothing behind it (see the type's note),
        // and a VoiceOver user told "add photo, button" would tap something that does nothing.
        .accessibilityHidden(true)
    }
}
