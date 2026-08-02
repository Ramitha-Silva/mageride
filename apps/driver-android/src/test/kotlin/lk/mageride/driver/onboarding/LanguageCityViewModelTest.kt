package lk.mageride.driver.onboarding

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.content.OperatingCity
import lk.mageride.shared.data.models.content.OperatingCityListResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-002's two rules: **Sinhala first and default** (AL-26) and **the cities are the
 * server's** (AL-27, US-1.3a).
 *
 * Both are the kind of thing that is right the day it is written and wrong two components later —
 * a language list re-derived from `Language.entries` puts English first, and a city list
 * "temporarily" hard-coded for a demo strands whichever city Admin activates next.
 */
class LanguageCityViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val preferences = FakeOnboardingPreferences()

    private val cities = listOf(
        city(code = "colombo", en = "Colombo", si = "කොළඹ", ta = "கொழும்பு", order = 1),
        city(code = "kandy", en = "Kandy", si = "මහනුවර", ta = "கண்டி", order = 2),
    )

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listOperatingCities", OperatingCityListResponse(cities))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_language_boxes_are_sinhala_then_tamil_then_english_with_sinhala_selected() {
        val state = viewModel().state.value

        // AL-26: "onboarding presents language as vertical boxes, Sinhala-first". Deliberately not
        // `Language.entries` — that order is the wire enum's and would put si/ta/en in whatever
        // sequence `_shared.yaml` happens to declare.
        assertEquals(listOf(Language.SI, Language.TA, Language.EN), state.languages)
        assertEquals(Language.SI, state.language, "Sinhala is the default, not a translation")
    }

    @Test
    fun the_cities_come_from_the_config_route_and_are_shown_in_the_servers_order() = runBlocking {
        val state = viewModel().state.await { !it.loadingCities }

        assertTrue(backend.called("listOperatingCities"), "the city list is never a constant (AL-27)")
        assertEquals(listOf("colombo", "kandy"), state.cities.map(OperatingCity::code))
        assertEquals("colombo", state.cityCode, "the first row the server sent is pre-selected")
        assertFalse(state.citiesFailed)
    }

    @Test
    fun a_city_name_is_shown_in_the_chosen_language() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loadingCities }
        model.selectLanguage(Language.TA)

        val state = model.state.value
        // The three names are server data, not resource strings: Admin can add a city without an
        // app release, so its Sinhala and Tamil names have to travel with it.
        assertEquals("கொழும்பு", state.cities.first().name(state.language))
    }

    @Test
    fun continue_stores_both_answers_and_marks_them_for_the_server() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loadingCities }
        model.selectLanguage(Language.TA)
        model.selectCity("kandy")

        assertTrue(model.state.value.canContinue)
        val languageChanged = model.confirm()

        assertEquals(Language.TA, preferences.language)
        assertEquals("kandy", preferences.operatingCityCode)
        // The screen runs before there is a session, so `iam.users` is written on the first
        // authenticated pass instead — the login screen's `syncPreferences()`.
        assertTrue(preferences.preferencesPendingSync, "the choice still has to reach iam.users")
        assertTrue(languageChanged, "a first run is always a language change: nothing was applied before")
    }

    @Test
    fun continue_is_dead_until_a_city_is_chosen() = runBlocking {
        // No cities from the server means nothing to pre-select, and the CTA has to stay down —
        // a Continue that stored a null city would put a driver on a dashboard with no centroid.
        backend.returns("listOperatingCities", OperatingCityListResponse(emptyList()))
        val model = viewModel()
        model.state.await { !it.loadingCities }

        assertFalse(model.state.value.canContinue)
        assertFalse(model.confirm(), "confirming without a city does nothing")
        assertNull(preferences.operatingCityCode)
    }

    @Test
    fun a_failed_city_call_is_offered_as_retry_rather_than_an_empty_list() = runBlocking {
        backend.fails("listOperatingCities", HttpStatusCode.InternalServerError, "internal-error")
        val model = viewModel()

        // An empty picker and an unreachable gateway look identical to a driver, and only one of
        // them is worth waiting out.
        assertTrue(model.state.await { it.citiesFailed }.citiesFailed)
        assertFalse(model.state.value.loadingCities)

        backend.returns("listOperatingCities", OperatingCityListResponse(cities))
        model.loadCities()

        val recovered = model.state.await { !it.loadingCities && !it.citiesFailed }
        assertEquals(2, recovered.cities.size)
    }

    private fun viewModel(): LanguageCityViewModel {
        val api = backend.mageRideApi()
        return LanguageCityViewModel(
            OnboardingRepository(content = api.content, iam = api.iam, preferences = preferences),
        )
    }

    private fun city(code: String, en: String, si: String, ta: String, order: Int) = OperatingCity(
        code = code,
        nameEn = en,
        nameSi = si,
        nameTa = ta,
        centroid = GeoPoint(lat = COLOMBO_LAT, lng = COLOMBO_LNG),
        sortOrder = order,
    )

    private companion object {
        const val COLOMBO_LAT = 6.93
        const val COLOMBO_LNG = 79.85
    }
}
