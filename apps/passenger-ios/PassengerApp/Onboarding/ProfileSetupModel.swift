import Foundation
import MageRideShared

/// SCR-PI-004's state.
///
/// - Parameters:
///   - name: The wireframe's *Name* row. The one required field.
///   - language: The wireframe's own Language row — see the model's note on why it is here.
///   - notificationsEnabled: *"Notifications & offers"*. On by default (US-10.7 is opt-out).
///   - isLoaded: Whether the existing profile has been read; the form is disabled until it has.
///   - isBusy: A save is in flight.
///   - errorKey: Resolved copy for the last failure, or `nil`.
struct ProfileSetupState: Equatable {

    var name: String = ""
    var language: Language = LanguageDisplay.default
    var notificationsEnabled: Bool = true
    var isLoaded: Bool = false
    var isBusy: Bool = false
    var errorKey: String?

    /// A name with something in it. Trimmed, so spaces alone do not pass.
    var isNameValid: Bool { !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    /// *Save* is live when the one required field has a value.
    var canSubmit: Bool { !isBusy && isLoaded && isNameValid }
}

/// SCR-PI-004 — the first profile.
///
/// **The Language row belongs on THIS screen and not on Edit Profile.** AL-26 removes the language
/// selector from SCR-PI-027b, and that wireframe says so in as many words; what it leaves is
/// onboarding (SCR-PI-002) and Profile & settings (SCR-PI-027). SCR-PI-004 is neither — it is the
/// *first* profile, drawn once — and its own wireframe draws a Language field. Keeping it is what
/// lets a passenger who tapped the wrong box two screens ago fix it without hunting through
/// settings; the value is pre-filled from what SCR-PI-002 already stored, so leaving it alone is the
/// common path.
///
/// **No referral and no Aadhaar** — D2' §SCR-PA-004 drops both, and there is nowhere on
/// `UpdateProfileRequest` to put them. **No photo either**, and that is a contract gap rather than a
/// choice: see ``ProfileAvatar`` and ``PassengerProfileRepository/save(name:language:notificationsEnabled:)``.
@MainActor
final class ProfileSetupModel: ObservableObject {

    @Published private(set) var state = ProfileSetupState()

    /// Flips once the save lands; the screen navigates on it.
    @Published private(set) var isSaved = false

    private let profiles: PassengerProfileRepository
    private let preferences: AppPreferences

    init(profiles: PassengerProfileRepository, preferences: AppPreferences) {
        self.profiles = profiles
        self.preferences = preferences
        self.state.language = preferences.language ?? LanguageDisplay.default
    }

    /// Pre-fills from `GET /v1/users/me`. Idempotent — `.task` may run again after a scene change.
    ///
    /// **A failure still marks the form loaded**: an empty form a passenger can fill in is better
    /// than a spinner they cannot leave, and the save is a `PUT` that overwrites whatever is there
    /// regardless. The server's language wins over the local preference when it has one, because the
    /// account is the source of truth for a passenger signing in on a second handset.
    func load() async {
        guard !state.isLoaded else { return }

        guard let profile = try? await profiles.me() else {
            state.isLoaded = true
            return
        }
        state.name = profile.firstName ?? ""
        state.language = profile.language ?? state.language
        state.notificationsEnabled = PassengerNotificationPreferences.marketingEnabled(profile.notifPrefs)
        state.isLoaded = true
    }

    func onNameChanged(_ input: String) {
        state.name = input
        state.errorKey = nil
    }

    func onLanguageChanged(_ language: Language) {
        state.language = language
        state.errorKey = nil
    }

    func onNotificationsChanged(_ enabled: Bool) {
        state.notificationsEnabled = enabled
    }

    func dismissError() {
        state.errorKey = nil
    }

    /// *Save* — one `PUT /v1/users/me` with everything the screen owns.
    ///
    /// The chosen language is written to the local preference **as well** as sent, and the bundle is
    /// re-pointed with it: ``PassengerLocale`` is what every `Text(key:)` resolves through, so a
    /// passenger who changed the language here would otherwise keep the old one on screen until
    /// something else happened to write one.
    func submit() async {
        guard state.canSubmit else { return }

        state.isBusy = true
        state.errorKey = nil
        do {
            try await profiles.save(
                name: state.name,
                language: state.language,
                notificationsEnabled: state.notificationsEnabled
            )
            preferences.language = state.language
            // Already on the server, so nothing is pending for the next authenticated pass.
            preferences.languagePendingSync = false
            PassengerLocale.apply(state.language)
            isSaved = true
        } catch is CancellationError {
            // The screen going away, not a failure the passenger caused.
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }
}
