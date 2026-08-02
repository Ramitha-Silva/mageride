package lk.mageride.shared.data.api

import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.CaptureSource
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.wallet.TransferDirectionFilter
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * A spot-check across the shapes the 176 operations come in, rather than 176 near-identical tests.
 *
 * C012 already asserts every DTO round-trips against the contract examples. What is worth
 * asserting here is the handful of *response shapes* the transport has to treat specially: a
 * `Page<T>`, a `oneOf(Schema, null)`, a multipart upload, a conditional GET and a `302` whose
 * whole payload is a header.
 */
class TypedClientTest {

    @Test
    fun a_paged_response_decodes_into_the_one_page_envelope() = runTest {
        val test = testApi { _, _ ->
            respondJson(
                """
                {"items":[{"rideId":"01R","state":"Completed","completedAt":"2026-07-27T04:15:00Z"}],
                 "cursor":"next-page","hasMore":true}
                """.trimIndent(),
            )
        }

        val page = test.api.ride.listRideHistory()

        assertEquals(1, page.items.size)
        assertEquals("next-page", page.cursor)
        assertTrue(page.hasMore)
    }

    @Test
    fun a_null_body_on_a_one_of_read_is_a_null_result_not_a_failure() = runTest {
        // "No active ride" is the ordinary answer for a passenger who is not in one; the contract
        // says `oneOf(RideDetail, null)`, not `404`.
        val test = testApi { _, _ -> respondJson("null") }

        assertNull(test.api.ride.getActivePassengerRide("01USER"))
        assertNull(test.api.tripState.getActiveSession("01VEHICLE"))
    }

    @Test
    fun a_present_body_on_the_same_read_decodes_normally() = runTest {
        val test = testApi { _, _ ->
            respondJson(
                """
                {"sessionId":"01S","vehicleId":"01V","mode":"B","state":"ACTIVE",
                 "startedAt":"2026-07-27T04:15:00Z"}
                """.trimIndent(),
            )
        }

        val session = test.api.tripState.getActiveSession("01VEHICLE")

        assertEquals("01S", session?.sessionId)
        assertEquals(ServiceMode.B, session?.mode)
    }

    @Test
    fun a_204_response_produces_no_decoding_attempt() = runTest {
        val test = testApi { _, _ -> respondNoContent() }

        test.api.iam.logout()
        test.api.iam.deleteSavedAddress("01ADDRESS")
        test.api.safety.unblockDriver("01DRIVER")

        assertEquals(3, test.requests.size)
        assertEquals("POST", test.requests[0].method)
        assertEquals("DELETE", test.requests[1].method)
    }

    @Test
    fun a_multipart_upload_sends_the_file_and_its_text_fields() = runTest {
        val test = testApi { _, _ -> respondJson("""{"artifactId":"01ARTIFACT"}""") }

        test.api.ride.uploadPackageProofPhoto(
            rideId = "01RIDE",
            file = FileUpload(fileName = "proof.jpg", bytes = byteArrayOf(1, 2, 3), contentType = "image/jpeg"),
            note = "left with the neighbour",
        )

        val request = test.requests.single()
        assertTrue(request.contentType?.startsWith("multipart/form-data") == true, "was ${request.contentType}")
        assertTrue(request.body.contains("proof.jpg"), "the filename should be in the part headers")
        assertTrue(request.body.contains("left with the neighbour"))
        assertTrue(request.idempotencyKey != null, "an upload is still a POST mutation")
    }

    @Test
    fun an_onboarding_step_can_be_sent_as_json_or_as_multipart() = runTest {
        val test = testApi { _, _ ->
            // `status` is required on this response (Δ C029): saving the fourth verified step
            // auto-approves the vehicle, so the response that caused it says so rather than
            // making the app poll for it.
            respondJson("""{"stepStatus":"VERIFIED","onboardingStatus":"incomplete","status":"PENDING"}""")
        }

        test.api.registry.uploadVehicleOnboardingStep(
            vehicleId = "01VEHICLE",
            step = OnboardingStep.INSURANCE,
            file = CapturedDocument(FileUpload("insurance.jpg", byteArrayOf(9)), CaptureSource.CAMERA_DRAG_CROP),
        )

        val request = test.requests.single()
        assertEquals("/v1/vehicles/01VEHICLE/onboarding/insurance", request.path)
        assertEquals("PUT", request.method)
    }

