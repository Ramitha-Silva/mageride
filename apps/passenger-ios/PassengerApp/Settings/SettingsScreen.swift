import MageRideShared
import SwiftUI

/// SCR-PI-027 — *"Settings"*.
///
/// The cell: a **large title**, the profile card with its `›`, a `glist` of 🌐 Language / ★ Save Home
/// & Work / 💳 Default payment / 🔔 Notifications, a second `glist` holding 💬 Help & support, and a
/// third holding the centred **Log out** and **Delete account** rows.
///
/// **There is no separate *"Saved addresses"* row here, and that is the wireframes' own difference.**
/// `passenger_android.html` draws five rows in the first group — Language, Save Home & Work, Saved
/// addresses, Default payment, Notifications — and this cell draws four, because on this platform
/// the Menu **tab** already carries *"Saved addresses"* one tap away and a drawer does not. *Save
/// Home & Work* opens SCR-PI-026 either way, which is what makes the behaviour identical: layout
/// follows the wireframe, behaviour follows Android (the C099 split).
///
/// **Default payment offers Cash and Wallet.** The cell's states line still says
/// *"Cash/LankaQR/OnePay"*, which predates the 2026-08-01 payment-custody change set: AL-57 and
/// AL-59 retired both of those rails, and the third surviving one — the driver's QR — is a
/// settlement choice `iam.yaml` itself excludes from a stored preference. See
/// ``PaymentRails/preferable``; the wireframe needs a micro-change-set, recorded in the C101
/// handoff.
///
/// **Nothing on this screen navigates on log out.** See ``SettingsModel/logOut()``.
@MainActor
struct SettingsScreen: View {

    @StateObject private var model: SettingsModel

    /// SCR-PI-027b.
    let onEditProfile: () -> Void

    /// SCR-PI-026 — reached from *Save Home & Work*.
    let onSavedAddresses: () -> Void

    /// SCR-PI-030.
    let onSupport: () -> Void

