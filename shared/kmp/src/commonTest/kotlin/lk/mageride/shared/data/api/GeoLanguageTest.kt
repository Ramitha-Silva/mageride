package lk.mageride.shared.data.api

import io.ktor.client.engine.mock.MockRequestHandleScope
import io.ktor.client.request.HttpRequestData
import io.ktor.client.request.HttpResponseData
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.query.AppLanguage
import lk.mageride.shared.data.api.query.LocalisedQueryApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.Language
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/**
 * `?lang=` on the two geocoding calls (D-26).
 *
 * A place name a passenger reads on SCR-PA-008 is a user-facing string like any other, so the
 * language they chose has to reach query-svc — which forwards it to the self-hosted Nominatim as
 * `accept-language`. Everything below is about **what left the device**, in the manner of the rest
 * of this package: the answer itself is Nominatim's and the shapes are C012's.
 *
 * The decorator exists because five separate screens reach the geocoder in the passenger app
 * alone. Asserting it here rather than at those screens is what makes a sixth screen correct
 * without being touched.
 */
class GeoLanguageTest {

    @Test
    fun the_chosen_language_reaches_both_geocoding_calls() = runTest {
        val test = testApi(respond = answer)
        val query = localised(test.api.query) { Language.SI }

        query.searchPlaces(query = "Colombo")
        query.reverseGeocode(lat = 6.9271, lng = 79.8612)

        assertEquals(listOf("si", "si"), test.requests.map { it.query["lang"] })
    }

    @Test
    fun a_language_change_is_read_per_call_and_not_captured_at_start_up() = runTest {
        // The Koin graph is a `single` and survives the `recreate()` a language change performs, so
        // a decorator that snapshotted its language would pin whatever the app opened in for the
        // rest of the process. That is the whole reason `AppLanguage` is a supplier.
        var chosen: Language? = Language.SI
        val test = testApi(respond = answer)
        val query = localised(test.api.query) { chosen }

        query.searchPlaces(query = "Colombo")
        chosen = Language.TA
        query.searchPlaces(query = "Colombo")
        chosen = null
        query.searchPlaces(query = "Colombo")

        assertEquals(listOf("si", "ta", null), test.requests.map { it.query["lang"] })
    }

    @Test
    fun an_app_that_binds_no_language_sends_none() = runTest {
        // `getOrNull<AppLanguage>()` in `ApiModule` — both iOS apps are this case, and they must go
        // on getting exactly the answer they got before the parameter existed. Absent is not `en`:
        // the platform asking for English is a different question from nobody asking at all.
        val test = testApi(respond = answer)
        val query = LocalisedQueryApi(test.api.query, language = null)

        query.searchPlaces(query = "Colombo")
        query.reverseGeocode(lat = 6.9271, lng = 79.8612)

        test.requests.forEach { assertNull(it.query["lang"], "no language was bound, so none is sent") }
    }

    @Test
    fun an_explicit_language_outranks_the_bound_one() = runTest {
        val test = testApi(respond = answer)
        val query = localised(test.api.query) { Language.SI }

        query.searchPlaces(query = "Colombo", lang = Language.EN)
        query.reverseGeocode(lat = 6.9271, lng = 79.8612, lang = Language.EN)

        assertEquals(listOf("en", "en"), test.requests.map { it.query["lang"] })
    }

    @Test
    fun the_rest_of_the_request_is_unchanged() = runTest {
        val test = testApi(respond = answer)
        val query = localised(test.api.query) { Language.SI }

        query.searchPlaces(query = "Galle Face", lat = 6.9271, lng = 79.8612, limit = 5)

        val request = test.requests.single()
        assertEquals("/v1/geo/search", request.path)
        assertEquals("Galle Face", request.query["q"])
        assertEquals("6.9271", request.query["lat"])
        assertEquals("79.8612", request.query["lng"])
        assertEquals("5", request.query["limit"])
    }

    @Test
    fun no_other_operation_gains_a_language() = runTest {
        // `QueryApi by delegate` — the two geocoding calls are overridden and the rest are
        // forwarded untouched. A test rather than a reading of the source, because `?lang=` on an
        // operation whose contract has no such parameter is the kind of thing spectral catches
        // months later, on someone else's PR.
        val test = testApi(respond = answer)
        val query = localised(test.api.query) { Language.SI }

        query.getNearbyVehicles(lat = 6.9271, lng = 79.8612)

        val request = test.requests.single()
        assertEquals("/v1/nearby", request.path)
        assertNull(request.query["lang"], "only the two geocoding calls carry a language")
    }

    private fun localised(delegate: QueryApi, language: AppLanguage): QueryApi = LocalisedQueryApi(delegate, language)
}

/**
 * One body per route, so a test can make more than one kind of call in a row.
 *
 * A value rather than a function, because `testApi` takes an extension function type and a
 * `::reference` to a top-level extension does not convert to one without naming its receiver.
 */
private val answer: suspend MockRequestHandleScope.(Int, HttpRequestData) -> HttpResponseData =
    { _, request ->
        respondJson(
            when (request.url.encodedPath) {
                "/v1/geo/reverse" -> """{"lat":6.9271,"lng":79.8612,"displayName":"Galle Face Green"}"""
                "/v1/nearby" -> """{"vehicles":[],"asOf":"2026-07-27T04:15:00Z"}"""
                else -> """{"places":[]}"""
            },
        )
    }
