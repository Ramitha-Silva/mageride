import Foundation

/// SCR-PI-005's state.
///
/// - Parameters:
///   - authorisation: Where the grant stands. Re-read on every appearance — a passenger can change
///     it in Settings while the app is backgrounded.
///   - isAsking: The system dialog is up.
struct PermissionsState: Equatable {

    var authorisation: LocationAuthorisation = .notDetermined
    var isAsking: Bool = false

    /// Whether the primary control asks the system, or opens Settings.
    ///
    /// **The difference is not cosmetic.** After a refusal `requestWhenInUseAuthorization()` does
    /// nothing at all for the life of the install, so a CTA that still said *"Allow location"* would
    /// be a button that silently did nothing — which is worse than no button.
    var opensSettings: Bool { authorisation == .denied }

    /// Nothing left to ask: the grant is in hand, so the screen has only its way out.
    var isGranted: Bool { authorisation == .granted }
}

/// SCR-PI-005 — the location rationale.
///
/// **This screen gates nothing.** Both *"Allow location"* and *"Not now"* continue to the map, and
/// what is remembered is that the rationale was **shown** — never the grant, which belongs to the OS
/// and can be revoked from Settings at any moment. A screen that stored the grant would be a second
/// answer to a question `CLLocationManager` already answers, and one that *gated* on it would be a
/// screen with no way out for a passenger who said no.
///
/// C077 made the same three calls on Android. The Section C difference is only in why the CTA
/// changes: Android stops showing the dialog after two refusals, iOS after one.
@MainActor
final class PermissionsModel: ObservableObject {

    @Published private(set) var state = PermissionsState()

    private let permissions: LocationPermission
    private let preferences: AppPreferences

    init(permissions: LocationPermission, preferences: AppPreferences) {
        self.permissions = permissions
        self.preferences = preferences
    }

    /// Reads the current grant. Called on appearance **and** on every return from the background,
    /// because Settings is where a denial is undone and the app is not running while that happens.
    func refresh() {
        state.authorisation = permissions.authorisation
    }

    /// The primary control: asks the system, or opens Settings once asking has stopped working.
    ///
    /// Either way the screen is **done** — see ``acknowledge()``. A passenger who is sent to Settings
    /// and never comes back has still seen the rationale, and the map asks again when it needs a fix.
    func primaryAction() async {
        acknowledge()

        guard !state.opensSettings else {
            permissions.openSettings()
            return
        }

        state.isAsking = true
        state.authorisation = await permissions.request()
        state.isAsking = false
    }

    /// *"Not now"*. Records that the rationale was shown and lets the passenger through.
    func skip() {
        acknowledge()
    }

    /// Marks SCR-PI-005 as seen.
    ///
    /// **The rationale, not the grant.** ``OnboardingRouter`` reads this to decide whether the screen
    /// is still owed; storing the grant here instead would leave a passenger who later revoked it in
    /// Settings being sent back through onboarding on the next cold start.
    private func acknowledge() {
        preferences.locationRationaleAcknowledged = true
    }
}
