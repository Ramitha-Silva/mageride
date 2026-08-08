import Foundation
import MageRideShared

/// Which of SCR-PI-027's two value rows is open as a chooser.
enum SettingsPicker: String, Identifiable {
    case language
    case payment

    var id: String { rawValue }
}

/// SCR-PI-027's state.
struct SettingsState {

    /// The card at the top — name, id and phone. `nil` until the read lands.
    var profile: UserProfile?

    /// The 🌐 row's value, and what the app is drawing in.
    var language: Language = LanguageDisplay.default

    /// The 💳 row's value — the rail the **next** booking starts with.
    var defaultPayment: PaymentMethod = PaymentMethod.cash

    var notificationsEnabled = true
    var isLoading = true
    var isBusy = false

    var picker: SettingsPicker?
    var isConfirmingDelete = false

    /// The PDPA erasure request `DELETE /v1/users/me` accepted, which is what the screen shows
    /// instead of claiming the account is gone (E-06).
    var deletionRequestId: String?

    var errorKey: String?

    /// `ID: 01J… · +94 77 123 4567`, or `nil` before the profile lands.
    var identityLine: String? {
        profile.map { "settings_identity".localisedFormat($0.userId, $0.phone) }
    }
}

/// SCR-PI-027 — *"Profile & settings"*.
///
/// The wireframe's rows in order: the profile card (→ SCR-PI-027b), then a `glist` of 🌐 **Language**,
/// ★ **Save Home & Work**, 💳 **Default payment** and 🔔 **Notifications**; then 💬 **Help &
/// support**; then **Log out** and **Delete account**.
///
/// **Language is here and on SCR-PI-002, and nowhere else** (AL-26). This is one of the two screens
/// the change set left it on; ``PassengerProfileRepository/update(name:notifPrefs:)`` — what
/// SCR-PI-027b saves through — has no language parameter at all, so the fence is structural rather
/// than remembered.
///
/// **A language change takes effect immediately and nothing is re-created** (Δ Section C).
/// ``PassengerLocale/apply(_:)`` re-points the bundle every lookup goes through and the next view
/// built resolves against it; the Android twin calls `Activity.recreate()` because
/// `attachBaseContext` has already run by the time a composable exists, and this platform has no
/// equivalent and needs none. The *local* write comes first and is not conditional on the call — a
/// passenger who chose Tamil on a train with no signal asked for a Tamil app — and
/// `languagePendingSync` is left set for C095's next authenticated pass if the server write fails.
///
/// **Default payment is two writes, and one of them may not be possible.** The device always
/// remembers (``AppPreferences/rememberRail(_:)``); `iam.users.default_payment_method` is written
/// only when the chosen rail has a value in an enum that predates AL-57 — see
/// ``PaymentRails/storedValueOf(_:)``. A wallet default is honoured here and does not follow the
/// passenger to a second handset, which is a contract gap and not a design.
@MainActor
final class SettingsModel: ObservableObject {

    @Published private(set) var state = SettingsState()

    private let profiles: PassengerProfileRepository
    private let identity: PassengerIdentity
    private let preferences: AppPreferences
    private let sessions: PassengerSessions

    init(
        profiles: PassengerProfileRepository,
        identity: PassengerIdentity,
        preferences: AppPreferences,
        sessions: PassengerSessions
    ) {
        self.profiles = profiles
        self.identity = identity
        self.preferences = preferences
        self.sessions = sessions
        state.language = preferences.language ?? LanguageDisplay.default
        state.defaultPayment = preferences.preferredRail
    }

    /// Reads `GET /v1/users/me`. What `.task` and `.refreshable` both call.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil

        let profile: UserProfile
        do {
            profile = try await profiles.me()
        } catch is CancellationError {
            return
        } catch {
            // The rows still render from what the device knows — the language it is drawing in and
            // the rail it will book with are both local facts.
            state.isLoading = false
            state.defaultPayment = preferences.preferredRail
            state.errorKey = SettingsErrors.messageKey(for: error)
            return
        }

        // SCR-PI-033's card reads the same profile — see ``PassengerIdentity``. Handing it over is
        // what stops the Menu tab making a second `GET /v1/users/me` of its own.
        identity.adopt(profile)
        // US-22.6: the account's stored rail seeds a handset that has none of its own, and loses to
        // one that has.
        preferences.adoptRail(profile.defaultPaymentMethod)

