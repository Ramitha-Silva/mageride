import SwiftUI

/// `passenger_ios.html`'s cluster-8 shapes — the editable search bar and the multi-line field.
///
/// Same rule as ``FormControls``, ``OnboardingControls``, ``MapControls``, ``BookingControls``,
/// ``RideControls``, ``HistoryControls`` and ``SettingsControls``: every measurement is a token, and
/// a later screen group takes a shape from here rather than redrawing one.
///
/// Two controls, because the rest of SCR-PI-030 and SCR-PI-030a is already drawable — the FAQ rows
/// are a ``GroupedList``, the ticket chip is a ``StatusPill``, the CTA pair is `.mageCta` /
/// `.mageCtaTonal` and the attach button is an ``OutlinedAction``-shaped `PhotosPicker`. That is the
/// point of the thirty-odd controls above.

// MARK: - Search

/// The wireframe's `.searchbar`, with something typed into it.
///
/// **Drawn rather than `.searchable`, and the wireframe is why.** That modifier belongs to a `List`
/// or a `NavigationStack` and puts its field in the **navigation bar**; SCR-PI-030 draws
/// *"🔍 Search help"* in the **body**, under the large title and above the FAQ group. The same call
/// `apps/driver-ios` made for SCR-DI-033.
///
/// **Not ``SearchBarButton``**, which is C096's and is a *button*: tapping that one opens
/// SCR-PI-008. This one is a `TextField`, because SCR-PI-030's search filters a list already on
/// screen and never navigates.
struct SearchField: View {

    let placeholderKey: String
    @Binding var value: String

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: MageRideControl.rowIcon))
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            TextField(placeholderKey.localised, text: $value)
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurface)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.search)

            if !value.isEmpty {
                Button {
                    value = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: MageRideControl.rowIcon))
                        .foregroundStyle(MageRideColor.outlineVariant)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text(key: "action_clear"))
            }
        }
        .padding(.horizontal, MageRideSpacing.sm)
        .frame(minHeight: MageRideControl.searchBar)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}

// MARK: - Multi-line entry

/// The wireframe's `.field.lbl` at `min-height:72px` — a label over a box that holds a paragraph.
///
/// **A `TextEditor` with a drawn placeholder, not `TextField(axis: .vertical)`.** SCR-PI-030a
/// reserves three lines *before* anything is typed, and an axis-vertical `TextField` grows from one
/// — which draws the wireframe's box only once the passenger has already filled it. `TextEditor` has
/// no placeholder of its own, so the hint is a `Text` behind it; the same control
/// `apps/driver-ios` added for SCR-DI-033a.
///
/// `.scrollContentBackground(.hidden)` is what lets the token fill show through — a `TextEditor`
/// paints `UITextView`'s own background otherwise, and the field would be white inside a grey box.
struct MultilineTextField: View {

    let labelKey: String
    let placeholderKey: String
    @Binding var value: String

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            Text(key: labelKey)
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            ZStack(alignment: .topLeading) {
                if value.isEmpty {
                    Text(key: placeholderKey)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .padding(.horizontal, MageRideSpacing.xxs)
                        .padding(.vertical, MageRideSpacing.xs)
                        .accessibilityHidden(true)
                }

                TextEditor(text: $value)
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                    .scrollContentBackground(.hidden)
                    .frame(minHeight: MageRideControl.multilineField)
            }
            .padding(.horizontal, MageRideSpacing.xs)
            .padding(.vertical, MageRideSpacing.xxs)
            .background(
                MageRideColor.surfaceVariant,
                in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
            )
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(Text(key: labelKey))
    }
}