    init(
        profiles: PassengerProfileRepository,
        identity: PassengerIdentity,
        preferences: AppPreferences,
        sessions: PassengerSessions,
        onEditProfile: @escaping () -> Void,
        onSavedAddresses: @escaping () -> Void,
        onSupport: @escaping () -> Void
    ) {
        _model = StateObject(
            wrappedValue: SettingsModel(
                profiles: profiles,
                identity: identity,
                preferences: preferences,
                sessions: sessions
            )
        )
        self.onEditProfile = onEditProfile
        self.onSavedAddresses = onSavedAddresses
        self.onSupport = onSupport
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.md) {
                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                IdentityCard(
                    name: model.state.profile?.firstName,
                    identity: model.state.identityLine,
                    showsChevron: true,
                    action: onEditProfile
                )

                preferenceRows

                GroupedList {
                    Button(action: onSupport) {
                        GroupedRow(
                            titleKey: "menu_support",
                            symbolName: "bubble.left.and.bubble.right.fill",
                            symbolTint: MageRideColor.success,
                            showsSeparator: false
                        ) {
                            RowChevron()
                        }
                    }
                    .buttonStyle(.plain)
                }

                // The `202`'s acknowledgement, which is deliberately NOT "your account has been
                // deleted". See ``SettingsModel/deleteAccount()``.
                if model.state.deletionRequestId != nil {
                    Text(key: "settings_delete_requested")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                }

                GroupedList {
                    CentredActionRow(titleKey: "settings_log_out", showsSeparator: true) {
                        Task { await model.logOut() }
                    }
                    CentredActionRow(
                        titleKey: "settings_delete_account",
                        tint: MageRideColor.error,
                        isEnabled: model.state.deletionRequestId == nil
                    ) {
                        model.confirmDelete()
                    }
                }
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "menu_settings"))
        .navigationBarTitleDisplayMode(.large)
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
        .sheet(item: pickerBinding) { picker in
            SettingsPickerSheet(
                picker: picker,
                language: model.state.language,
                payment: model.state.defaultPayment,
                onLanguage: { language in Task { await model.chooseLanguage(language) } },
                onPayment: { method in Task { await model.chooseDefaultPayment(method) } }
            )
        }
        // US-1.8's confirm, and the sentence that keeps it honest — the cell's own
        // *"delete → `.alert` confirm (PDPA)"*.
        .alert(
            Text(key: "settings_delete_title"),
            isPresented: deleteBinding
        ) {
            Button(role: .destructive) {
                Task { await model.deleteAccount() }
            } label: {
                Text(key: "settings_delete_confirm")
            }
            Button(role: .cancel) { model.dismissDelete() } label: { Text(key: "action_cancel") }
        } message: {
            Text(key: "settings_delete_body")
        }
    }

    // MARK: -

    /// The wireframe's first `glist` — four rows, and the fourth is a `Toggle` rather than a link.
    private var preferenceRows: some View {
        GroupedList {
            Button {
                model.openPicker(.language)
            } label: {
                GroupedRow(
                    titleKey: "settings_language",
                    symbolName: "globe",
                    symbolTint: MageRideColor.outlineVariant
                ) {
                    // The endonym, not a translated language name: `සිංහල` is the same string in all
                    // three locales, which is why it is data — see ``LanguageDisplay``.
                    RowChevron(value: LanguageDisplay.endonym(model.state.language))
                }
            }
            .buttonStyle(.plain)

            Button(action: onSavedAddresses) {
                GroupedRow(
                    titleKey: "settings_save_home_work",
                    symbolName: "star.fill",
                    symbolTint: MageRideColor.primary
                ) {
                    RowChevron()
                }
            }
            .buttonStyle(.plain)

            Button {
                model.openPicker(.payment)
            } label: {
                GroupedRow(
                    titleKey: "settings_default_payment",
                    symbolName: "creditcard.fill",
                    symbolTint: MageRideColor.secondary
                ) {
                    RowChevron(value: PaymentRails.labelKey(model.state.defaultPayment).localised)
                }
            }
            .buttonStyle(.plain)

            GroupedRow(
                titleKey: "settings_notifications",
                symbolName: "bell.fill",
                symbolTint: MageRideColor.error,
                showsSeparator: false
            ) {
                // The row itself is not tappable: the switch is the control, and a row that also
                // toggled would fire twice for one thumb.
                Toggle("", isOn: notificationsBinding)
                    .labelsHidden()
                    .disabled(model.state.isLoading)
            }
        }
    }

    private var notificationsBinding: Binding<Bool> {
        Binding(
            get: { model.state.notificationsEnabled },
            set: { enabled in Task { await model.setNotifications(enabled) } }
        )
    }

    private var pickerBinding: Binding<SettingsPicker?> {
        Binding(
            get: { model.state.picker },
            set: { picker in if picker == nil { model.dismissPicker() } }
        )
    }

    private var deleteBinding: Binding<Bool> {
        Binding(
            get: { model.state.isConfirmingDelete },
            set: { presented in if !presented { model.dismissDelete() } }
        )
    }
}

/// The two value rows' choosers.
///
/// **A `.sheet` rather than the Android `AlertDialog`**, and it is one sheet rather than two: both
/// are *"pick one of a short list"*, the cell draws a grouped list for every choice on this surface,
/// and a dialog holding three tappable boxes is a Material shape. `SelectionRow` is C095's — the same
/// control SCR-PI-002 draws the language on, which is the point of a passenger coming back to
/// correct that choice recognising it.
private struct SettingsPickerSheet: View {

    let picker: SettingsPicker
    let language: Language
    let payment: PaymentMethod
    let onLanguage: (Language) -> Void
    let onPayment: (PaymentMethod) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
            Text(key: picker == .language ? "settings_language" : "settings_default_payment")
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)

            if picker == .language {
                ForEach(LanguageDisplay.choices, id: \.self) { choice in
                    SelectionRow(
                        label: LanguageDisplay.endonym(choice),
                        secondary: LanguageDisplay.englishName(choice),
                        isSelected: choice == language
                    ) {
                        onLanguage(choice)
                    }
                }
            } else {
                // Keyed on the rail's own wire value, exactly as ``PaymentMethodScreen`` keys its
                // list — a `\.self` over a Kotlin enum leans on an `NSObject` `Hashable` conformance
                // that neither host has exercised, and the wire string is the identity anyway.
                ForEach(PaymentRails.preferable, id: \.wire) { method in
                    SelectionRow(
                        label: PaymentRails.labelKey(method).localised,
                        secondary: PaymentRails.captionKey(method).localised,
                        isSelected: method == payment
                    ) {
                        onPayment(method)
                    }
                }
            }

            Spacer(minLength: 0)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.top, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.lg)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(MageRideColor.surface)
        .presentationDetents([.height(MageRideControl.pickerSheetHeight), .medium])
        .presentationDragIndicator(.visible)
    }
}
