import Foundation
import MageRideShared

/// SCR-DI-029's data — the profile, its preferences, its emergency contact and the way out.
///
/// **One `GET /v1/users/me` and one `GET /v1/me/emergency-contacts`.** `GET /v1/me/bootstrap` would
/// answer both in one round trip (AL-14) and is deliberately not used here: it also carries the
/// addresses, the payment methods, any trip in flight and the whole RBAC matrix, and this screen is
/// opened by a driver who wants to change their emergency contact — paying for the eager-fetch payload
/// on every visit to a settings screen is the opposite of what it is for.
///
/// **The level comes from ``JobsRepository``, not from a second read of its own** (C090). The wireframe
/// draws an `L3 ›` on the Driver Level row and that is `GET /v1/drivers/{id}/level`; one repository
/// owning that call is what stops two screens disagreeing about a driver's level.
///
/// A protocol for the reason every seam in this target is one: `IamApi` is a Kotlin interface and
/// `AuthSessionManager` a Kotlin class, and neither can be stood in for from Swift.
protocol ProfileRepository: AnyObject {

    /// `GET /v1/users/me` — name, photo, language and the notification switch map.
    func profile() async throws -> UserProfile

    /// `GET /v1/drivers/profile` — the driver's own photograph, absolute and ready to load
    /// (Δ MCS-25).
    ///
    /// **Not ``profile()``'s `photoUrl`.** That is `iam.users.photo_url`, and D3'
    /// §`getDriverProfile` records that Profile Setup *"writes `registry.driver_profiles` and never
    /// touches `iam.users`"* — so for a driver who onboarded in this app it is always `nil`, and the
    /// avatar it fed was never going to render whatever loader was put behind it.
    ///
    /// Non-throwing, like ``standing(driverId:)``: a header with no picture is a header, and an
    /// error banner over a working screen because an avatar did not load would be worse than the
    /// glyph.
    func driverPhotoUrl() async -> String?

    /// What the headers should draw right now, before a single call has answered (§3.16, Δ MCS-27).
    func cachedProfile(driverId: String) async -> CachedDriverProfile?

    /// Records what a completed read answered, so the next open has it.
    func cacheIdentity(driverId: String, name: String?, level: Int32?, registration: String?) async

    /// The driver's photograph as bytes — off disk when they are current, from the network when not.
    func driverPhoto(driverId: String) async -> Data?

    /// `GET /v1/me/emergency-contacts` — who D-33's SOS SMS goes to (AL-13).
    func emergencyContacts() async throws -> [EmergencyContact]

    /// The level and the US-6A.14 counters behind the `L3 ›` row.
    func standing(driverId: String) async -> JobStanding

    /// `PUT /v1/users/me` — the display name behind the header's ✎.
    func saveName(_ name: String) async throws -> UserProfile

    /// `PUT /v1/users/me` with the whole switch map (US-10.7).
    ///
    /// **The map is sent back in full, keys this build has never heard of included.** The event list
    /// *"grows without a contract change"*, so an app that sent only the keys it knows would silently
    /// re-enable a type a newer build had muted. ``DriverNotificationGroup/applied(to:isEnabled:)``
    /// adds to what was read rather than replacing it, and this passes the result through.
    func saveNotificationPreferences(_ preferences: [String: Bool]) async throws -> UserProfile

    /// `PUT /v1/me/prefs/language` **and** the local store (D-26, AL-26).
    func saveLanguage(_ language: Language) async throws

    /// The language the app is currently rendering in, or `nil` before SCR-DI-002 was answered.
    func storedLanguage() -> Language?

    /// Stores the emergency contact SCR-DI-032's SOS sends to (AL-13, D-33).
    ///
    /// **One contact, replaced rather than accumulated.** The wireframe's row is singular — *"Amma ·
    /// +94 77 000 1111"* — and `EmergencyContact.isPrimary` is *"exactly one per account that has
    /// any"*, because the SOS budget is p99 ≤ 5 s and the primary is denormalised onto
    /// `iam.users.emergency_contact_name/phone` for it. So an existing contact is **updated in place**
    /// and only a driver with none creates one; adding a second would leave the SOS fast path pointing
    /// at whichever the server had already denormalised.
    func saveEmergencyContact(
        existing: EmergencyContact?,
        name: String,
        phone: String
    ) async throws -> EmergencyContact

    /// `POST /v1/auth/logout` — end this device's session (US-1.7).
    func logOut() async
}

/// ``ProfileRepository`` over `:shared`'s iam client, C090's jobs reads and C014's session.
final class ApiProfileRepository: ProfileRepository {

    private let iam: IamApi
    private let jobs: JobsRepository
    private let sessions: DriverSessions
    private let preferences: OnboardingPreferences
    private let registry: RegistryApi
    private let gatewayOrigin: String
    private let databases: DriverDatabase

    init(
        iam: IamApi,
        jobs: JobsRepository,
        sessions: DriverSessions,
        preferences: OnboardingPreferences,
        registry: RegistryApi,
        gatewayOrigin: String,
        databases: DriverDatabase
    ) {
        self.iam = iam
        self.jobs = jobs
        self.sessions = sessions
        self.preferences = preferences
        self.registry = registry
        self.gatewayOrigin = gatewayOrigin
        self.databases = databases
    }

    func cachedProfile(driverId: String) async -> CachedDriverProfile? {
        guard let cache = await cache() else { return nil }

        return cache.read(driverId: driverId)
    }

