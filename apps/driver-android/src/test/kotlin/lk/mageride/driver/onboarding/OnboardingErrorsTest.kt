package lk.mageride.driver.onboarding

import lk.mageride.driver.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import java.io.IOException
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * D-26's copy table, and the hole that used to be in it.
 *
 * `OnboardingErrors` covered the codes each screen group's own contracts declare and nothing else,
 * so the **cross-cutting** half of the registry — the codes every call can answer, `forbidden` and
 * `unauthorized` and the 5xx family — all fell through to `error_generic`. On SCR-DA-010 that is
 * the *"Something went wrong. Please try again."* banner above the map: one sentence for a dozen
 * failures that need different answers from the driver, and for the one that is actually common on
 * a first run (`403 forbidden` from `GET /v1/vehicles/mine`, on an account that never got the
 * `driver` role) the least useful of them.
 *
 * The tests below are the table, asserted. The last one is the rule: **no code in the registry may
 * resolve to the generic message just because nobody wrote a row for it.**
 */
class OnboardingErrorsTest {

    @Test
    fun the_cross_cutting_codes_each_resolve_to_their_own_copy() {
        assertEquals(R.string.error_forbidden, copyFor(403, ErrorCode.FORBIDDEN))
        assertEquals(R.string.session_expired, copyFor(401, ErrorCode.UNAUTHORIZED))
        assertEquals(R.string.error_validation_failed, copyFor(400, ErrorCode.BAD_REQUEST))
        assertEquals(R.string.error_upgrade_required, copyFor(426, ErrorCode.UPGRADE_REQUIRED))
        assertEquals(R.string.error_attestation_failed, copyFor(401, ErrorCode.ATTESTATION_FAILED))

        // Ours rather than the driver's, and the only family where "try again" is right advice.
        assertEquals(R.string.error_service_down, copyFor(500, ErrorCode.INTERNAL_ERROR))
        assertEquals(R.string.error_service_down, copyFor(503, ErrorCode.SERVICE_UNAVAILABLE))
        assertEquals(R.string.error_service_down, copyFor(503, ErrorCode.DEPENDENCY_UNAVAILABLE))
        assertEquals(R.string.error_service_down, copyFor(504, ErrorCode.UPSTREAM_TIMEOUT))
    }

    @Test
    fun a_code_this_build_predates_falls_back_to_the_status_rather_than_to_the_generic_message() {
        // `ErrorCode.fromWire` answers `null` for a code a service registered after this build was
        // cut (`MageRideErrors.Register`, C002) — deliberately, so the body still parses. The type
        // is what is left to branch on, and it is enough to tell a driver which kind of failure it
        // was.
        val unknown = MageRideError.of(ProblemDetails(type = TYPE + "not-a-code-yet", title = "?", status = 403))

        assertEquals(null, unknown.code, "the fixture must be a code this build does not know")
        assertEquals(R.string.error_forbidden, OnboardingErrors.messageFor(unknown))
    }

    @Test
    fun a_5xx_with_no_mageride_body_at_all_still_reads_as_a_platform_failure() {
        // A 502 from something between the app and the gateway carries whatever body that hop
        // writes, so `readProblem` synthesises one and there is no kebab code in it.
        val bare = MageRideError.of(ProblemDetails(type = "about:blank", title = "Bad Gateway", status = 502))

        assertEquals(R.string.error_service_down, OnboardingErrors.messageFor(bare))
    }

    @Test
    fun the_transport_failures_and_a_plain_throwable_are_unchanged() {
        assertEquals(R.string.error_offline, OnboardingErrors.messageFor(MageRideError.Network(IOException())))
        assertEquals(R.string.error_offline, OnboardingErrors.messageFor(MageRideError.Timeout(IOException())))

        // Δ MCS-15 — `Serialization` has its own copy now, and this assertion is REVERSED.
        //
        // It used to expect `error_generic`, and the reason recorded beside it was that "'try
        // again in a moment' would be a promise this app cannot keep — the body will be the same
        // shape next time". That reasoning is right and it argued against its own assertion:
        // `error_generic` IS "Something went wrong. Please try again." The test asked for a message
        // that does not promise a retry and then pinned the one that does.
        //
        // It cost a real driver a day. When registry-svc started returning `auto_verified` — a
        // member the KMP `VerifyStatus` enum did not have — SCR-DA-003a showed "Something went
        // wrong. Please try again." over an HTTP 200, and retrying re-sent the same images to the
        // same defect for as long as anyone was willing to keep tapping.
        //
        // `error_malformed_response` says what the comment always meant: the app could not read the
        // reply, update it or contact support. Same argument as `PayloadTooLarge` one branch above.
        assertEquals(
            R.string.error_malformed_response,
            OnboardingErrors.messageFor(MageRideError.Serialization(IOException())),
        )

        // An unexpected throwable is still the generic message: nothing is known about it, so
        // there is nothing better to say.
        assertEquals(R.string.error_generic, OnboardingErrors.messageFor(IllegalStateException("boom")))
    }

