import Foundation
import MageRideShared

/// US-1.14's active-trip continuity, as one method.
///
/// **A seam rather than `RideApi` itself**, for the reason ``NearbySnapshots`` gives on the live
/// plane: `RideApi` is a Kotlin interface with dozens of `suspend` methods, implementing one from
/// Swift means matching whatever completion-handler selector Kotlin/Native generated for it, and the
/// splash needs exactly one read. A protocol that says so is a protocol nobody can quietly add a
/// second call to — and it is what makes ``SplashModel`` assertable with no gateway.
protocol ActiveRideLookup: AnyObject {

    /// `GET /v1/rides/passenger/{id}/active` — the ride this passenger is on, or `nil`.
    ///
    /// Answers `nil` on any failure as well as on "no ride": see the note at the foot of this file
    /// for why those two collapse here and do not on the profile read beside it.
    func activeRideId(passengerId: String) async -> String?
}

/// ``ActiveRideLookup`` over the real client.
final class ApiActiveRideLookup: ActiveRideLookup {

    private let rides: RideApi

    init(rides: RideApi) {
        self.rides = rides
    }

    func activeRideId(passengerId: String) async -> String? {
        guard let ride = try? await rides.getActivePassengerRide(passengerId: passengerId) else { return nil }
        return ride.rideId
    }
}

/// SCR-PI-001 — the boot router.
///
/// Three questions, in order, and only as many as the answer needs:
///
/// 1. **Has this install chosen a language?** Local, instant (`UserDefaults`).
/// 2. **Is there a session?** `AuthSessionManager.restore()` — a Keychain read, no network.
/// 3. **Does this passenger have a profile, and a ride in flight?** `GET /v1/users/me` and
///    `GET /v1/rides/passenger/{id}/active`, and *only* when signed in.
///
/// The splash is the one screen allowed to block on that, and it blocks on the smallest thing that
/// can answer the question — which is why steps 2 and 3 are skipped entirely for a first run.
@MainActor
final class SplashModel: ObservableObject {

    /// Where to go. `nil` while the splash is still deciding — the wireframe's spinner.
    @Published private(set) var route: PassengerRoute?

    private let sessions: PassengerSessions
    private let profiles: PassengerProfileRepository
    private let rides: ActiveRideLookup
    private let preferences: AppPreferences

    init(
        sessions: PassengerSessions,
        profiles: PassengerProfileRepository,
        rides: ActiveRideLookup,
        preferences: AppPreferences
    ) {
        self.sessions = sessions
        self.profiles = profiles
        self.rides = rides
        self.preferences = preferences
    }

    /// Idempotent: SwiftUI may run `.task` again after a scene change, and deciding twice would
    /// navigate twice.
    func decide() async {
        guard route == nil else { return }

        await sessions.restore()
        let signedIn = sessions.isSignedIn

        // Hoisted out of the argument list because `&&` takes its right-hand side as an
        // `@autoclosure`, which is not async — so `signedIn && (await hasProfile())` does not
        // compile. The short-circuit is preserved deliberately: `hasProfile()` is a network read and
        // a signed-out passenger has no profile to ask about.
        let profileComplete = signedIn ? await hasProfile() : false

        let destination = OnboardingRouter.next(
            signedIn: signedIn,
            firstRunComplete: preferences.firstRunComplete,
            profileComplete: profileComplete,
            locationAcknowledged: preferences.locationRationaleAcknowledged
        )

        // US-1.14's active-trip continuity, and the Android cell's own state line: *"resumes active
        // ride via GET /v1/rides/passenger/{id}/active"*. Only from the map — a passenger who still
        // owes a name or has never seen the location rationale is finishing those first, and the
        // ride is not going anywhere.
        guard destination == .liveMap, let passengerId = sessions.userId else {
            route = destination.route
            return
        }
        route = (await rides.activeRideId(passengerId: passengerId))
            .map { PassengerRoute.activeRide(rideId: $0) }
            ?? destination.route
    }

    /// Whether `iam.users` already has a name for this passenger (US-1.5).
    ///
    /// **A failed call answers `true`, and that is deliberate.** This runs on a session restored from
    /// the Keychain, so the passenger has signed in before and has therefore already been through
    /// Profile Setup — the screen cannot be reached without it. Answering `false` on a flat tunnel
    /// would put a working passenger back on an onboarding form; answering `true` lands them on the
    /// map, which has the offline banner and can retry everything. A genuinely new account never
    /// takes this path: ``LoginModel`` computes its own destination from the verify it just made,
    /// and answers the opposite way for the opposite reason.
    private func hasProfile() async -> Bool {
        guard let profile = try? await profiles.me() else { return true }
        return !(profile.firstName?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
    }
}

// **The two reads answer a failure differently and that is the point.** `hasProfile` guesses `true`,
// because guessing wrong strands a working passenger on an onboarding form; the active-ride lookup
// guesses `nil`, because guessing wrong puts somebody with no ride on a ride screen with nothing to
// show. Landing on the map instead of on the ride is recovered the moment SCR-PI-010's socket
// connects; neither of the other two mistakes is recovered at all.
