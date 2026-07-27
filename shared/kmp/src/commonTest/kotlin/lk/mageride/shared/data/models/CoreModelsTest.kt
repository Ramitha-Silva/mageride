package lk.mageride.shared.data.models

import kotlinx.datetime.LocalDate
import kotlinx.serialization.SerializationException
import kotlinx.serialization.builtins.serializer
import kotlinx.serialization.encodeToString
import lk.mageride.shared.serialization.MageRideJson
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Instant

/**
 * The shared primitives every DTO is built from.
 *
 * These are the pieces a wrong assumption would break everywhere at once: the timestamp format,
 * the business-date format, the money type, the pagination envelope and the error registry.
 */
class CoreModelsTest {

    // ---- timestamps & business dates ---------------------------------------------------------

    @Test
    fun a_timestamp_round_trips_as_the_iso_8601_utc_instant_the_contracts_print() {
        // _shared.yaml#/components/schemas/Timestamp, example: '2026-07-27T04:15:00Z'
        val encoded = MageRideJson.encodeToString(Instant.parse("2026-07-27T04:15:00Z"))

        assertEquals("\"2026-07-27T04:15:00Z\"", encoded)
        assertEquals(
            Instant.parse("2026-07-27T04:15:00Z"),
            MageRideJson.decodeFromString<Timestamp>(encoded),
        )
    }

    @Test
    fun a_business_date_round_trips_as_a_bare_asia_colombo_calendar_day() {
        // _shared.yaml#/components/schemas/BusinessDate, example: '2026-07-27'
        val encoded = MageRideJson.encodeToString(LocalDate(2026, 7, 27))

        assertEquals("\"2026-07-27\"", encoded)
        assertEquals(LocalDate(2026, 7, 27), MageRideJson.decodeFromString<BusinessDate>(encoded))
    }

    // ---- money -------------------------------------------------------------------------------

    @Test
    fun money_serialises_as_integer_minor_units_and_always_states_its_currency() {
        // _shared.yaml#/components/schemas/Money — required [amountMinor, currency].
        // `currency` is a default, and MageRideJson sets encodeDefaults = false, so it is only on
        // the wire because Money forces it with @EncodeDefault.
        assertEquals(
            """{"amountMinor":48000,"currency":"LKR"}""",
            MageRideJson.encodeToString(Money.ofMinor(48_000)),
        )
    }

    @Test
    fun money_arithmetic_stays_in_minor_units() {
        val fare = Money.ofMinor(45_000)
        val tip = Money.ofMinor(5_000)

        assertEquals(Money.ofMinor(50_000), fare + tip)
        assertEquals(Money.ofMinor(40_000), fare - tip)
        assertEquals(Money.ofMinor(90_000), fare * 2)
        assertEquals(Money.ZERO, Money.ofMinor(0))
        assertTrue(fare > tip)
    }

    @Test
    fun a_flat_minor_field_is_reachable_as_money_without_reshaping_the_wire_form() {
        // Most D3' payloads spell money flat; MoneyHolder is the bridge, and the JSON stays flat.
        val fare = lk.mageride.shared.data.models.ride.FareEstimate(amountMinor = 45_000)

        assertEquals(Money.ofMinor(45_000), fare.money)
        assertEquals("""{"amountMinor":45000,"currency":"LKR"}""", MageRideJson.encodeToString(fare))
    }

    // ---- geo ---------------------------------------------------------------------------------

    @Test
    fun a_place_is_a_coordinate_plus_an_optional_address() {
        val decoded = MageRideJson.decodeFromString<Place>(
            """{"lat":6.927079,"lng":79.861243,"address":"Colombo Fort"}""",
        )

        assertEquals(GeoPoint(lat = 6.927079, lng = 79.861243), decoded.point)
        assertEquals("Colombo Fort", decoded.address)
    }

    @Test
    fun a_pickup_confirmation_carries_the_device_accuracy_it_reported() {
        // Body of POST /v1/location-requests/{requestId}/confirm (P-02).
        val decoded = MageRideJson.decodeFromString<GeoPointWithAccuracy>(
            """{"lat":6.9271,"lng":79.8612,"accuracy":8.0}""",
        )

        assertEquals(8.0, decoded.accuracy)
        assertEquals(GeoPoint(lat = 6.9271, lng = 79.8612), decoded.point)
    }

    // ---- pagination --------------------------------------------------------------------------

    @Test
    fun a_cursor_page_decodes_its_null_cursor_as_the_last_page() {
        // _shared.yaml#/components/schemas/CursorPage — `cursor` is null on the last page and is
        // force-serialised server-side (C002 decision 9), so "last page" is never "field missing".
        val page = MageRideJson.decodeFromString(
            Page.serializer(String.serializer()),
            """{"items":["a","b"],"cursor":null,"hasMore":false}""",
        )

        assertEquals(listOf("a", "b"), page.items)
        assertNull(page.cursor)
        assertEquals(false, page.hasMore)
    }

    @Test
    fun a_cursor_page_carries_its_continuation_token_when_there_is_one() {
        val page = MageRideJson.decodeFromString(
            Page.serializer(String.serializer()),
            """{"items":["a"],"cursor":"b3BhcXVl","hasMore":true}""",
        )

        assertEquals("b3BhcXVl", page.cursor)
        assertTrue(page.hasMore)
        assertEquals(listOf("A"), page.map { it.uppercase() }.items)
    }

    @Test
    fun a_page_request_rejects_a_limit_outside_the_gateway_bounds() {
        assertEquals(PageRequest(), PageRequest.FIRST)
        assertEquals(100, PageRequest(limit = PageRequest.MAX_LIMIT).limit)
        assertFailsWith<IllegalArgumentException> { PageRequest(limit = 0) }
        assertFailsWith<IllegalArgumentException> { PageRequest(limit = 101) }
    }

