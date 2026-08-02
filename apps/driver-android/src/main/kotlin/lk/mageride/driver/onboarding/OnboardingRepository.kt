package lk.mageride.driver.onboarding

import kotlinx.coroutines.CancellationException
import lk.mageride.shared.data.api.Conditional
import lk.mageride.shared.data.api.content.ContentApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OperatingCity
import lk.mageride.shared.data.models.iam.LanguagePreference
import lk.mageride.shared.data.models.iam.OperatingCityPreference

/**
 * SCR-DA-002's data: the launch cities, and where the two answers end up.
 *
 * **The city list is never a constant** (AL-27). `config.operating_cities` is admin-managed and
 * `GET /v1/config/cities` is the only route that reads it, precisely so activating a new launch
 * city needs no app release (US-1.3a). A hard-coded list here would silently strand a city the
 * Admin Portal had already opened.
 *
 * The screen runs before sign-in, so the two answers are written locally first and pushed to
 * `iam.users` by [syncPreferences] on the first authenticated pass — the login screen calls it
 * once the OTP has been verified.
 */
internal class OnboardingRepository(
    private val content: ContentApi,
    private val iam: IamApi,
    private val preferences: OnboardingPreferences,
) {

    private var cachedTag: String? = null
    private var cachedCities: List<OperatingCity> = emptyList()

    /**
     * The active launch cities, `sortOrder` first (the server orders them; this does not re-sort).
     *
     * A conditional GET: `/v1/config/cities` is the one route in the whole contract that declares
     * an `ETag` and a `304`, and the first-run screen is exactly the caller it was declared for.
     * A `NotModified` answers from [cachedCities], so a driver who backs out of the screen and
     * returns pays no body.
     */
    suspend fun cities(): List<OperatingCity> = when (val answer = content.getOperatingCities(cachedTag)) {
        is Conditional.NotModified -> cachedCities

        is Conditional.Value -> {
            cachedCities = answer.value.cities
            cachedTag = answer.etag
            cachedCities
        }
    }

    /** What SCR-DA-002 currently shows as selected — Sinhala by default (AL-26). */
    fun selection(): OnboardingSelection = OnboardingSelection(
        language = preferences.language ?: Language.SI,
        cityCode = preferences.operatingCityCode,
    )

    /**
     * The language the app is currently rendering in, or `null` on a first run.
     *
     * Distinct from [selection]'s Sinhala default: `null` here means "the handset's locale is
     * what is on screen", and choosing සිංහල over it is still a change that needs the Activity
     * rebuilt.
     */
    fun storedLanguage(): Language? = preferences.language

    /**
     * Records the driver's answers on the device and marks them for the server.
     *
     * Local first, and unconditionally: the flow continues to Login whether or not the handset can
     * reach the gateway, and a driver who chose සිංහල must not be shown an English login screen
     * because a preference call timed out.
     */
    fun choose(language: Language, cityCode: String) {
        preferences.language = language
        preferences.operatingCityCode = cityCode
        preferences.preferencesPendingSync = true
    }

    /**
     * Pushes the stored answers to `iam.users` (D-26 language, AL-27 operating city).
     *
     * Called after sign-in, and best effort: neither preference is worth failing a login over, and
     * the flag stays set so the next authenticated pass tries again. Returns whether both landed.
     */
    @Suppress("TooGenericExceptionCaught")
    suspend fun syncPreferences(): Boolean {
        if (!preferences.preferencesPendingSync) return true

        return try {
            preferences.language?.let { iam.setLanguagePreference(LanguagePreference(it)) }
            preferences.operatingCityCode?.let { iam.setOperatingCity(OperatingCityPreference(it)) }
            preferences.preferencesPendingSync = false
            true
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // Left pending on purpose — see the KDoc.
            false
        }
    }
}

/**
 * SCR-DA-002's current answers.
 *
 * @property language Sinhala until the driver says otherwise (AL-26).
 * @property cityCode `null` until a city is picked; the CTA is disabled while it is.
 */
internal data class OnboardingSelection(val language: Language, val cityCode: String?)
