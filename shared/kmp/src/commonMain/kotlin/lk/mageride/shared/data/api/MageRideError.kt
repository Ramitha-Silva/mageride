package lk.mageride.shared.data.api

import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails

/**
 * Every failure this client can produce, as one closed hierarchy.
 *
 * **The contract's error model is RFC 7807** (D3' §0): a `application/problem+json` body whose
 * `type` is `https://mageride.lk/errors/{code}`, where `{code}` is the stable kebab key from
 * `_shared.yaml#/components/schemas/ErrorCode`. The status tells the caller *what kind* of
 * failure it is; the code tells it *which* failure. Both are preserved here, and neither is
 * collapsed into the other — a `409` can be `offer-already-accepted` or `version-conflict`, and
 * an app that retries the second must not retry the first.
 *
 * Branching is meant to happen on the type for the coarse decision and on [code] for the fine
 * one:
 * ```
 * when (val e = error) {
 *     is MageRideError.Gone     -> if (e.code == ErrorCode.OFFER_EXPIRED) showOfferGone()
 *     is MageRideError.Conflict -> if (e.code == ErrorCode.OFFER_ALREADY_ACCEPTED) showTaken()
 *     else                      -> showGeneric(e.code)
 * }
 * ```
 *
 * **Never render [message], `title` or `detail` to a user.** They are developer-facing English
 * by contract; the apps resolve their Si/Ta/En copy from [code] (D-26, CLAUDE.md "Trilingual
 * resources").
 *
 * @property code The stable kebab key, resolved against the registry. `null` when the transport
 *   failed before a problem body existed, or when the server registered a code this build
 *   predates (`MageRideErrors.Register`, C002) — see [ProblemDetails.errorCode].
 */
public sealed class MageRideError(message: String, cause: Throwable? = null) : Exception(message, cause) {

    public abstract val code: ErrorCode?

    // ------------------------------------------------------------------------------------------
    // Transport failures — no HTTP response, so no problem body and no code.
    // ------------------------------------------------------------------------------------------

    /**
     * The request never produced a response: DNS, TLS, connection reset, no network.
     *
     * Already retried per [RetryPolicy] by the time it surfaces, so treat it as "offline", not
     * as "try once more".
     */
    public class Network(cause: Throwable) : MageRideError("network failure", cause) {
        override val code: ErrorCode? get() = null
    }

    /**
     * The request exceeded the client deadline (D6' §8.3: API calls time out at 15 s).
     *
     * A timed-out **mutation may still have been applied** server-side. Replaying it with the
     * original `Idempotency-Key` is safe and is the correct recovery (R-14); minting a new key
     * is how a double charge happens.
     */
    public class Timeout(cause: Throwable) : MageRideError("request timed out", cause) {
        override val code: ErrorCode? get() = null
    }

    /** A 2xx body did not match the contract's schema. A contract violation, not a user error. */
    public class Serialization(cause: Throwable) : MageRideError("malformed response body", cause) {
        override val code: ErrorCode? get() = null
    }

    /**
     * The circuit breaker for [service] is open, so the call was never attempted (D6' §8.3).
     *
     * @property service The dependency whose breaker is open.
     * @property retryAfterMillis How long until the breaker admits its next probe.
     */
    public class CircuitOpen(public val service: ApiService, public val retryAfterMillis: Long) :
        MageRideError("circuit open for ${service.id}") {
        override val code: ErrorCode? get() = null
    }

    // ------------------------------------------------------------------------------------------
    // Problem-backed failures — a 4xx/5xx with an RFC 7807 body.
    // ------------------------------------------------------------------------------------------

    /**
     * A failure the server described with a Problem body.
     *
     * @property problem The parsed body. Synthesised with the transport's own `type` when the
     *   server (or something between us and it) answered with a non-problem body.
     */
    public sealed class Api(public val problem: ProblemDetails) :
        MageRideError("HTTP ${problem.status} ${problem.code}") {

        /** The HTTP status, 400–599. */
        public val status: Int get() = problem.status

        /** The stable kebab key exactly as the server spelled it, known to this build or not. */
        public val wireCode: String get() = problem.code

        override val code: ErrorCode? get() = problem.errorCode
    }

    /** `400` — validation or state error. `problem.errors` carries the field-level detail. */
    public class BadRequest(problem: ProblemDetails) : Api(problem)

    /** `401` — missing, expired or rejected credential. A refresh has already been attempted. */
    public class Unauthorized(problem: ProblemDetails) : Api(problem)

    /**
     * `401 attestation-failed` — Play Integrity / App Attest was rejected at the edge (D-30).
     *
     * Distinct from [Unauthorized] because the recovery is: re-acquire an attestation token, not
     * send the user back to the login screen.
     */
    public class AttestationFailed(problem: ProblemDetails) : Api(problem)

    /** `402` — the caller's wallet balance or merchant onboarding blocks the operation. */
    public class PaymentRequired(problem: ProblemDetails) : Api(problem)

    /** `403` — authenticated but not permitted (deny-by-default RBAC, AL-06). */
    public class Forbidden(problem: ProblemDetails) : Api(problem)

    /** `404` — no such resource, or none visible to this caller. */
    public class NotFound(problem: ProblemDetails) : Api(problem)

