import MageRideShared
import SwiftUI

/// SCR-DI-029's three editors, each a `.sheet` at the `.medium` detent.
///
/// A sheet rather than a destination for each: none of the three is drawn as a screen in the wireframe
/// — the ✎ on the header row and the ✎ on the emergency-contact row are affordances *on* the profile —
/// and AL-35's ruling on SCR-DI-030's rating sheet (*"a modal bottom sheet, not an inline card"*) is
/// the same shape applied to the same kind of edit. `.medium` for the same reason SCR-DI-033a takes it:
/// the profile stays visible behind, which is what says the edit is a detour rather than a screen.
@MainActor
struct ProfileEditorSheet: View {

    let sheet: ProfileSheet
    @ObservedObject var model: DriverProfileModel

    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                switch sheet {
                case .name: NameEditor(model: model)
                case .emergency: EmergencyContactEditor(model: model)
                case .language: LanguageEditor(model: model)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .background(MageRideColor.background)
            .navigationTitle(Text(key: titleKey))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { dismiss() } label: { Text(key: "action_cancel") }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private var titleKey: String {
        switch sheet {
        case .name: return "profile_name_title"
        case .emergency: return "profile_emergency_title"
        case .language: return "profile_language_title"
        }
    }
}

/// The header ✎ — `PUT /v1/users/me` with a display name (US-1.5).
private struct NameEditor: View {

    @ObservedObject var model: DriverProfileModel

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            LabelledTextField(
                labelKey: "profile_name_label",
                value: Binding(get: { model.state.nameDraft }, set: model.onNameChange)
            )

            Button {
                Task { await model.saveName() }
            } label: {
                Text(key: "action_save")
            }
            .buttonStyle(.mageCta(loading: model.state.isSaving))
            .disabled(!model.state.canSaveName)

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }
        }
        .padding(MageRideSpacing.md)
    }
}

/// AL-13's emergency contact — the name and number **SCR-DI-032's driver SOS sends to** (D-33).
///
/// The picker is offered above the fields rather than instead of them: an address book is the fast path
/// and typing is the one that always works, and safety-svc answers `400 no-emergency-contact` to an SOS
/// raised by an account with none.
private struct EmergencyContactEditor: View {

    @ObservedObject var model: DriverProfileModel

    @State private var isPickingContact = false

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            Text(key: "profile_emergency_why")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)

            Button { isPickingContact = true } label: {
                HStack(spacing: MageRideSpacing.xxs) {
                    Image(systemName: "person.crop.circle.badge.plus")
                        .font(.footnote)
                    Text(key: "profile_emergency_pick")
                        .mageFont(.bodyEmphasis)
                }
                .foregroundStyle(MageRideColor.primary)
            }
            .buttonStyle(.plain)

            LabelledTextField(
                labelKey: "profile_emergency_name_label",
                value: Binding(get: { model.state.contactNameDraft }, set: model.onContactNameChange)
            )

            VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
                SectionLabel(key: "profile_emergency_phone_label")
                PhoneNumberField(
                    value: Binding(get: { model.state.contactPhoneDraft }, set: model.onContactPhoneChange),
                    isError: model.state.isContactPhoneRejected
                )
                if model.state.isContactPhoneRejected {
                    FormErrorText(messageKey: "profile_emergency_phone_invalid")
                }
            }

            Button {
                Task { await model.saveEmergencyContact() }
            } label: {
                Text(key: "action_save")
            }
            .buttonStyle(.mageCta(loading: model.state.isSaving))
            .disabled(!model.state.canSaveContact)

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }
        }
        .padding(MageRideSpacing.md)
        .sheet(isPresented: $isPickingContact) {
            ContactPickerView(
                onPicked: { name, phone in
                    isPickingContact = false
                    model.onContactPicked(name: name, phone: phone)
                },
                onDismiss: { isPickingContact = false }
            )
            .ignoresSafeArea()
        }
    }
}

/// AL-26's three boxes again — the same control SCR-DI-002 uses, and the same endonym table.
private struct LanguageEditor: View {

    @ObservedObject var model: DriverProfileModel

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
            // `LanguageCityState.languages` rather than a second list: AL-26 fixes the order — Sinhala
            // first and default — and SCR-DI-002 is where it is written down.
            GroupedList {
                ForEach(Array(LanguageCityState.languages.enumerated()), id: \.offset) { index, language in
                    SelectionRow(
                        label: LanguageDisplay.endonym(language),
                        secondary: LanguageDisplay.englishName(language),
                        isSelected: model.state.profile?.language == language,
                        showsSeparator: index < LanguageCityState.languages.count - 1,
                        onSelect: { Task { await model.choose(language: language) } }
                    )
                }
            }

            if let errorKey = model.state.errorKey {
                FormErrorText(messageKey: errorKey)
            }
        }
        .padding(MageRideSpacing.md)
    }
}