    func cacheIdentity(driverId: String, name: String?, level: Int32?, registration: String?) async {
        guard let cache = await cache() else { return }

        cache.writeIdentity(
            driverId: driverId,
            name: name,
            level: level.map { KotlinInt(int: $0) },
            registration: registration,
            at: nowInstant())
    }

    /// **Bytes rather than a URL, which is the whole point of §3.16.** The signed link carries an
    /// `expires` that changes on every profile read, so a URL-keyed image cache misses every time
    /// and re-downloads a photograph the handset already holds. The link's `v` says *which* photo it
    /// is; when that has not changed nothing is fetched and the avatar paints from disk.
    func driverPhoto(driverId: String) async -> Data? {
        let cache = await cache()

        guard let link = (try? await registry.getDriverProfile())?.photoUrl else {
            return cache?.read(driverId: driverId).photoBytes.map(dataOf)
        }

        let query = signedLinkParameters(link)
        let version = query["v"]

        if let cache, !cache.needsPhoto(driverId: driverId, version: version) {
            return cache.read(driverId: driverId).photoBytes.map(dataOf)
        }

        guard let expires = query["expires"].flatMap(Int64.init),
              let signature = query["signature"],
              let bytes = try? await registry.getDriverProfilePhoto(
                  driverId: driverId, version: version ?? "", expires: expires, signature: signature)
        else { return nil }

        cache?.writePhoto(driverId: driverId, version: version, bytes: bytes, at: nowInstant())

        return dataOf(bytes)
    }

    private func cache() async -> DriverProfileCache? {
        guard let db = await databases.get() else { return nil }

        return DriverProfileCache(db: db)
    }

    func driverPhotoUrl() async -> String? {
        let profile = try? await registry.getDriverProfile()

        return absoluteUrl(gatewayOrigin: gatewayOrigin, path: profile?.photoUrl)
    }

    func profile() async throws -> UserProfile {
        try await iam.getMyProfile()
    }

    func emergencyContacts() async throws -> [EmergencyContact] {
        try await iam.listEmergencyContacts().items
    }

    func standing(driverId: String) async -> JobStanding {
        await jobs.standing(driverId: driverId)
    }

    func saveName(_ name: String) async throws -> UserProfile {
        try await iam.updateMyProfile(
            request: UpdateProfileRequest(
                firstName: name.trimmingCharacters(in: .whitespacesAndNewlines),
                photoUrl: nil,
                language: nil,
                notifPrefs: nil
            )
        )
    }

    func saveNotificationPreferences(_ preferences: [String: Bool]) async throws -> UserProfile {
        try await iam.updateMyProfile(
            request: UpdateProfileRequest(
                firstName: nil,
                photoUrl: nil,
                language: nil,
                // A Swift `[String: Bool]` crosses as a `Map<String, Boolean>`; the values box to
                // `KotlinBoolean`, which is what the generated initialiser asks for.
                notifPrefs: preferences.mapValues { KotlinBoolean(value: $0) }
            )
        )
    }

    /// Both halves, because they answer different questions: the server's copy is what every rendered
    /// template and SMS is written in, and the device's is what ``DriverLocale`` reads to redirect the
    /// app's own strings. The local write comes second — a driver whose language change failed at the
    /// gateway still gets the app they asked for.
    func saveLanguage(_ language: Language) async throws {
        _ = try await iam.setLanguagePreference(request: LanguagePreference(language: language))
        preferences.language = language
    }

    func storedLanguage() -> Language? { preferences.language }

    /// The number goes up in E.164 because that is what `EmergencyContactInput.phone` is and what
    /// safety-svc hands the SMS gateway.
    func saveEmergencyContact(
        existing: EmergencyContact?,
        name: String,
        phone: String
    ) async throws -> EmergencyContact {
        let input = EmergencyContactInput(name: name.trimmingCharacters(in: .whitespacesAndNewlines), phone: phone)
        guard let existing else {
            return try await iam.createEmergencyContact(request: input, idempotencyKey: nil)
        }
        return try await iam.updateEmergencyContact(contactId: existing.contactId, request: input)
    }

    /// Through ``DriverSessions`` rather than through `IamApi` directly: it is what holds the session
    /// (C014), and calling the route without telling it would leave a signed-out app whose
    /// `SessionState` still said `SignedIn` until the next 401. It also raises `RouteToLogin`, which
    /// ``DriverShellModel`` is the single subscriber to — so nothing on this screen navigates.
    func logOut() async {
        await sessions.logOut()
    }
}

/// Joins the gateway origin to a path a service handed back (Δ MCS-25).
///
/// Internal rather than private so `AbsoluteUrlTests` can hold the joining rules — the slashes are
/// the kind of thing that works against one deployment and breaks against the next.
///
/// An **absolute** URL is returned untouched. registry-svc answers a relative path today, but D-36
/// lets the same operation redirect to a presigned bucket URL, and a caller that blindly prefixed
/// the origin would turn that into a 404 against the gateway.
func absoluteUrl(gatewayOrigin: String, path: String?) -> String? {
    let trimmed = (path ?? "").trimmingCharacters(in: .whitespacesAndNewlines)

    guard !trimmed.isEmpty else { return nil }

    if trimmed.hasPrefix("http://") || trimmed.hasPrefix("https://") { return trimmed }

    var origin = gatewayOrigin
    while origin.hasSuffix("/") { origin.removeLast() }

    var relative = trimmed
    while relative.hasPrefix("/") { relative.removeFirst() }

    return "\(origin)/\(relative)"
}
