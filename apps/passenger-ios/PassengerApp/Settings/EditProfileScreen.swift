import MageRideShared
import SwiftUI

/// SCR-PI-027b — *"Edit profile"*.
///
/// The cell: `‹ Settings · Edit profile · **Save**`, the `avatar lg` with its 📷 badge and *"Take
/// photo or upload"*, a `glist` holding **Full name**, a second holding the **Notifications &
/// offers** switch, the `SOS / emergency contacts` label, and a third `glist` of contact rows over a
/// centred `＋ Add SOS contact`.
///
/// **No language row** (AL-26) — the cell says so, and the model's save has no field for one.
///
/// **The photo control is drawn and inert, and that is a missing route rather than a missing
/// screen.** `UpdateProfileRequest.photoUrl` is a *URL*, and nothing on the app-facing surface mints
/// one for a passenger: `POST /v1/support/screenshots`, the Mode B transfer slip and the driver's
/// document uploads are the whole upload set, and none of them is an avatar. C095 hit the same wall
/// on SCR-PI-004 and made the same call; landing it needs an upload route first. Disabled rather
/// than silently ignored, because a control that looks live and does nothing is worse than one that
/// says it is not ready. The cell's `PhotosPicker` note is therefore unimplementable today and is
/// recorded in the C101 handoff.
///
/// **Save is in the navigation bar**, which is what the cell's `.act` slot draws and what
/// SCR-PI-004 already does on this platform.
@MainActor
struct EditProfileScreen: View {

    @StateObject private var model: EditProfileModel

    /// Popped on a successful save — the wireframe's `‹ Settings` is where a saved profile lands.
    let onSaved: () -> Void

    init(
        profiles: PassengerProfileRepository,
        contacts: SosContacts,
        identity: PassengerIdentity,
        keys: IdempotencyKeys,
        onSaved: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: EditProfileModel(
                profiles: profiles,
                contacts: contacts,
                identity: identity,
                keys: keys
            )
        )
        self.onSaved = onSaved
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                avatar

                GroupedList {
                    GroupedRow(titleKey: "edit_profile_name", showsSeparator: false) {
                        TextField("edit_profile_name".localised, text: nameBinding)
                            .mageFont(.body)
                            .foregroundStyle(MageRideColor.onSurface)
                            .multilineTextAlignment(.trailing)
                            .textContentType(.givenName)
                            .textInputAutocapitalization(.words)
                            .disabled(!model.state.isLoaded || model.state.isSaving)
                    }
                }

                GroupedList {
                    GroupedRow(titleKey: "edit_profile_notifications", showsSeparator: false) {
                        Toggle("", isOn: notificationsBinding)
                            .labelsHidden()
                            .disabled(!model.state.isLoaded || model.state.isSaving)
                    }
                }

                SectionLabel(key: "edit_profile_sos_contacts")
                    .padding(.top, MageRideSpacing.xxs)

