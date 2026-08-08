import Foundation
import MageRideShared

/// Who is signed in, for the two screens that draw the same card.
///
/// SCR-PI-033's identity card and SCR-PI-027's profile card are the same three values — a name, an
/// id and a phone number — and they are both reachable in one session on the **same** tab stack. A
/// holder is what stops that being two `GET /v1/users/me` calls, and what makes a rename on
/// SCR-PI-027b change the Menu card behind it without a third.
///
/// **One read, shared, and refreshed by whoever changed it.** SCR-PI-027 and SCR-PI-027b both load
/// the profile anyway and hand the result over (``adopt(_:)``); ``refresh(force:)`` is what the Menu
/// card asks for when it has nothing at all. A card that re-fetched on every appear would cost a
/// request each time a passenger tapped the Menu tab.
///
/// **A failure is swallowed and leaves the card brand-only**, which is what it draws before the
/// first read: a menu that showed an error where a name goes would put a failed request in front of
/// navigation the passenger opened the Menu to use.
///
/// `apps/passenger-android`'s twin is a Koin `single` for a stronger reason — SCR-PA-033 is a
/// **drawer** the shell hosts above every screen, so its header genuinely cannot own a fetch. Here
/// the Menu is a tab destination and could have; it does not, because SCR-PI-027b's save has to
/// reach it and a screen cannot publish into another screen's model.
@MainActor
final class PassengerIdentity: ObservableObject {

    /// The signed-in passenger, or `nil` before the first successful read.
    @Published private(set) var profile: UserProfile?

    private let profiles: PassengerProfileRepository

    init(profiles: PassengerProfileRepository) {
        self.profiles = profiles
    }

    /// Takes a profile a screen has already read.
    func adopt(_ profile: UserProfile) {
        self.profile = profile
    }

    /// Reads `GET /v1/users/me` if nothing has been read yet.
    func refresh(force: Bool = false) async {
        guard force || profile == nil else { return }
        profile = try? await profiles.me()
    }

    /// Drops the profile — the session it belonged to has ended.
    ///
    /// Called from two places and both are needed: SCR-PI-027's *Log out*, which is the deliberate
    /// exit, and ``PassengerShellModel``'s `RouteToLogin` collector, which is every other way a
    /// session can end (a failed refresh, `403 device-revoked`, PDPA erasure).
    func clear() {
        profile = nil
    }
}
