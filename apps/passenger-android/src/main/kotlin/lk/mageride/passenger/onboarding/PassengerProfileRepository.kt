package lk.mageride.passenger.onboarding

import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.iam.UpdateProfileRequest
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * `iam.users` as the first-run cluster uses it — SCR-PA-004, and the profile check the splash and
 * the login screen each make.
 *
 * One class over one service, because that is genuinely all this is: `GET /v1/users/me` and
 * `PUT /v1/users/me`. C083's Profile & settings reads the same pair for SCR-PA-027; when it
 * lands, this is the seam it should reuse rather than open a second one.
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

    internal companion object {

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
