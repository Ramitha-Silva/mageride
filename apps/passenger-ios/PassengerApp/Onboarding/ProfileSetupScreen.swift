import MageRideShared
import SwiftUI

/// **SCR-PI-004 · profile setup** — US-1.5's first profile.
///
/// The wireframe's column: a nav row with `‹ Back`, the title *Set up profile* and a bold **Save**,
/// then the avatar with its `＋` badge, then one grouped list of three rows — Name, Language and
/// *Notifications & offers*.
///
/// **Save is in the nav row, not a bottom CTA**, which is what the cell draws and what the `Δ iOS`
/// clause (*"`PhotosPicker` + `Form`"*) implies: this is a settings-shaped screen rather than a
/// wizard step. The Android twin has a bottom *"Save & continue"* because its wireframe draws one;
/// same action, same one `PUT`.
///
/// **`‹ Back` is drawn and does nothing**, which is the wireframe's own state: cluster 1 is a
/// one-way flow — the shell *replaces* its pre-session root rather than stacking (see
/// ``PassengerNavigator/open(_:)``) — so there is no OTP screen behind this one to return to, and a
/// passenger who is signed in has nowhere back to go. It is left visible because the cell draws it;
/// it is inert rather than absent so the row's layout matches the frame.
@MainActor
struct ProfileSetupScreen: View {

    @StateObject private var model: ProfileSetupModel

    private let onComplete: () -> Void

    init(
        profiles: PassengerProfileRepository,
        preferences: AppPreferences,
        onComplete: @escaping () -> Void
    ) {
        _model = StateObject(wrappedValue: ProfileSetupModel(profiles: profiles, preferences: preferences))
        self.onComplete = onComplete
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.md) {
            navigationRow

            ProfileAvatar()
                .padding(.vertical, MageRideSpacing.xxs)

            GroupedList {
                GroupedRow(titleKey: "profile_name_label") {
                    TextField("profile_name_hint".localised, text: nameBinding)
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurface)
                        .multilineTextAlignment(.trailing)
                        .textContentType(.givenName)
                        .textInputAutocapitalization(.words)
                        .disabled(!model.state.isLoaded || model.state.isBusy)
                }

                GroupedRow(titleKey: "onboarding_language_label") {
                    // A `.menu` `Picker` rather than a second list of rows: SCR-PI-002 already drew
                    // the three boxes and this row exists to *correct* a choice, not to make one.
                    Picker("", selection: languageBinding) {
                        ForEach(LanguageDisplay.choices, id: \.self) { language in
                            Text(LanguageDisplay.endonym(language)).tag(language)
                        }
                    }
                    .pickerStyle(.menu)
                    .tint(MageRideColor.onSurfaceVariant)
                    .disabled(!model.state.isLoaded || model.state.isBusy)
                }

                GroupedRow(titleKey: "profile_notifications", showsSeparator: false) {
                    Toggle("", isOn: notificationsBinding)
                        .labelsHidden()
                        .disabled(!model.state.isLoaded || model.state.isBusy)
                }
            }

            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.surface)
        .task { await model.load() }
        .onChange(of: model.isSaved) { saved in
            if saved { onComplete() }
        }
        .alert(
            Text(key: "profile_error_title"),
            isPresented: alertBinding,
            presenting: model.state.errorKey
        ) { _ in
            Button { } label: { Text(key: "action_ok") }
        } message: { key in
            Text(key: key)
        }
    }

    /// The wireframe's `.navtop`: an inert `‹ Back`, the title, and Save.
    private var navigationRow: some View {
        HStack {
            Text(key: "action_back")
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.outlineVariant)

            Spacer()

            Text(key: "profile_title")
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.onSurface)

            Spacer()

            Button {
                Task { await model.submit() }
            } label: {
                if model.state.isBusy {
                    ProgressView().tint(MageRideColor.primary)
                } else {
                    Text(key: "action_save")
                        .mageFont(.subtitle)
                        .foregroundStyle(model.state.canSubmit ? MageRideColor.primary : MageRideColor.outlineVariant)
                }
            }
            .buttonStyle(.plain)
            .disabled(!model.state.canSubmit)
        }
        .frame(minHeight: MageRideControl.minimumTapTarget)
    }

    private var nameBinding: Binding<String> {
        Binding(get: { model.state.name }, set: { model.onNameChanged($0) })
    }

    private var languageBinding: Binding<Language> {
        Binding(get: { model.state.language }, set: { model.onLanguageChanged($0) })
    }

    private var notificationsBinding: Binding<Bool> {
        Binding(get: { model.state.notificationsEnabled }, set: { model.onNotificationsChanged($0) })
    }

    private var alertBinding: Binding<Bool> {
        Binding(
            get: { model.state.errorKey != nil },
            set: { presented in if !presented { model.dismissError() } }
        )
    }
}