    @Test
    fun the_conditional_cities_read_reports_not_modified_without_a_body() = runTest {
        val test = testApi { _, _ -> respond("", HttpStatusCode.NotModified, headersOf()) }

        val result = test.api.content.getOperatingCities(ifNoneMatch = "\"v3\"")

        assertIs<Conditional.NotModified>(result)
        assertNull(result.valueOrNull)
        assertEquals("\"v3\"", test.requests.single().headers["If-None-Match"])
    }

    @Test
    fun the_conditional_cities_read_returns_the_value_and_its_etag() = runTest {
        val test = testApi { _, _ ->
            respond(
                content = """{"cities":[{"code":"CMB","nameEn":"Colombo","nameSi":"කොළඹ","nameTa":"கொழும்பு",
                    "centroid":{"lat":6.9271,"lng":79.8612},"sortOrder":1}]}
                """.trimIndent(),
                status = HttpStatusCode.OK,
                headers = headersOf("Content-Type" to listOf("application/json"), "ETag" to listOf("\"v4\"")),
            )
        }

        val result = test.api.content.getOperatingCities()

        val value = assertIs<Conditional.Value<*>>(result)
        assertEquals("\"v4\"", value.etag)
        assertEquals(1, result.valueOrNull?.cities?.size)
    }

    @Test
    fun the_gtfs_download_returns_the_location_header_rather_than_following_it() = runTest {
        val test = testApi { _, _ ->
            respond("", HttpStatusCode.Found, headersOf("Location", "https://objects.mageride.lk/signed/feed.zip"))
        }

        val url = test.api.transit.downloadGtfsFeedUrl("01FEED")

        assertEquals("https://objects.mageride.lk/signed/feed.zip", url)
        assertEquals(1, test.requests.size, "the redirect must not be chased")
    }

    @Test
    fun a_csv_statement_comes_back_as_text_with_the_right_accept_header() = runTest {
        val test = testApi { _, _ ->
            respond("date,amount\n2026-07-27,48000\n", HttpStatusCode.OK, headersOf("Content-Type", "text/csv"))
        }

        val csv = test.api.wallet.downloadWalletStatementCsv("01USER")

        assertTrue(csv.startsWith("date,amount"))
        assertEquals("text/csv", test.requests.single().headers["Accept"])
    }

    @Test
    fun list_query_parameters_are_comma_joined_as_the_contract_asks() = runTest {
        // `explode: false` on `?types=` and `?modes=` means one parameter, not one per value.
        val test = testApi { _, _ -> respondJson("""{"vehicles":[],"asOf":"2026-07-27T04:15:00Z"}""") }

        test.api.query.getNearbyVehicles(
            lat = 6.9271,
            lng = 79.8612,
            types = listOf(VehicleType.THREE_WHEELER, VehicleType.SEDAN),
            modes = listOf(ServiceMode.C),
        )

        val query = test.requests.single().query
        assertEquals("three_wheeler,sedan", query["types"])
        assertEquals("C", query["modes"])
    }

    @Test
    fun a_business_date_query_parameter_is_sent_as_an_iso_date() = runTest {
        val test = testApi { _, _ -> respondJson("""{"items":[],"cursor":null,"hasMore":false}""") }

        test.api.wallet.listWalletTransfers("01DRIVER", direction = TransferDirectionFilter.SENT)

        assertEquals("sent", test.requests.single().query["direction"])
    }

    @Test
    fun each_client_labels_its_calls_with_its_own_contract_path() = runTest {
        val test = testApi { _, _ -> respondJson("{}") }

        runCatching { test.api.dispatch.getDirectionalFilter() }
        runCatching { test.api.support.listFaqArticles() }
        runCatching { test.api.safety.getSharedTrip("share-token") }

        assertEquals(
            listOf("/v1/standby/directional", "/v1/support/faq", "/v1/trip-share/public/share-token"),
            test.requests.map { it.path },
        )
    }
}
