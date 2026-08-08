import Foundation
import MageRideShared

/// `iam.users` and its preferences, as this app uses them.
///
/// Two clusters read it and there is deliberately only one of it: C095's first run — SCR-PI-004 and
/// the profile check the splash and the login screen each make — and C101's SCR-PI-027/027b, which
/// edits the same row from the other end of the app's life. A second seam onto `/v1/users/me` would
/// be two places that decide what `notif_prefs` looks like.
///
/// **The `PUT` is split in two on purpose.** ``save(name:language:notificationsEnabled:)`` is the
/// first-run call and carries a language, because SCR-PI-004 draws a Language row;
/// ``update(name:notifPrefs:)`` is Settings' and **has no language parameter at all**, which is
/// AL-26's *"language selection removed from Edit-profile"* made structural rather than left to a
/// screen to remember. The language has its own route — ``saveLanguage(_:)`` — because SCR-PI-027
/// changes it on its own and a whole-profile `PUT` to move one field would race whatever else the
/// screen had loaded.
protocol PassengerProfileRepository: AnyObject {

    /// `GET /v1/users/me`. Throws on any failure — every caller decides what a failure means, and
    /// the splash and the login screen decide **differently**. See ``SplashModel`` and ``LoginModel``.
    func me() async throws -> UserProfile

    /// `PUT /v1/users/me` — SCR-PI-004's save.
    @discardableResult
    func save(name: String, language: Language, notificationsEnabled: Bool) async throws -> UserProfile

    /// `PUT /v1/users/me` — SCR-PI-027b's *Save*. **No language, by construction** (AL-26).
    @discardableResult
    func update(name: String?, notifPrefs: [String: Bool]?) async throws -> UserProfile

    /// `PUT /v1/me/prefs/language` — SCR-PI-027's Language row (D-26, AL-26).
    @discardableResult
    func saveLanguage(_ language: Language) async throws -> Language

    /// `PUT /v1/me/prefs/payment-method` — SCR-PI-027's Default payment row (US-22.4, AL-14).
    ///
    /// Takes the **stored** enum rather than a rail, because not every rail has one: see
    /// ``PaymentRails/storedValueOf(_:)``, which is what decides whether this call can be made at
    /// all. Δ C101.
    @discardableResult
    func saveDefaultPaymentMethod(_ method: DefaultPaymentMethod) async throws -> DefaultPaymentMethod

    /// `DELETE /v1/users/me` — SCR-PI-027's *Delete account* (US-1.8, PDPA). Δ C101.
    ///
    /// **`202`, not `204`.** The account is not gone when this returns: the request becomes a
    /// pdpa-svc erasure request that a statutory hold can delay (E-06), and the returned id is what
    /// `GET /v1/pdpa/{requestId}` tracks. The screen says so — telling a passenger their account is
    /// deleted when it is queued is the one thing that dialog must not do.
    @discardableResult
    func deleteAccount() async throws -> String
}

/// ``PassengerProfileRepository`` over `IamApi`.
final class ApiPassengerProfileRepository: PassengerProfileRepository {

    private let iam: IamApi

    init(iam: IamApi) {
        self.iam = iam
    }

    func me() async throws -> UserProfile {
        try await iam.getMyProfile()
    }

    /// **Every field the screen owns goes in one call.** `UpdateProfileRequest` is all-optional, so
    /// a partial save is expressible and would be the wrong thing: the wireframe's *Save* is one
    /// action, and three calls would leave a passenger who lost signal halfway with a name and no
    /// language.
    ///
    /// `photoUrl` is deliberately **not** a parameter. D2' §SCR-PI-004 names `PhotosPicker` and the
    /// field is a *URL*, but nothing on the app-facing surface mints one for a passenger —
    /// `POST /v1/support/screenshots` and the driver's document routes are the only uploads in the
    /// sixteen contracts. A parameter with no way to fill it is an invitation to send `nil` over a
    /// value the server already holds. C077 recorded the same gap; see ``ProfileAvatar``.
    ///
    /// - Parameter notificationsEnabled: The wireframe's *"Notifications & offers"* switch. Sent as
    ///   the whole `notifPrefs` map under one key rather than as a boolean field, because that is
    ///   the shape `iam.users.notif_prefs` has — and US-10.7 is **opt-out**, so an absent key reads
    ///   as on. Writing the key explicitly is what makes turning it *off* stick.
    @discardableResult
    func save(name: String, language: Language, notificationsEnabled: Bool) async throws -> UserProfile {
        try await iam.updateMyProfile(
            request: UpdateProfileRequest(
                firstName: name.trimmingCharacters(in: .whitespacesAndNewlines),
                photoUrl: nil,
                language: language,
                notifPrefs: [PassengerNotificationPreferences.marketing: KotlinBoolean(value: notificationsEnabled)]
            )
        )
    }

