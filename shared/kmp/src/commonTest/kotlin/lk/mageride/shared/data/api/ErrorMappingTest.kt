package lk.mageride.shared.data.api

import io.ktor.client.engine.mock.respond
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * RFC 7807 → typed [MageRideError] (D3' §0).
 *
 * The status picks the type; the stable kebab code picks the branch inside it. Both survive, and
 * neither is inferred from the other.
 */
class ErrorMappingTest {

    @Test
    fun the_offer_race_has_two_distinct_typed_outcomes() = runTest {
        // The definition of done. `409 offer-already-accepted` means another driver won and the
        // ride is alive; `410 offer-expired` means the 15-second window elapsed and nobody took
        // it. One `catch` must not be able to swallow both by accident.
        val taken = testApi { _, _ ->
            respondProblem(HttpStatusCode.Conflict, ErrorCode.OFFER_ALREADY_ACCEPTED.wire)
        }
        val expired = testApi { _, _ ->
            respondProblem(HttpStatusCode.Gone, ErrorCode.OFFER_EXPIRED.wire)
        }
        val accept = AcceptRideOfferRequest(offerId = "01OFFER", version = 2)

        val conflict = assertFailsWith<MageRideError.Conflict> {
            taken.api.ride.acceptRideOffer("01RIDE", "01DRIVER", accept)
        }
        val gone = assertFailsWith<MageRideError.Gone> {
            expired.api.ride.acceptRideOffer("01RIDE", "01DRIVER", accept)
        }

        assertEquals(ErrorCode.OFFER_ALREADY_ACCEPTED, conflict.code)
        assertEquals(ErrorCode.OFFER_EXPIRED, gone.code)
        assertNotEquals<Any>(conflict::class, gone::class)
        assertEquals(HttpStatusCode.Conflict.value, conflict.status)
        assertEquals(HttpStatusCode.Gone.value, gone.status)
    }

    @Test
    fun a_409_version_conflict_is_the_same_type_but_a_different_code() = runTest {
        // Optimistic concurrency, not a lost race: the caller must re-read and re-decide rather
        // than tell the user someone else took the ride.
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.Conflict, ErrorCode.VERSION_CONFLICT.wire) }

        val error = assertFailsWith<MageRideError.Conflict> {
            test.api.ride.acceptRideOffer("01RIDE", "01DRIVER", AcceptRideOfferRequest("01OFFER", 2))
        }

        assertEquals(ErrorCode.VERSION_CONFLICT, error.code)
    }

    @Test
    fun every_status_class_maps_to_its_own_type() {
        assertIs<MageRideError.BadRequest>(problemFor(BAD_REQUEST, ErrorCode.VALIDATION_FAILED))
        assertIs<MageRideError.Unauthorized>(problemFor(UNAUTHORIZED, ErrorCode.UNAUTHORIZED))
        assertIs<MageRideError.AttestationFailed>(problemFor(UNAUTHORIZED, ErrorCode.ATTESTATION_FAILED))
        assertIs<MageRideError.PaymentRequired>(problemFor(PAYMENT_REQUIRED, ErrorCode.INSUFFICIENT_WALLET))
        assertIs<MageRideError.Forbidden>(problemFor(FORBIDDEN, ErrorCode.NOT_OWNER))
        assertIs<MageRideError.NotFound>(problemFor(NOT_FOUND, ErrorCode.VEHICLE_NOT_FOUND))
        assertIs<MageRideError.Conflict>(problemFor(CONFLICT, ErrorCode.ACTIVE_RIDE_EXISTS))
        assertIs<MageRideError.Gone>(problemFor(GONE, ErrorCode.TOKEN_EXPIRED_OR_REVOKED))
        assertIs<MageRideError.PayloadTooLarge>(problemFor(TOO_LARGE, ErrorCode.PAYLOAD_TOO_LARGE))
        assertIs<MageRideError.Unprocessable>(problemFor(UNPROCESSABLE, ErrorCode.ROUTE_UNAVAILABLE))
        assertIs<MageRideError.Locked>(problemFor(LOCKED, ErrorCode.OTP_LOCKED))
        assertIs<MageRideError.UpgradeRequired>(problemFor(UPGRADE, ErrorCode.UPGRADE_REQUIRED))
        assertIs<MageRideError.RateLimited>(problemFor(TOO_MANY, ErrorCode.RATE_LIMITED))
        assertIs<MageRideError.Server>(problemFor(SERVER_ERROR, ErrorCode.INTERNAL_ERROR))
        assertIs<MageRideError.Server>(problemFor(GATEWAY_TIMEOUT, ErrorCode.UPSTREAM_TIMEOUT))
        assertIs<MageRideError.Unexpected>(problemFor(TEAPOT, ErrorCode.BAD_REQUEST))
    }

    @Test
    fun a_code_this_build_does_not_know_still_produces_a_usable_error() = runTest {
        // A service may register a new code at start-up (C002). Failing to parse the body that
        // explains the failure would be the worst possible reaction.
        val test = testApi { _, _ -> respondProblem(HttpStatusCode.Conflict, "a-code-from-the-future") }

        val error = assertFailsWith<MageRideError.Conflict> { test.api.ride.getRide("01RIDE") }

        assertNull(error.code, "unknown codes resolve to null, not to an exception")
        assertEquals("a-code-from-the-future", error.wireCode)
    }

    @Test
    fun a_non_problem_error_body_keeps_the_status_and_falls_back_to_a_kernel_code() = runTest {
        // A load balancer or captive portal can answer HTML. The status is still the truth.
        val test = testApi { _, _ ->
            respond("<html>gateway</html>", HttpStatusCode.BadGateway, headersOf("Content-Type", "text/html"))
        }

        val error = assertFailsWith<MageRideError.Server> { test.api.ride.getRide("01RIDE") }

        assertEquals(HttpStatusCode.BadGateway.value, error.status)
        assertEquals(ErrorCode.INTERNAL_ERROR, error.code)
    }

    @Test
    fun a_validation_failure_carries_its_field_level_detail() = runTest {
        val test = testApi { _, _ ->
            respondProblem(
                status = HttpStatusCode.BadRequest,
                code = ErrorCode.VALIDATION_FAILED.wire,
                extensions = """"errors":{"phone":["must be a Sri Lankan mobile number"]}""",
            )
        }

        val error = assertFailsWith<MageRideError.BadRequest> { test.api.ride.getRide("01RIDE") }

        assertEquals(listOf("must be a Sri Lankan mobile number"), error.problem.errors?.get("phone"))
    }

    @Test
    fun a_malformed_success_body_is_a_serialization_error_not_a_network_one() = runTest {
        val test = testApi { _, _ -> respondJson("""{"unexpected":"shape"}""") }

        val error = assertFailsWith<MageRideError.Serialization> { test.api.ride.getRide("01RIDE") }

        assertNull(error.code)
        assertTrue(error.cause != null)
    }

    private fun problemFor(status: Int, code: ErrorCode): MageRideError.Api = MageRideError.of(
        ProblemDetails(type = code.typeUri, title = code.wire, status = status),
    )

    private companion object {
        const val BAD_REQUEST = 400
        const val UNAUTHORIZED = 401
        const val PAYMENT_REQUIRED = 402
        const val FORBIDDEN = 403
        const val NOT_FOUND = 404
        const val CONFLICT = 409
        const val GONE = 410
        const val TOO_LARGE = 413
        const val UNPROCESSABLE = 422
        const val LOCKED = 423
        const val UPGRADE = 426
        const val TOO_MANY = 429
        const val TEAPOT = 418
        const val SERVER_ERROR = 500
        const val GATEWAY_TIMEOUT = 504
    }
}
