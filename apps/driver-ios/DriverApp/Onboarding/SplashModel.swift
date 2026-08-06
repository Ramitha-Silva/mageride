import Foundation

/// SCR-DI-001 — the boot router.
///
/// Three questions, in order, and only as many as the answer needs:
///
/// 1. **Has this install chosen a language and a city?** Local, instant (`UserDefaults`).
/// 2. **Is there a session?** `AuthSessionManager.restore()` — a Keychain read, no network.
/// 3. **Does this driver have a profile?** `GET /v1/users/me`, and *only* when signed in.
///
/// The splash is the one screen allowed to block on that, and it blocks on the smallest thing that
/// can answer the question — which is why the third is skipped entirely for a signed-out driver.
@MainActor
final class SplashModel: ObservableObject {

    /// Where to go. `nil` while the splash is still deciding — the wireframe's spinner.
    @Published private(set) var destination: OnboardingDestination?

    private let sessions: DriverSessions
    private let profiles: DriverProfileRepository
    private let preferences: OnboardingPreferences

    init(sessions: DriverSessions, profiles: DriverProfileRepository, preferences: OnboardingPreferences) {
        self.sessions = sessions
        self.profiles = profiles
        self.preferences = preferences
    }

    /// Answers the three questions. Idempotent — SwiftUI may run a `.task` again after a scene
    /// change, and routing twice would replace the root the driver has already moved on from.
    func route() async {
        guard destination == nil else { return }

        await sessions.restore()
        let signedIn = sessions.isSignedIn

        destination = OnboardingRouter.next(
            signedIn: signedIn,
            firstRunComplete: preferences.firstRunComplete,
            profileComplete: signedIn && (await hasProfile()),
            permissionsAcknowledged: preferences.permissionsAcknowledged
        )
    }

    /// Whether `registry.driver_profiles` already has a name for this driver (US-2.21).
    ///
    /// **A failed call answers `true`, and that is deliberate.** This runs on a session that was
    /// restored from the Keychain, so the driver has signed in before and has therefore already
    /// been through Profile Setup — the screen cannot be reached without it. Answering `false` on a
    /// flat tunnel would put a working driver back on an onboarding form; answering `true` lands
    /// them on the dashboard, which has the offline banner and can retry everything. A genuinely
    /// new account never takes this path: the login screen computes its own destination from the
    /// verify it just made.
    /// `do`/`catch` rather than `try?`: SE-0230 flattens `try?` on an optional-returning call, so a
    /// driver whose profile genuinely has no name would be indistinguishable from a call that
    /// failed — and the two answers here are opposites.
    private func hasProfile() async -> Bool {
        do {
            let name = try await profiles.profileName()
            return !(name?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
        } catch {
            return true
        }
    }
}
