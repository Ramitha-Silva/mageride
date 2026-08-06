package lk.mageride.passenger.onboarding

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OnboardingSlide

/**
 * SCR-PA-002's state.
 *
 * @property language The highlighted row — Sinhala until the passenger says otherwise (AL-26).
 * @property slides content-svc's carousel, or empty while it is being fetched and after it failed.
 *   The screen falls back to [FeatureSlides.Fallback] on empty; see that KDoc.
 * @property languageChanged Whether the choice differs from what the app is currently rendering
 *   in, which is what decides whether Get Started has to recreate the Activity.
 */
internal data class OnboardingState(
    val language: Language = Language.SI,
    val slides: List<OnboardingSlide> = emptyList(),
    val languageChanged: Boolean = false,
)

/**
 * SCR-PA-002 — the three-slide carousel and the language picker.
 *
 * **No gating.** BR-25.1: the carousel is *"presentation only"* — a passenger can reach Get Started
 * from slide 1 without swiping, and Skip is the same action minus the reading. Nothing here waits
 * for content-svc, because nothing here needs it: the language boxes are the screen's actual job.
 */
internal class OnboardingViewModel(private val onboarding: OnboardingRepository) : ViewModel() {

    /**
     * The language the `Resources` on screen were inflated in — read **once**, before anything is
     * written.
     *
     * `null` on a genuine first run, meaning "the handset's own locale is what is being drawn". It
     * has to be captured here rather than re-read in [select], because `choose` writes through to
     * the same preference: comparing against it afterwards would compare a value with itself and
     * report that nothing had changed, every time.
     */
    private val renderingLanguage: Language? = onboarding.storedLanguage()

    private val mutableState = MutableStateFlow(
        OnboardingState(language = onboarding.selectedLanguage()),
    )

    val state: StateFlow<OnboardingState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch {
            mutableState.update { it.copy(slides = onboarding.slides()) }
        }
    }

    /** A language box was tapped. Recorded locally at once — see [OnboardingRepository.choose]. */
    fun select(language: Language) {
        onboarding.choose(language)
        mutableState.update {
            it.copy(language = language, languageChanged = language != renderingLanguage)
        }
    }

    /**
     * Get Started, and Skip — the same action.
     *
     * The wireframe's Skip sits above the carousel and the CTA below the language list; neither
     * skips the *language*, because there is nothing to skip: Sinhala is already chosen (AL-26)
     * and [OnboardingRepository.choose] has already stored whatever is highlighted. So both doors
     * do exactly one thing, which is why there is one method.
     *
     * @return whether the Activity must be recreated before Login is shown. A language chosen here
     *   only reaches `Resources` through `attachBaseContext`, so a passenger who picked සිංහල and
     *   was sent straight on would meet an English login screen.
     */
    fun finish(): Boolean {
        val chosen = mutableState.value.language
        // Written unconditionally rather than only on a change: `firstRunComplete` is
        // "SCR-PA-002 has been answered", and a passenger who accepted the Sinhala default without
        // touching a box has answered it. Without this write the router would send them straight
        // back here on the next cold start.
        onboarding.choose(chosen)
        return chosen != renderingLanguage
    }
}
