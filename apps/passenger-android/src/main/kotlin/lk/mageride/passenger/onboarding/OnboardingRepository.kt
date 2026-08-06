package lk.mageride.passenger.onboarding

import kotlinx.coroutines.CancellationException
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.api.content.ContentApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OnboardingAudience
import lk.mageride.shared.data.models.content.OnboardingSlide
import lk.mageride.shared.data.models.iam.LanguagePreference

/**
 * SCR-PA-002's data: the carousel, and where the language answer ends up.
 *
 * The screen runs **before sign-in**, so the answer is written locally first and pushed to
 * `iam.users` by [syncPreferences] on the first authenticated pass — the login screen calls it once
 * the OTP has been verified.
 */
internal class OnboardingRepository(
    private val content: ContentApi,
    private val iam: IamApi,
    private val preferences: AppPreferences,
) {

    private var cached: List<OnboardingSlide> = emptyList()

    /**
     * AL-28's three slides, or an empty list when content-svc cannot be reached.
     *
     * Empty is a supported answer, not a failure: the screen falls back to
     * [FeatureSlides.Fallback], which is bundled and trilingual. First launch is exactly when a
     * passenger is most likely to be on a bad connection, and a carousel is not worth blocking the
     * language picker over.
     *
     * Cached for the composition's life because the language picker is on this screen — switching
     * to සිංහල re-renders from the same response rather than re-fetching, which is why the payload
     * carries all three languages at once.
     */
    @Suppress("TooGenericExceptionCaught")
    suspend fun slides(): List<OnboardingSlide> {
        if (cached.isNotEmpty()) return cached
        return try {
            content.listOnboardingSlides(OnboardingAudience.PASSENGER)
                .slides
                .sortedBy(OnboardingSlide::slot)
                .also { cached = it }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            emptyList()
        }
    }

    /** What SCR-PA-002 shows as selected — Sinhala by default (AL-26). */
    fun selectedLanguage(): Language = preferences.language ?: Language.SI

    /**
     * The language the app is currently rendering in, or `null` on a first run.
     *
     * Distinct from [selectedLanguage]'s Sinhala default: `null` here means "the handset's locale
     * is what is on screen", and choosing සිංහල over it is still a change that needs the Activity
     * rebuilt.
     */
    fun storedLanguage(): Language? = preferences.language

    /**
     * Records the choice on the device and marks it for the server.
     *
     * Local first, and unconditionally: the flow continues to Login whether or not the handset can
     * reach the gateway, and a passenger who chose සිංහල must not be shown an English login screen
     * because a preference call timed out.
     */
    fun choose(language: Language) {
        preferences.language = language
        preferences.languagePendingSync = true
    }

    /**
     * Pushes the stored language to `iam.users` (D-26).
     *
     * Called after sign-in, and best effort: a preference is not worth failing a login over, and
     * the flag stays set so the next authenticated pass tries again. Returns whether it landed.
     */
    @Suppress("TooGenericExceptionCaught")
    suspend fun syncPreferences(): Boolean {
        if (!preferences.languagePendingSync) return true

        return try {
            preferences.language?.let { iam.setLanguagePreference(LanguagePreference(it)) }
            preferences.languagePendingSync = false
            true
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // Left pending on purpose — see the KDoc.
            false
        }
    }
}
