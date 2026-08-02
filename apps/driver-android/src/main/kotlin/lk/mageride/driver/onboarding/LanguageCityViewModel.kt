package lk.mageride.driver.onboarding

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OperatingCity

/**
 * SCR-DA-002's state.
 *
 * @property languages Sinhala, then Tamil, then English — AL-26's order, fixed in [LANGUAGES].
 * @property language The selected language. Sinhala until the driver changes it.
 * @property cities The active launch cities from `GET /v1/config/cities`. Empty while loading.
 * @property cityCode The selected city's code; `null` disables Continue.
 * @property loadingCities Whether the city list is in flight.
 * @property citiesFailed Whether the city call failed — the screen offers Retry.
 */
internal data class LanguageCityState(
    val language: Language = Language.SI,
    val cities: List<OperatingCity> = emptyList(),
    val cityCode: String? = null,
    val loadingCities: Boolean = true,
    val citiesFailed: Boolean = false,
) {
    /** AL-26's vertical boxes, in the one order the spec fixes: Sinhala first and default. */
    val languages: List<Language> get() = LANGUAGES

    /** The wireframe's CTA is dead until a city has been chosen; the city list is a radio group. */
    val canContinue: Boolean get() = cityCode != null

    internal companion object {
        /**
         * *"language as **vertical boxes (Sinhala first & default)**, then Tamil, English"* —
         * D2' §B SCR-DA-002, AL-26. Not `Language.entries`: that order is the wire enum's and
         * changing this list is the only way to change what the screen shows.
         */
        val LANGUAGES: List<Language> = listOf(Language.SI, Language.TA, Language.EN)
    }
}

/**
 * SCR-DA-002 — first-run language and operating city.
 *
 * Two selections and one rule each: the language defaults to Sinhala (AL-26) and the city list is
 * **always** the server's (AL-27, US-1.3a). Both are written to the device on Continue and pushed
 * to `iam.users` after sign-in — the screen runs before there is a session to write them against.
 */
internal class LanguageCityViewModel(private val repository: OnboardingRepository) : ViewModel() {

    private val mutableState = MutableStateFlow(
        repository.selection().let { LanguageCityState(language = it.language, cityCode = it.cityCode) },
    )

    val state: StateFlow<LanguageCityState> = mutableState.asStateFlow()

    init {
        loadCities()
    }

    /** `GET /v1/config/cities`. Retried by the screen's error state, never silently. */
    fun loadCities() {
        mutableState.update { it.copy(loadingCities = true, citiesFailed = false) }
        viewModelScope.launch {
            @Suppress("TooGenericExceptionCaught")
            try {
                val cities = repository.cities()
                mutableState.update { current ->
                    current.copy(
                        cities = cities,
                        // Colombo is first because `sortOrder` puts it there, not because the app
                        // says so. Pre-selecting the first row saves a tap without inventing a
                        // default the server did not express.
                        cityCode = current.cityCode ?: cities.firstOrNull()?.code,
                        loadingCities = false,
                        citiesFailed = false,
                    )
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                mutableState.update { it.copy(loadingCities = false, citiesFailed = true) }
            }
        }
    }

    fun selectLanguage(language: Language) {
        mutableState.update { it.copy(language = language) }
    }

    fun selectCity(code: String) {
        mutableState.update { it.copy(cityCode = code) }
    }

    /**
     * Stores both answers and reports whether the app has to re-inflate its resources.
     *
     * @return `true` when the chosen language is not the one the app is currently rendering, which
     *   is the caller's cue to `recreate()` the Activity — Android resolves a `Resources` object
     *   once per configuration, so a language chosen after that has no effect until it is rebuilt.
     *   On a first run the stored language is `null` (the screen is in the handset's own locale),
     *   so even choosing the Sinhala default is a change.
     */
    fun confirm(): Boolean {
        val chosen = mutableState.value
        val city = chosen.cityCode ?: return false
        val rendering = repository.storedLanguage()
        repository.choose(chosen.language, city)
        return rendering != chosen.language
    }
}