    @Test
    fun the_screen_group_tables_still_win_over_the_cross_cutting_one() {
        // `not-found` and `conflict` are cross-cutting keys that the wallet cluster claims with
        // copy of its own (C073). Order matters: the specific table is consulted first.
        assertEquals(R.string.error_not_found, copyFor(404, ErrorCode.NOT_FOUND))
        assertEquals(R.string.error_already_done, copyFor(409, ErrorCode.CONFLICT))
        assertEquals(R.string.error_otp_invalid, copyFor(400, ErrorCode.INVALID_OTP))
        assertEquals(R.string.error_not_online, copyFor(403, ErrorCode.NOT_ONLINE))
    }

    @Test
    fun every_cross_cutting_code_has_copy_of_its_own() {
        // The C002 block of `ErrorCode` — the codes that belong to no service and can therefore
        // arrive on any screen. This map IS the table; a new kernel code added without a row fails
        // here rather than shipping as "Something went wrong" on whichever screen meets it first.
        //
        // Each is asserted at the status it actually arrives with, because the status decides the
        // `MageRideError` subclass and for two of them that is the whole point: `attestation-failed`
        // is a 401 that must not read as a lost session, and `rate-limited` is a 429 that must not
        // read as a server fault.
        val table = mapOf(
            Triple(400, ErrorCode.VALIDATION_FAILED, "validation") to R.string.error_validation_failed,
            Triple(400, ErrorCode.BAD_REQUEST, "bad request") to R.string.error_validation_failed,
            Triple(401, ErrorCode.UNAUTHORIZED, "no session") to R.string.session_expired,
            Triple(401, ErrorCode.ATTESTATION_FAILED, "integrity") to R.string.error_attestation_failed,
            Triple(403, ErrorCode.FORBIDDEN, "no role") to R.string.error_forbidden,
            Triple(404, ErrorCode.NOT_FOUND, "no row") to R.string.error_not_found,
            Triple(409, ErrorCode.CONFLICT, "already done") to R.string.error_already_done,
            Triple(413, ErrorCode.PAYLOAD_TOO_LARGE, "too big") to R.string.error_image_too_large,
            Triple(426, ErrorCode.UPGRADE_REQUIRED, "old build") to R.string.error_upgrade_required,
            Triple(429, ErrorCode.RATE_LIMITED, "bucket empty") to R.string.error_otp_rate_limited,
            Triple(500, ErrorCode.INTERNAL_ERROR, "our fault") to R.string.error_service_down,
            Triple(502, ErrorCode.DEPENDENCY_UNAVAILABLE, "a hop is out") to R.string.error_service_down,
            Triple(503, ErrorCode.SERVICE_UNAVAILABLE, "service out") to R.string.error_service_down,
            Triple(504, ErrorCode.UPSTREAM_TIMEOUT, "slow hop") to R.string.error_service_down,
        )

        table.forEach { (arrival, expected) ->
            val (status, code, what) = arrival
            assertEquals(expected, copyFor(status, code), "$status ${code.wire} ($what)")
        }
    }

    @Test
    fun the_codes_that_are_this_apps_own_bug_keep_the_generic_message() {
        // R-14/R-18's four, plus the two that mean this client built a request the route does not
        // accept. Every one of them is a defect in the app rather than a situation the driver is
        // in, and there is no advice to give about one beyond what the generic message says. They
        // are listed here so the omission is a decision on the record and not a missing row.
        listOf(
            ErrorCode.IDEMPOTENCY_KEY_REQUIRED,
            ErrorCode.IDEMPOTENCY_KEY_INVALID,
            ErrorCode.IDEMPOTENCY_KEY_REUSE,
            ErrorCode.IDEMPOTENCY_IN_PROGRESS,
            ErrorCode.METHOD_NOT_ALLOWED,
            ErrorCode.UNSUPPORTED_MEDIA_TYPE,
        ).forEach { code ->
            assertEquals(R.string.error_generic, copyFor(422, code), code.wire)
        }
    }

    /**
     * The copy for [code] as it arrives — through a real [ProblemDetails], not by calling the
     * private table.
     *
     * [status] decides which `MageRideError` subclass the body becomes, and for four of the codes
     * asserted here that is the whole point: `attestation-failed` is a `401` that must not read as
     * a lost session, and `upgrade-required` is a `426` that must not read as a server fault.
     */
    private fun copyFor(status: Int, code: ErrorCode): Int = OnboardingErrors.messageFor(
        MageRideError.of(ProblemDetails(type = TYPE + code.wire, title = code.wire, status = status)),
    )

    private companion object {
        const val TYPE = ProblemDetails.TYPE_PREFIX
    }
}