        state.profile = profile
        state.language = profile.language ?? state.language
        state.defaultPayment = preferences.preferredRail
        state.notificationsEnabled = PassengerNotificationPreferences.marketingEnabled(profile.notifPrefs)
        state.isLoading = false
    }

    func clearError() {
        state.errorKey = nil
    }

    func openPicker(_ picker: SettingsPicker) {
        state.picker = picker
    }

    func dismissPicker() {
        state.picker = nil
    }

    /// The 🌐 row (D-26, AL-26).
    ///
    /// The device is written first and unconditionally; the server copy is what every SMS and
    /// server-rendered string is written in, and it can be corrected on the next visit. The two
    /// answer different questions, so a failure of one is not a reason to abandon the other.
    func chooseLanguage(_ language: Language) async {
        guard language != state.language else {
            state.picker = nil
            return
        }

        preferences.language = language
        preferences.languagePendingSync = true
        // Before the state change, so the re-render this publishes already resolves in the new
        // language rather than one frame behind it.
        PassengerLocale.apply(language)

        state.language = language
        state.picker = nil
        state.isBusy = true
        state.errorKey = nil

        do {
            _ = try await profiles.saveLanguage(language)
            preferences.languagePendingSync = false
            state.isBusy = false
        } catch is CancellationError {
            state.isBusy = false
        } catch {
            // The app still changes language — see the class note. `languagePendingSync` is left
            // set, so C095's `OnboardingRepository` pushes it on the next authenticated pass.
            state.isBusy = false
            state.errorKey = SettingsErrors.messageKey(for: error)
        }
    }

    /// The 💳 row (US-22.4, AL-14).
    ///
    /// The device is told unconditionally, because that is what the next booking reads; the account
    /// is told when the contract can express the choice. See the class note.
    func chooseDefaultPayment(_ method: PaymentMethod) async {
        preferences.rememberRail(method)
        state.defaultPayment = method
        state.picker = nil
        state.errorKey = nil

        guard let stored = PaymentRails.storedValueOf(method) else { return }

        do {
            _ = try await profiles.saveDefaultPaymentMethod(stored)
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = SettingsErrors.messageKey(for: error)
        }
    }

    /// The 🔔 row (US-10.7).
    ///
    /// The switch flips first and is put back if the call fails: a toggle that waited for a round
    /// trip would feel broken on a Sri Lankan mobile network, and one that stayed flipped after a
    /// failure would be lying about what the account holds.
    func setNotifications(_ enabled: Bool) async {
        state.notificationsEnabled = enabled
        state.isBusy = true
        state.errorKey = nil

        do {
            let saved = try await profiles.update(
                name: nil,
                // The whole switch map, keys this build has never heard of included — see
                // ``PassengerNotificationPreferences``.
                notifPrefs: PassengerNotificationPreferences.withMarketing(
                    state.profile?.notifPrefs,
                    enabled: enabled
                )
            )
            identity.adopt(saved)
            state.profile = saved
            state.isBusy = false
        } catch is CancellationError {
            state.isBusy = false
        } catch {
            state.isBusy = false
            state.notificationsEnabled = !enabled
            state.errorKey = SettingsErrors.messageKey(for: error)
        }
    }

    /// *Log out* (US-1.7).
    ///
    /// **Nothing here navigates.** ``PassengerSessions/logOut()`` raises C014's `RouteToLogin`, and
    /// ``PassengerShellModel`` is its single subscriber — one path out for a deliberate logout, a
    /// failed refresh and a revoked device alike. The identity is cleared here as well as there
    /// because this is the one exit the passenger *chose*, and the card must not still be showing
    /// their name while the stack unwinds.
    func logOut() async {
        identity.clear()
        await sessions.logOut()
    }

    /// *Delete account* — the tap that opens the alert. Nothing has happened yet.
    func confirmDelete() {
        state.isConfirmingDelete = true
    }

    func dismissDelete() {
        state.isConfirmingDelete = false
    }

    /// `DELETE /v1/users/me` (US-1.8, PDPA).
    ///
    /// **Accepted, not done.** The `202` hands the request to pdpa-svc, where a statutory hold can
    /// delay it (E-06), so the screen reports a *request* — and the session is deliberately left
    /// alone: signing the passenger out here would claim an erasure that has not happened and would
    /// take away the only surface that can tell them when it has.
    func deleteAccount() async {
        state.isConfirmingDelete = false
        state.isBusy = true
        state.errorKey = nil

        do {
            state.deletionRequestId = try await profiles.deleteAccount()
            state.isBusy = false
        } catch is CancellationError {
            state.isBusy = false
        } catch {
            state.isBusy = false
            state.errorKey = SettingsErrors.deletionMessageKey(for: error)
        }
    }
}
