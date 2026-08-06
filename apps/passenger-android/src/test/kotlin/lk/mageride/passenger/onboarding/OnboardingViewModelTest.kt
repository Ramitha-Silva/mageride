package lk.mageride.passenger.onboarding

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OnboardingSlide
import lk.mageride.shared.data.models.content.OnboardingSlidesResponse
import lk.mageride.shared.data.models.content.TrilingualText
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-002 — the language answer, and the carousel that must never block it.
 *
 * The carousel is presentation only (BR-25.1). What actually matters on this screen is that the
 * choice is stored **before** anything can fail, and that the app is rebuilt in the chosen language
 * before the login screen is drawn — otherwise AL-26's Sinhala-first promise ends at this screen.
 */
class OnboardingViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun sinhala_is_highlighted_on_a_first_run() {
        // AL-26 / US-1.3: "default highlight = Sinhala", not the handset's locale and not English.
        assertEquals(Language.SI, viewModel(FakeAppPreferences(language = null)).state.value.language)
    }

    @Test
    fun the_stored_language_is_highlighted_on_a_return_visit() {
        assertEquals(Language.TA, viewModel(FakeAppPreferences(language = Language.TA)).state.value.language)
    }

    @Test
    fun choosing_a_language_stores_it_immediately_and_marks_it_for_the_server() {
        // Local first and unconditionally: the flow continues to Login whether or not the handset
        // can reach the gateway, and a passenger who chose සිංහල must not meet an English login
        // screen because a preference call timed out.
        val preferences = FakeAppPreferences(language = null)
        val model = viewModel(preferences)

        model.select(Language.TA)

        assertEquals(Language.TA, preferences.language)
        assertTrue(preferences.languagePendingSync)
    }

    @Test
    fun finishing_after_a_change_asks_for_the_activity_to_be_rebuilt() {
        // A language only reaches `Resources` through `attachBaseContext`, which runs before
        // `onCreate` — so the Activity has to be recreated or the next screen is in the old locale.
        val model = viewModel(FakeAppPreferences(language = Language.SI))

        model.select(Language.EN)

        assertTrue(model.state.value.languageChanged)
        assertTrue(model.finish(), "the caller must recreate")
    }

    @Test
    fun accepting_the_default_still_completes_the_first_run_and_still_rebuilds() {
        // A passenger who never touches a box has still ANSWERED the screen. Without the write the
        // router would send them straight back here on the next cold start, forever.
        //
        // And it still asks for a rebuild, which is the non-obvious half: on a first run there is
        // no stored language, so the app is drawing in **the handset's** locale — which for most
        // users here is not Sinhala. Accepting the default is therefore a real change from what is
        // on screen, and skipping the recreate would leave AL-26's Sinhala-first promise showing an
        // English login screen to somebody who never asked for one.
        val preferences = FakeAppPreferences(language = null)
        val model = viewModel(preferences)

        assertTrue(model.finish(), "the handset's locale is not necessarily the chosen one")
        assertEquals(Language.SI, preferences.language)
        assertTrue(preferences.firstRunComplete)
    }

    @Test
    fun re_selecting_the_language_already_in_force_needs_no_rebuild() {
        val model = viewModel(FakeAppPreferences(language = Language.TA))

        model.select(Language.TA)

        assertFalse(model.state.value.languageChanged)
        assertFalse(model.finish())
    }

    @Test
    fun the_carousel_comes_from_content_svc_when_it_answers() = runBlocking {
        backend.returns(
            "listOnboardingSlides",
            OnboardingSlidesResponse(
                slides = listOf(slide(slot = 2), slide(slot = 1)),
            ),
        )
        val model = viewModel(FakeAppPreferences(language = null))

        val state = model.state.await { it.slides.isNotEmpty() }

        // Sorted by slot: the server's order is not guaranteed and the pager counts on it.
        assertEquals(listOf(1, 2), state.slides.map(OnboardingSlide::slot))
    }

    @Test
    fun a_carousel_that_cannot_be_fetched_leaves_the_language_picker_working() = runBlocking {
        // First launch is exactly when a passenger is most likely to be on a bad connection. The
        // screen falls back to the bundled trilingual slides; what must not happen is the picker
        // being blocked behind a spinner.
        backend.fails("listOnboardingSlides", HttpStatusCode.ServiceUnavailable, "service-unavailable")
        val preferences = FakeAppPreferences(language = null)
        val model = viewModel(preferences)

        model.select(Language.EN)

        assertTrue(model.state.value.slides.isEmpty(), "and the screen draws FeatureSlides.Fallback")
        assertEquals(Language.EN, preferences.language)
        assertEquals(FeatureSlides.COUNT, FeatureSlides.Fallback.size)
    }

    @Test
    fun the_three_rows_are_sinhala_then_tamil_then_english() {
        // US-1.3 fixes the ORDER, not just the set — and it is not the enum's declaration order by
        // accident. `LanguageChoices` is what the screen iterates.
        assertEquals(listOf(Language.SI, Language.TA, Language.EN), LanguageChoices)
    }

    private fun viewModel(preferences: FakeAppPreferences): OnboardingViewModel {
        val api = backend.mageRideApi()
        return main.own(
            OnboardingViewModel(
                OnboardingRepository(content = api.content, iam = api.iam, preferences = preferences),
            ),
        )
    }

    private fun slide(slot: Int) = OnboardingSlide(
        slot = slot,
        illustrationRef = "onboarding/passenger-$slot",
        title = TrilingualText(si = "සිරස්තලය", ta = "தலைப்பு", en = "Title $slot"),
        body = TrilingualText(si = "විස්තරය", ta = "விவரம்", en = "Body $slot"),
    )
}
