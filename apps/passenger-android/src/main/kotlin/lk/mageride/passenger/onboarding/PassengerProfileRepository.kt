package lk.mageride.passenger.onboarding

import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethodPreference
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.UpdateProfileRequest
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * `iam.users` and its preferences, as this app uses them.
 *
 * Two clusters read it and there is deliberately only one of it: C077's first run — SCR-PA-004 and
 * the profile check the splash and the login screen each make — and C083's SCR-PA-027/027b, which
 * edits the same row from the other end of the app's life. A second class over `/v1/users/me`
 * would be two places that decide what `notif_prefs` looks like.
 *
 * **The `PUT` is split in two on purpose** (Δ C083). [save] is the first-run call and carries a
 * language, because SCR-PA-004 draws a Language row; [update] is Settings' and **has no language
 * parameter at all**, which is AL-26's *"language selection removed from Edit-profile"* made
 * structural rather than left to a screen to remember. The language has its own route —
 * [saveLanguage] — because SCR-PA-027 changes it on its own and a whole-profile `PUT` to move one
 * field would race whatever else the screen had loaded.
 */
internal class PassengerProfileRepository(private val iam: IamApi) {

    /** `GET /v1/users/me`. Throws on any failure — every caller decides what a failure means. */
    suspend fun me(): UserProfile = iam.getMyProfile()

    /**
     * `PUT /v1/users/me` — SCR-PA-004's save.
     *
     * **Every field the screen owns goes in one call.** The contract's `UpdateProfileRequest` is
     * all-optional, so a partial save is expressible and would be the wrong thing: the wireframe's
     * *"Save & continue"* is one action, and three calls would leave a passenger who lost signal
     * halfway with a name and no language.
     *
     * @param notificationsEnabled The wireframe's *"Notifications & offers"* switch. Sent as the
     *   whole `notifPrefs` map under one key rather than as a boolean field, because that is the
     *   shape `iam.users.notif_prefs` has — and US-10.7 is **opt-out**, so an absent key reads as
     *   on. Writing the key explicitly is what makes turning it *off* stick.
     */
    suspend fun save(
        firstName: String,
        language: Language,
        notificationsEnabled: Boolean,
        photoUrl: String? = null,
    ): UserProfile = iam.updateMyProfile(
        UpdateProfileRequest(
            firstName = firstName.trim(),
            photoUrl = photoUrl,
            language = language,
            notifPrefs = mapOf(NOTIFICATIONS_AND_OFFERS to notificationsEnabled),
        ),
    )

    /**
     * `PUT /v1/users/me` — SCR-PA-027b's *Save*, and SCR-PA-027's 🔔 switch.
     *
     * **No language, by construction** (AL-26). The parameter does not exist, so no screen reached
     * from Settings can send one however it is written; the language row on SCR-PA-027 goes through
     * [saveLanguage] instead.
     *
     * @param notifPrefs The **whole** switch map, keys this build has never heard of included. The
     *   set of notification types grows without a contract change, so a caller that sent only the
     *   keys it knows would silently re-enable a type a newer build had muted — see [withMarketing].
     */
    suspend fun update(
        firstName: String? = null,
        photoUrl: String? = null,
        notifPrefs: Map<String, Boolean>? = null,
    ): UserProfile = iam.updateMyProfile(
        UpdateProfileRequest(
            firstName = firstName?.trim(),
            photoUrl = photoUrl,
            notifPrefs = notifPrefs,
        ),
    )

    /**
     * `PUT /v1/me/prefs/language` — SCR-PA-027's Language row (D-26, AL-26).
     *
     * **The server's copy only.** The device's copy is what `MainActivity.attachBaseContext` reads
     * through `PassengerLocale.wrap`, and writing it is the caller's — the screen also has to call
     * `recreate()`, and a repository that wrote the preference without re-inflating the resources
     * would leave the app rendering in the old language with the new one stored.
     */
    suspend fun saveLanguage(language: Language): Language =
        iam.setLanguagePreference(LanguagePreference(language)).language

    /**
     * `PUT /v1/me/prefs/payment-method` — SCR-PA-027's Default payment row (US-22.4, AL-14).
     *
     * Takes the **stored** enum rather than a rail, because not every rail has one: see
     * `PaymentRails.storedValueOf`, which is what decides whether this call can be made at all.
     */
    suspend fun saveDefaultPaymentMethod(method: DefaultPaymentMethod): DefaultPaymentMethod =
        iam.setDefaultPaymentMethod(DefaultPaymentMethodPreference(method)).defaultPaymentMethod

    /**
     * `DELETE /v1/users/me` — SCR-PA-027's *Delete account* (US-1.8, PDPA).
     *
     * **`202`, not `204`.** The account is not gone when this returns: the request becomes a
     * pdpa-svc erasure request that a statutory hold can delay (E-06), and the returned id is what
     * `GET /v1/pdpa/{requestId}` tracks. The screen says so — telling a passenger their account is
     * deleted when it is queued is the one thing this dialog must not do.
     */
    suspend fun deleteAccount(): Ulid = iam.deleteMyAccount().requestId

    internal companion object {

        /**
         * The switch map with *"Notifications & offers"* set, and every other key left alone.
         *
         * `null` and an absent key both mean *"never said otherwise"*, which US-10.7 makes **on** —
         * so a profile that has never been touched still round-trips to `{MARKETING: true}` rather
         * than to an empty map that would read as a mute.
         */
        fun withMarketing(existing: Map<String, Boolean>?, enabled: Boolean): Map<String, Boolean> =
            (existing.orEmpty()) + (NOTIFICATIONS_AND_OFFERS to enabled)

        /** Whether the switch reads as on. US-10.7 is opt-out, so an absent key is on. */
        fun marketingEnabled(existing: Map<String, Boolean>?): Boolean = existing?.get(NOTIFICATIONS_AND_OFFERS)
            ?: true

        /**
         * The `iam.users.notif_prefs` key behind SCR-PA-004's one switch.
         *
         * The wireframe offers a single *"Notifications & offers"* toggle rather than the per-type
         * list SCR-PA-027b draws, so it maps to one key. Nothing safety-critical is behind it:
         * `RIDE_CANCELLED`, `SOS_TRIGGERED` and `SOS_RESOLVED` are the three iam-svc refuses to
         * store a preference for at all, so they cannot be muted here or anywhere.
         */
        const val NOTIFICATIONS_AND_OFFERS = "MARKETING"
    }
}