    // ---- problem+json ------------------------------------------------------------------------

    @Test
    fun a_problem_body_exposes_the_stable_kebab_code_from_its_type_uri() {
        val problem = MageRideJson.decodeFromString<ProblemDetails>(
            """
            {"type":"https://mageride.lk/errors/offer-expired","title":"Offer has expired",
             "status":410,"instance":"/v1/rides/01JQ/offer/01JR/accept",
             "traceId":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"}
            """.trimIndent(),
        )

        assertEquals("offer-expired", problem.code)
        assertEquals(ErrorCode.OFFER_EXPIRED, problem.errorCode)
        assertEquals(410, problem.status)
    }

    @Test
    fun an_unknown_server_side_code_degrades_to_null_rather_than_failing_to_parse() {
        // A service may register a new code at start-up (MageRideErrors.Register, C002). An older
        // build must still be able to read the body that explains the failure.
        val problem = MageRideJson.decodeFromString<ProblemDetails>(
            """{"type":"https://mageride.lk/errors/coined-next-year","title":"New","status":409}""",
        )

        assertEquals("coined-next-year", problem.code)
        assertNull(problem.errorCode)
    }

    @Test
    fun a_validation_failure_carries_its_field_level_detail() {
        val problem = MageRideJson.decodeFromString<ProblemDetails>(
            """
            {"type":"https://mageride.lk/errors/validation-failed","title":"Validation failed",
             "status":400,"errors":{"phone":["must match ^\\+947\\d{8}$"]}}
            """.trimIndent(),
        )

        assertEquals(ErrorCode.VALIDATION_FAILED, problem.errorCode)
        assertEquals(1, problem.errors?.get("phone")?.size)
    }

    @Test
    fun a_426_carries_the_update_gate_extensions_the_client_renders() {
        // D-31: the gateway answers 426 with updateUrl / latestVersion / isMandatory as Problem
        // extensions, which is the same trio GET /v1/version/check returns.
        val problem = MageRideJson.decodeFromString<ProblemDetails>(
            """
            {"type":"https://mageride.lk/errors/upgrade-required","title":"Upgrade required",
             "status":426,"updateUrl":"https://play.google.com/store/apps/details?id=lk.mageride",
             "latestVersion":"1.6.2","isMandatory":true}
            """.trimIndent(),
        )

        assertEquals(ErrorCode.UPGRADE_REQUIRED, problem.errorCode)
        assertEquals("1.6.2", problem.latestVersion)
        assertEquals(true, problem.isMandatory)
    }

    @Test
    fun every_error_code_has_a_unique_kebab_key_that_resolves_back_to_itself() {
        val wires = ErrorCode.entries.map { it.wire }

        assertEquals(wires.size, wires.toSet().size, "error codes must be globally unique")
        ErrorCode.entries.forEach { code ->
            assertEquals(code, ErrorCode.fromWire(code.wire))
            assertEquals(ProblemDetails.TYPE_PREFIX + code.wire, code.typeUri)
            assertTrue(code.wire.matches(Regex("^[a-z0-9]+(-[a-z0-9]+)*$")), code.wire)
        }
    }

    // ---- position samples --------------------------------------------------------------------

    @Test
    fun a_position_sample_round_trips_the_topic_contract_payload() {
        // backend/contracts/realtime/mqtt-topics.md §2.1 — the JSON rendering of the CBOR payload.
        val payload = """
            {"vehicleId":"01JQ9F8Z6N5R7T2V4X6Y8A0B2C",
             "sampleTs":"2026-06-13T10:15:30Z","receivedTs":"2026-06-13T10:15:31Z","seq":84213,
             "lat":6.9271,"lng":79.8612,"speedMps":11.8,"headingDeg":270,
             "accuracyM":7.5,"hdop":0.9,"satCount":11,"source":1,
             "mode":"C","vehicleType":"three_wheeler","fleetId":null,
             "tripId":"01JR9F8Z6N5R7T2V4X6Y8A0B2C"}
        """.trimIndent()

        val sample = MageRideJson.decodeFromString<PositionSample>(payload)

        assertEquals(84_213L, sample.seq)
        assertEquals(PositionSource.GT06, sample.source)
        assertEquals(ServiceMode.C, sample.mode)
        assertEquals(VehicleType.THREE_WHEELER, sample.vehicleType)
        assertNull(sample.fleetId)
        assertEquals(GeoPoint(lat = 6.9271, lng = 79.8612), sample.point)
        assertEquals(sample, MageRideJson.decodeFromString(MageRideJson.encodeToString(sample)))
    }

    @Test
    fun the_position_source_is_the_small_integer_the_check_constraint_allows() {
        // telemetry.positions.source SMALLINT, ck_positions_source CHECK (source BETWEEN 0 AND 4).
        assertEquals(listOf(0, 1, 2, 3, 4), PositionSource.entries.map { it.code })
        PositionSource.entries.forEach { source ->
            assertEquals("${source.code}", MageRideJson.encodeToString(source))
            assertEquals(source, PositionSource.fromCode(source.code))
        }
        assertNull(PositionSource.fromCode(5))
        assertTrue(PositionSource.MOBILE.isHardware.not())
    }

    @Test
    fun a_source_outside_the_check_domain_is_an_error_rather_than_a_silent_default() {
        assertFailsWith<SerializationException> {
            MageRideJson.decodeFromString<PositionSource>("9")
        }
    }
}
