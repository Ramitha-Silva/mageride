package lk.mageride.driver.profile

import lk.mageride.driver.jobs.JobStanding
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.onboarding.OnboardingPreferences
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.EmergencyContactInput
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.UpdateProfileRequest
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.domain.auth.AuthSessionManager

/**
 * SCR-DA-029's data — the profile, its preferences, its emergency contact and the way out.
 *
 * **One `GET /v1/users/me` and one `GET /v1/me/emergency-contacts`.** `GET /v1/me/bootstrap` would
 * answer both in one round trip (AL-14) and is deliberately not used here: it also carries the
 * addresses, the payment methods, any trip in flight and the whole RBAC matrix, and this screen is
 * opened by a driver who wants to change their emergency contact — paying for the eager-fetch
 * payload on every visit to a settings screen is the opposite of what it is for.
 *
 * **The level comes from `JobsRepository`, not from a second read of its own** (C072). The wireframe
 * draws an `L3 ›` on the Driver Level row and that is `GET /v1/drivers/{id}/level`; one repository
 * owning that call is what stops two screens disagreeing about a driver's level.
 */
internal class ProfileRepository(
    private val iam: IamApi,
    private val jobs: JobsRepository,
    private val sessions: AuthSessionManager,
    private val preferences: OnboardingPreferences,
    private val registry: RegistryApi,
    private val gatewayOrigin: String,
) {

    /** `GET /v1/users/me` — name, photo, language and the notification switch map. */
    suspend fun profile(): UserProfile = iam.getMyProfile()

    /**
     * `GET /v1/drivers/profile` — the driver's own photograph (Δ MCS-25).
     *
     * **Not [profile]'s `photoUrl`, and that is the whole point of this method existing.** The
     * header used to read the one on `GET /v1/users/me`, which is `iam.users.photo_url` — and D3'
     * §`getDriverProfile` records that Profile Setup *"writes `registry.driver_profiles` and never
     * touches `iam.users`"*. So for a driver who onboarded in this app that field is **always
     * null**, and the avatar it fed was never going to render whatever loader was put behind it.
     * The photograph AL-27 required them to upload is the one registry-svc stored.
     *
     * Absolute, because a loader needs one. registry-svc answers a **relative** signed path for the
     * reason `TicketDetail.screenshotUrl` is relative — a service does not know the origin it is
     * reached on, and a response that could name one could name a different one. [DriverEnvironment]
     * is where this app's single gateway origin lives, and resolving here keeps every caller from
     * having to remember to.
     */
    suspend fun driverPhotoUrl(): String? =
        absoluteUrl(gatewayOrigin, registry.getDriverProfile()?.photoUrl)

    /** `GET /v1/me/emergency-contacts` — who D-33's SOS SMS goes to (AL-13). */
    suspend fun emergencyContacts(): List<EmergencyContact> = iam.listEmergencyContacts().items

    /** The level and the US-6A.14 counters behind the `L3 ›` row. */
    suspend fun standing(driverId: Ulid): JobStanding = jobs.standing(driverId)

    /** `PUT /v1/users/me` — the display name behind the header's ✎. */
    suspend fun saveName(name: String): UserProfile = iam.updateMyProfile(UpdateProfileRequest(firstName = name.trim()))

    /**
     * `PUT /v1/users/me` with the whole switch map (US-10.7).
     *
     * **The map is sent back in full, keys this build has never heard of included.** The event list
     * *"grows without a contract change"*, so an app that sent only the keys it knows would silently
     * re-enable a type a newer build had muted. [DriverNotificationGroup.applied] adds to what was
     * read rather than replacing it, and this passes the result through.
     */
    suspend fun saveNotificationPreferences(preferences: Map<String, Boolean>): UserProfile =
        iam.updateMyProfile(UpdateProfileRequest(notifPrefs = preferences))

    /**
     * `PUT /v1/me/prefs/language` **and** the local store (D-26, AL-26).
     *
     * Both, because they answer different questions: the server's copy is what every rendered
     * template and SMS is written in, and the device's is what `MainActivity.attachBaseContext`
     * reads through `DriverLocale.wrap` before a single composable runs. The local write comes
     * second — a driver whose language change failed at the gateway still gets the app they asked
     * for, and `OnboardingRepository.syncPreferences()` is not involved because there is a session
     * here and the call can simply be made.
     */
    suspend fun saveLanguage(language: Language): Language {
        iam.setLanguagePreference(LanguagePreference(language))
        preferences.language = language
        return language
    }

    /** The language the app is currently rendering in, or `null` before SCR-DA-002 was answered. */
    fun storedLanguage(): Language? = preferences.language

    /**
     * Stores the emergency contact SCR-DA-032's SOS sends to (AL-13, D-33).
     *
     * **One contact, replaced rather than accumulated.** The wireframe's row is singular — *"Amma ·
     * +94 77 000 1111"* — and `EmergencyContact.isPrimary` is *"exactly one per account that has
     * any"*, because the SOS budget is p99 ≤ 5 s and the primary is denormalised onto
     * `iam.users.emergency_contact_name/phone` for it. So an existing contact is **updated in
     * place** and only a driver with none creates one; adding a second would leave the SOS fast
     * path pointing at whichever the server had already denormalised.
     */
    suspend fun saveEmergencyContact(existing: EmergencyContact?, name: String, phone: PhoneE164): EmergencyContact {
        val input = EmergencyContactInput(name = name.trim(), phone = phone)
        return if (existing == null) {
            iam.createEmergencyContact(input)
        } else {
            iam.updateEmergencyContact(existing.contactId, input)
        }
    }

    /**
     * `POST /v1/auth/logout` — end this device's session.
     *
     * Through `AuthSessionManager` rather than through `IamApi` directly: it is what holds the
     * session (C014), and calling the route without telling it would leave a signed-out app whose
     * `SessionState` still said `SignedIn` until the next 401.
     */
    suspend fun logOut() {
        sessions.logout()
    }
}

/**
 * Joins the gateway origin to a path a service handed back (Δ MCS-25).
 *
 * `internal` rather than private so [ProfileRepositoryUrlTest] can hold the joining rules — the
 * slashes are the kind of thing that works against one deployment and breaks against the next.
 *
 * An **absolute** URL is returned untouched. registry-svc answers a relative path today, but D-36
 * lets the same operation redirect to a presigned bucket URL, and a caller that blindly prefixed
 * the origin would turn that into a 404 against the gateway.
 */
internal fun absoluteUrl(gatewayOrigin: String, path: String?): String? {
    val trimmed = path?.trim().orEmpty()

    if (trimmed.isEmpty()) return null

    if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) return trimmed

    return gatewayOrigin.trimEnd('/') + "/" + trimmed.removePrefix("/")
}
