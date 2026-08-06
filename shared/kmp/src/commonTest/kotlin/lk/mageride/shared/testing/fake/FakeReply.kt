package lk.mageride.shared.testing.fake

import io.ktor.http.Headers
import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.serialization.MageRideJson

/**
 * What the fake backend answers one call with.
 *
 * Deliberately at the HTTP level rather than the DTO level: half of what C013's pipeline does is
 * only observable from a status and a header — the `401` that triggers exactly one refresh, the
 * `426` that raises the update wall (D-31), the `409`/`410` pair that keeps "another driver won"
 * apart from "the fifteen seconds elapsed", the `Retry-After` a `429` carries. A reply expressed
 * as "a DTO" could not say any of that.
 *
 * @property status The HTTP status.
 * @property body The response body, or `null` for a bodiless reply.
 * @property headers Response headers. `Content-Type` is set for you by the factories below.
 */
public class FakeReply private constructor(
    public val status: HttpStatusCode,
    public val body: String?,
    public val headers: Headers,
) {
    public companion object {

        private const val JSON = "application/json"
        private const val PROBLEM_JSON = "application/problem+json"
        private const val IMAGE_JPEG = "image/jpeg"

        /** Deliberately not valid JSON: decoding it as a DTO must fail loudly, not half-work. */
        private const val BINARY_FIXTURE = "PNG\r\n\n mageride-test-fixture"

        /** A JSON body at [status]. */
        public fun json(body: JsonElement, status: HttpStatusCode = HttpStatusCode.OK): FakeReply =
            FakeReply(status, body.toString(), headersOf("Content-Type", JSON))

        /** A JSON body typed out as a string, for the tests that want to assert on malformed input. */
        public fun raw(body: String, status: HttpStatusCode = HttpStatusCode.OK): FakeReply =
            FakeReply(status, body, headersOf("Content-Type", JSON))

        /** A DTO, encoded the way the platform encodes it. */
        public inline fun <reified T> value(value: T, status: HttpStatusCode = HttpStatusCode.OK): FakeReply =
            raw(MageRideJson.encodeToString(value), status)

        /** A bodiless reply — the eleven `204`s, and any status a test wants to send empty. */
        public fun empty(
            status: HttpStatusCode = HttpStatusCode.NoContent,
            headers: Headers = Headers.Empty,
        ): FakeReply = FakeReply(status, body = null, headers = headers)

        /**
         * A reply whose body is **bytes**, not JSON (Δ C076a).
         *
         * The three reads that serve an object rather than a document — `getModeBFile`,
         * `getSupportScreenshot`, `downloadSignedGtfsObject` — declare `image/jpeg`, `image/png` or
         * `application/pdf`, and their clients return a `ByteArray`. What matters to a test is
         * that the bytes arrive and are *not* parseable as a DTO; the content is a fixture, so it
         * is short and recognisable rather than a real JPEG.
         *
         * @param content The payload. The default is enough to assert a non-empty read.
         * @param contentType What the platform would serve it as.
         */
        public fun binary(
            content: String = BINARY_FIXTURE,
            status: HttpStatusCode = HttpStatusCode.OK,
            contentType: String = IMAGE_JPEG,
        ): FakeReply = FakeReply(status, content, headersOf("Content-Type", contentType))

        /**
         * An RFC 7807 problem, as the gateway serves them (D3' §0).
         *
         * @param code The stable kebab error key — `offer-expired`, `insufficient-wallet`. It is
         *   what the apps resolve Si/Ta/En copy from (D-26), and what
         *   [lk.mageride.shared.data.api.MageRideError] branches on.
         * @param extensions Extra members alongside the RFC 7807 four — the `426` trio, a
         *   `retryAfter`, a `penalty`.
         */
        public fun problem(
            status: HttpStatusCode,
            code: String,
            extensions: JsonObject = JsonObject(emptyMap()),
            headers: Headers = Headers.Empty,
        ): FakeReply {
            val members = buildString {
                append("{\"type\":\"").append(ProblemDetails.TYPE_PREFIX).append(code).append("\",")
                append("\"title\":\"").append(status.description).append("\",")
                append("\"status\":").append(status.value)
                extensions.forEach { (key, value) -> append(",\"").append(key).append("\":").append(value) }
                append("}")
            }
            val merged = Headers.build {
                appendAll(headers)
                append("Content-Type", PROBLEM_JSON)
            }
            return FakeReply(status, members, merged)
        }
    }
}