    /// - Parameter notifPrefs: The **whole** switch map, keys this build has never heard of
    ///   included. The set of notification types grows without a contract change, so a caller that
    ///   sent only the keys it knows would silently re-enable a type a newer build had muted.
    @discardableResult
    func update(name: String?, notifPrefs: [String: Bool]?) async throws -> UserProfile {
        try await iam.updateMyProfile(
            request: UpdateProfileRequest(
                firstName: name?.trimmingCharacters(in: .whitespacesAndNewlines),
                photoUrl: nil,
                language: nil,
                notifPrefs: notifPrefs.map { $0.mapValues { KotlinBoolean(value: $0) } }
            )
        )
    }

    /// **The server's copy only.** The device's copy is what ``PassengerLocale`` redirects the
    /// bundle from, and writing it is the caller's — a repository that wrote the preference without
    /// re-pointing the bundle would leave the app rendering in the old language with the new one
    /// stored.
    @discardableResult
    func saveLanguage(_ language: Language) async throws -> Language {
        try await iam.setLanguagePreference(request: LanguagePreference(language: language)).language
    }

    @discardableResult
    func saveDefaultPaymentMethod(_ method: DefaultPaymentMethod) async throws -> DefaultPaymentMethod {
        try await iam
            .setDefaultPaymentMethod(request: DefaultPaymentMethodPreference(defaultPaymentMethod: method))
            .defaultPaymentMethod
    }

    @discardableResult
    func deleteAccount() async throws -> String {
        try await iam.deleteMyAccount().requestId
    }
}

/// The `iam.users.notif_prefs` keys this app writes.
enum PassengerNotificationPreferences {

    /// The key behind SCR-PI-004's one switch.
    ///
    /// The wireframe offers a single *"Notifications & offers"* toggle rather than the per-type list
    /// SCR-PI-027b draws, so it maps to one key. **Nothing safety-critical is behind it**:
    /// `RIDE_CANCELLED`, `SOS_TRIGGERED` and `SOS_RESOLVED` are three iam-svc refuses to store a
    /// preference for at all, so they cannot be muted here or anywhere.
    static let marketing = "MARKETING"

    /// Whether the switch reads as on. US-10.7 is **opt-out**, so an absent key is on — which is
    /// what makes a profile nobody has touched round-trip to *enabled* rather than to a mute.
    static func marketingEnabled(_ existing: [String: KotlinBoolean]?) -> Bool {
        existing?[marketing]?.boolValue ?? true
    }

    /// The switch map with *"Notifications & offers"* set, and **every other key left alone**
    /// (Δ C101).
    ///
    /// The set of notification types grows without a contract change, so a caller that sent only the
    /// keys it knows would silently re-enable a type a newer build had muted. `nil` and an absent
    /// key both mean *"never said otherwise"*, which US-10.7 makes **on** — so a profile nobody has
    /// touched still round-trips to `{MARKETING: true}` rather than to an empty map that would read
    /// as a mute.
    ///
    /// It answers `[String: Bool]` because that is what ``PassengerProfileRepository/update(name:notifPrefs:)``
    /// takes: the boxing back into `KotlinBoolean` happens once, inside the repository, so no screen
    /// in this app ever constructs a boxed Kotlin primitive.
    static func withMarketing(_ existing: [String: KotlinBoolean]?, enabled: Bool) -> [String: Bool] {
        var prefs = (existing ?? [:]).mapValues(\.boolValue)
        prefs[marketing] = enabled
        return prefs
    }
}