    /**
     * `409` — optimistic-concurrency, uniqueness or atomic-accept conflict.
     *
     * `offer-already-accepted` lands here: another driver won the race, the offer is *gone for
     * this driver* but the ride is very much alive. Contrast [Gone].
     */
    public class Conflict(problem: ProblemDetails) : Api(problem)

    /**
     * `410` — the resource existed and has expired.
     *
     * `offer-expired` lands here: the 15-second window elapsed and nobody took it. Deliberately
     * a different type from [Conflict] so "someone else got it" and "it timed out" cannot be
     * handled by one accidental branch.
     */
    public class Gone(problem: ProblemDetails) : Api(problem)

    /** `413` — upload above its ceiling. */
    public class PayloadTooLarge(problem: ProblemDetails) : Api(problem)

    /** `422` — well-formed but not satisfiable, e.g. no route between two points. */
    public class Unprocessable(problem: ProblemDetails) : Api(problem)

    /** `423` — attempt budget exhausted (OTP entry, package OTP). */
    public class Locked(problem: ProblemDetails) : Api(problem)

    /**
     * `426` — this build is below the per-platform floor (D-31, US-17.1/17.2).
     *
     * Every call can answer this, because the gateway applies the version gate before routing.
     * The same payload is also published on [MageRideApiSignals.upgradeRequired] so an app can
     * put up the update wall once, from one place, instead of at every call site.
     */
    public class UpgradeRequired(problem: ProblemDetails) : Api(problem) {
        /** Store link for the update. */
        public val updateUrl: String? get() = problem.updateUrl

        /** The newest published build for this platform. */
        public val latestVersion: String? get() = problem.latestVersion

        /** `true` blocks the client entirely; `false` is a soft nudge. */
        public val isMandatory: Boolean get() = problem.isMandatory ?: true

        /** The same three fields as a value, for [MageRideApiSignals]. */
        public fun toSignal(): UpgradeRequiredSignal =
            UpgradeRequiredSignal(latestVersion = latestVersion, updateUrl = updateUrl, isMandatory = isMandatory)
    }

    /**
     * `429` — a Redis token bucket refused the call.
     *
     * @property retryAfterSeconds The `Retry-After` header, when the gateway sent one. The
     *   retry policy has already waited it out for the attempts it was allowed; a surviving 429
     *   means the bucket is still empty.
     */
    public class RateLimited(problem: ProblemDetails, public val retryAfterSeconds: Int?) : Api(problem)

    /** `5xx` — an unhandled server failure. `problem.traceId` correlates with the service trace. */
    public class Server(problem: ProblemDetails) : Api(problem)

    /** A 4xx/5xx this client has no dedicated type for. Always inspect [status] and [code]. */
    public class Unexpected(problem: ProblemDetails) : Api(problem)

    public companion object {
        /**
         * Maps a Problem body onto the hierarchy, keyed on status first and on the stable code
         * only where the code changes what the caller must *do* (D-30's `attestation-failed`).
         *
         * @param problem The parsed — or synthesised — problem body.
         * @param retryAfterSeconds The `Retry-After` header value, for a `429`.
         */
        public fun of(problem: ProblemDetails, retryAfterSeconds: Int? = null): Api = when (problem.status) {
            HTTP_UNAUTHORIZED -> unauthorized(problem)
            HTTP_TOO_MANY_REQUESTS -> RateLimited(problem, retryAfterSeconds)
            else -> byStatus(problem)
        }

        private fun unauthorized(problem: ProblemDetails): Api =
            if (problem.errorCode == ErrorCode.ATTESTATION_FAILED) {
                AttestationFailed(problem)
            } else {
                Unauthorized(problem)
            }

        private fun byStatus(problem: ProblemDetails): Api = when (problem.status) {
            HTTP_BAD_REQUEST -> BadRequest(problem)
            HTTP_PAYMENT_REQUIRED -> PaymentRequired(problem)
            HTTP_FORBIDDEN -> Forbidden(problem)
            HTTP_NOT_FOUND -> NotFound(problem)
            HTTP_CONFLICT -> Conflict(problem)
            HTTP_GONE -> Gone(problem)
            HTTP_PAYLOAD_TOO_LARGE -> PayloadTooLarge(problem)
            HTTP_UNPROCESSABLE -> Unprocessable(problem)
            HTTP_LOCKED -> Locked(problem)
            HTTP_UPGRADE_REQUIRED -> UpgradeRequired(problem)
            else -> if (problem.status >= HTTP_INTERNAL_ERROR) Server(problem) else Unexpected(problem)
        }

        private const val HTTP_BAD_REQUEST = 400
        private const val HTTP_UNAUTHORIZED = 401
        private const val HTTP_PAYMENT_REQUIRED = 402
        private const val HTTP_FORBIDDEN = 403
        private const val HTTP_NOT_FOUND = 404
        private const val HTTP_CONFLICT = 409
        private const val HTTP_GONE = 410
        private const val HTTP_PAYLOAD_TOO_LARGE = 413
        private const val HTTP_UNPROCESSABLE = 422
        private const val HTTP_LOCKED = 423
        private const val HTTP_UPGRADE_REQUIRED = 426
        private const val HTTP_TOO_MANY_REQUESTS = 429
        private const val HTTP_INTERNAL_ERROR = 500
    }
}