                if model.state.contacts.isEmpty {
                    // What an empty list costs, said here rather than at the moment of an alarm:
                    // `POST /v1/sos` answers `400 no-emergency-contact` with none on file.
                    Text(key: "edit_profile_sos_empty")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                GroupedList {
                    ForEach(model.state.contacts, id: \.contactId) { contact in
                        Button {
                            model.editContact(contact)
                        } label: {
                            GroupedValueRow(
                                title: contact.name,
                                subtitle: contact.phone,
                                symbolName: "person.fill",
                                symbolTint: MageRideColor.error
                            ) {
                                // The cell draws `✎` where every other row on this surface draws
                                // `›`, because the row edits rather than navigates.
                                Image(systemName: "pencil")
                                    .font(.system(size: MageRideControl.chipIcon, weight: .semibold))
                                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                                    .accessibilityHidden(true)
                            }
                        }
                        .buttonStyle(.plain)
                        .disabled(model.state.removing == contact.contactId)
                    }

                    CentredActionRow(titleKey: "edit_profile_add_sos", isEnabled: model.state.isLoaded) {
                        model.addContact()
                    }
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "edit_profile_title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                Button {
                    Task { await model.save() }
                } label: {
                    if model.state.isSaving {
                        ProgressView()
                    } else {
                        Text(key: "action_save").mageFont(.subtitle)
                    }
                }
                .disabled(!model.state.canSave)
            }
        }
        .task { await model.load() }
        .onChange(of: model.state.isSaved) { saved in
            if saved { onSaved() }
        }
        .sheet(isPresented: contactBinding) {
            if let draft = model.state.contactDraft {
                SosContactSheet(
                    draft: draft,
                    onNameChanged: model.onContactNameChanged,
                    onPhoneChanged: model.onContactPhoneChanged,
                    onSave: { Task { await model.saveContact() } },
                    onRemove: { Task { await model.removeContact() } }
                )
            }
        }
    }

    // MARK: -

    /// The wireframe's `avatar lg` with its 📷 badge and the *"Take photo or upload"* textlink.
    ///
    /// Both inert — see the screen's note. The photo itself is not rendered either: the profile
    /// carries a URL and this app ships no image loader (C100 argued the same point about the fleet
    /// owner's LankaQR and decoded bytes by hand rather than adding one).
    private var avatar: some View {
        VStack(spacing: MageRideSpacing.xs) {
            ProfileAvatar(badgeSymbol: "camera.fill")
            TextLink(key: "edit_profile_photo", isEnabled: false) { }
        }
        .frame(maxWidth: .infinity)
    }

    private var nameBinding: Binding<String> {
        Binding(get: { model.state.name }, set: model.onNameChanged)
    }

    private var notificationsBinding: Binding<Bool> {
        Binding(get: { model.state.notificationsEnabled }, set: model.onNotificationsChanged)
    }

    private var contactBinding: Binding<Bool> {
        Binding(
            get: { model.state.contactDraft != nil },
            set: { presented in if !presented { model.dismissContact() } }
        )
    }
}

/// The add / edit SOS contact sheet.
///
/// **A `.sheet` where Android has an `AlertDialog`**, for the reason SCR-PI-026a is one: this cluster
/// captures two things and both are short forms with a keyboard, and an alert with two text fields in
/// it is a Material shape rather than an iOS one. The cell draws no frame for this control at all, so
/// the conservative reading is the one its sibling already established.
///
/// The number is entered through the same `+94`-prefixed field SCR-PI-003 uses, because it is the
/// same question: Sri Lanka is the only country this platform operates in, so the prefix is a label
/// on the field rather than a country picker, and both `0771234567` and `771234567` have to work.
private struct SosContactSheet: View {

    let draft: SosContactDraft
    let onNameChanged: (String) -> Void
    let onPhoneChanged: (String) -> Void
    let onSave: () -> Void
    let onRemove: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            Text(key: draft.isEditing ? "edit_profile_edit_sos" : "edit_profile_add_sos")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)

            LabelledTextField(
                labelKey: "edit_profile_sos_name",
                value: Binding(get: { draft.name }, set: onNameChanged)
            )

            SectionLabel(key: "edit_profile_sos_phone")
            PhoneNumberField(
                value: Binding(get: { draft.phone }, set: onPhoneChanged),
                isEnabled: !draft.isSaving
            )

            Button(action: onSave) {
                Text(key: "action_save")
            }
            .buttonStyle(.mageCta(loading: draft.isSaving))
            .disabled(!draft.canSave)
            .padding(.top, MageRideSpacing.xxs)

            if draft.isEditing {
                Button(action: onRemove) {
                    Text(key: "edit_profile_remove_sos")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.error)
                        .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(draft.isSaving)
            }

            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .presentationDetents([.height(MageRideControl.contactSheetHeight), .large])
        .presentationDragIndicator(.visible)
    }
}
