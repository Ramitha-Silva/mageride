import Foundation
import MageRideShared

/// SCR-DI-036's header (Δ MCS-24).
///
/// The Menu tab used to draw a generic *"driver"* label above the platform id, and the reason was
/// recorded and was a good one: this component did not own the profile read, so *"a wrong name is
/// worse than none and an invented rating is worse still"*. That reasoning survives — the rating is
/// still drawn only if one exists, and none does — but the premise does not. This model owns the
/// read now, so the name, the level and the live vehicle's plate are the tab's to show.
///
/// **Three reads, and none of them blocks the menu.** The rows are the point of the screen and they
/// are static, so the header fills in behind them; a failure leaves it on its defaults rather than
/// putting an error over a list of links that all still work.
@MainActor
final class MenuModel: ObservableObject {

    @Published private(set) var header = DriverHeaderState()

    private let identity: DriverIdentity
    private let profiles: ProfileRepository

    init(identity: DriverIdentity, profiles: ProfileRepository) {
        self.identity = identity
        self.profiles = profiles
    }

    /// What the handset already knows, drawn before a single call is made (§3.16, Δ MCS-27).
    ///
    /// The tab used to open on a placeholder and fill in a second later, every time, on whatever
    /// connection the driver happened to have. This is that second.
    func paintFromCache() async {
        guard let driverId = identity.driverId,
              let cached = await profiles.cachedProfile(driverId: driverId),
              !cached.isEmpty
        else { return }

        // A read that has already answered outranks the cache — on a fast connection it genuinely
        // can arrive first.
        header = DriverHeaderState(
            name: header.name ?? cached.name,
            level: header.level ?? cached.level?.int32Value,
            registration: header.registration ?? cached.registration,
            rating: header.rating,
            photo: header.photo ?? cached.photoBytes.map { IosBytesKt.nsDataOf(bytes: $0) as Data })
    }

    func load() async {
        // Deliberately silent on failure. A header that could not load is a header with no name in
        // it; an error banner over eight working links would be worse than the blank.
        let profile = try? await profiles.profile()
        let live = try? await identity.liveVehicle().live

        var standing: JobStanding?
        if let driverId = identity.driverId {
            standing = await profiles.standing(driverId: driverId)
        }

        if let driverId = identity.driverId {
            await profiles.cacheIdentity(
                driverId: driverId,
                name: profile?.firstName,
                level: standing?.standing?.level?.int32Value,
                registration: live?.registrationNumber)
        }

        header = DriverHeaderState(
            name: profile?.firstName,
            level: standing?.standing?.level,
            registration: live?.registrationNumber,
            // No app-facing read carries a driver's own star average. See ``DriverHeaderState``
            // for the four places it is not.
            rating: nil,
            photo: header.photo
        )
    }
}

extension MenuModel {

    /// The avatar, on its own task (Δ MCS-25/27) — a slow photograph must not hold the rows back.
    func loadPhoto() async {
        guard let driverId = identity.driverId,
              let bytes = await profiles.driverPhoto(driverId: driverId)
        else { return }

        header.photo = bytes
    }
}
