package lk.mageride.shared.data.api

import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.MockRequestHandleScope
import io.ktor.client.engine.mock.respond
import io.ktor.client.engine.mock.toByteArray
import io.ktor.client.request.HttpRequestData
import io.ktor.client.request.HttpResponseData
import io.ktor.http.Headers
import io.ktor.http.HttpStatusCode
import io.ktor.http.Parameters
import io.ktor.http.headersOf
import lk.mageride.shared.data.models.ClientPlatform
import lk.mageride.shared.data.models.ProblemDetails
import kotlin.random.Random

// MockEngine plumbing shared by the C013 tests.
//
// The point of every test in this package is the *pipeline*, not the DTOs — C012 already asserts
// the shapes. So the assertions here are about what left the device (method, path, query, headers,
// body) and what the caller was handed back (a typed error, a signal, a decoded value).

/** One request as the engine saw it. */
internal class RecordedRequest(
    val method: String,
    val path: String,
    val query: Parameters,
    val headers: Headers,
    val contentType: String?,
    val body: String,
) {
    /** The `Idempotency-Key` header, or `null` when the request carried none. */
    val idempotencyKey: String? get() = headers[MageRideHeaders.IDEMPOTENCY_KEY]

    /** The `Authorization` header, or `null`. */
    val authorization: String? get() = headers["Authorization"]
}

/** A client wired to a MockEngine, with everything a test needs to poke at it. */
internal class TestApi(
    val api: MageRideApi,
    val transport: ApiTransport,
    val signals: MageRideApiSignals,
    val requests: List<RecordedRequest>,
)

internal const val TEST_BASE_URL: String = "https://api.test.mageride.lk"
internal const val TEST_APP_VERSION: String = "1.4.0"

internal fun testConfig(
    retry: RetryPolicy = RetryPolicy(),
    circuitBreaker: CircuitBreakerPolicy = CircuitBreakerPolicy(),
    logLevel: ApiLogLevel = ApiLogLevel.NONE,
): ApiConfig = ApiConfig(
    baseUrl = TEST_BASE_URL,
    appVersion = TEST_APP_VERSION,
    platform = ClientPlatform.ANDROID,
    retry = retry,
    circuitBreaker = circuitBreaker,
    logLevel = logLevel,
)

/**
 * Builds a client whose engine answers with [respond].
 *
 * @param respond Receives the zero-based attempt index, so a test can fail the first send and
 *   succeed on the second without keeping its own counter.
 */
@Suppress("LongParameterList")
internal fun testApi(
    config: ApiConfig = testConfig(),
    tokens: TokenProvider = TokenProvider.Anonymous,
    attestation: AttestationProvider = AttestationProvider.Unavailable,
    signals: MageRideApiSignals = MageRideApiSignals(),
    breaker: CircuitBreaker = CircuitBreaker(config.circuitBreaker) { 0L },
    idempotencyKeys: IdempotencyKeyGenerator = SequentialIdempotencyKeys(),
    random: Random = Random(SEED),
    respond: suspend MockRequestHandleScope.(Int, HttpRequestData) -> HttpResponseData,
): TestApi {
    val recorded = mutableListOf<RecordedRequest>()
    val engine = MockEngine { request ->
        val index = recorded.size
        recorded += RecordedRequest(
            method = request.method.value,
            path = request.url.encodedPath,
            query = request.url.parameters,
            headers = request.headers,
            // Ktor carries the request's own media type on the body, not in `headers` — a
            // multipart boundary is part of the OutgoingContent, not something a caller sets.
            contentType = request.body.contentType?.toString(),
            body = request.body.toByteArray().decodeToString(),
        )
        respond(index, request)
    }
    val client = mageRideHttpClient(
        engine = engine,
        config = config,
        tokens = tokens,
        attestation = attestation,
        signals = signals,
        breaker = breaker,
        random = random,
    )
    val transport = ApiTransport(client, config, idempotencyKeys)
    return TestApi(MageRideApi(transport, signals), transport, signals, recorded)
}

/** `200 application/json` with [body]. */
internal fun MockRequestHandleScope.respondJson(body: String, status: HttpStatusCode = HttpStatusCode.OK) =
    respond(body, status, headersOf("Content-Type", "application/json"))

/** `204`, no body — the shape of every delete and of `POST /v1/auth/logout`. */
internal fun MockRequestHandleScope.respondNoContent() = respond("", HttpStatusCode.NoContent, Headers.Empty)

/**
 * An RFC 7807 problem body, as the gateway serves them (D3' §0).
 *
 * @param extensions Raw JSON pairs appended to the object — the `426` trio, for instance.
 */
internal fun MockRequestHandleScope.respondProblem(
    status: HttpStatusCode,
    code: String,
    extensions: String = "",
    headers: Headers = Headers.Empty,
): HttpResponseData {
    val body = buildString {
        append("{")
        append("\"type\":\"").append(ProblemDetails.TYPE_PREFIX).append(code).append("\",")
        append("\"title\":\"").append(status.description).append("\",")
        append("\"status\":").append(status.value)
        if (extensions.isNotBlank()) append(",").append(extensions)
        append("}")
    }
    val merged = Headers.build {
        appendAll(headers)
        append("Content-Type", "application/problem+json")
    }
    return respond(body, status, merged)
}

/** Keys a test can predict, so "the retry reused the key" is an equality assertion. */
internal class SequentialIdempotencyKeys : IdempotencyKeyGenerator {
    private var minted = 0

    val count: Int get() = minted

    override fun next(): String {
        minted++
        return PREFIX + minted.toString().padStart(SUFFIX_WIDTH, '0')
    }

    private companion object {
        const val PREFIX = "TESTIDEMPOTENCYKEY"
        const val SUFFIX_WIDTH = 8
    }
}

/**
 * A [TokenProvider] that counts what the pipeline asked of it.
 *
 * C014 owns the real one; this exists so the "exactly one refresh, then give up" rule is asserted
 * here, where the rule lives.
 */
internal class FakeTokenProvider(
    initialToken: String? = "access-1",
    private val rotatedToken: String? = "access-2",
    private val refreshSucceeds: Boolean = true,
) : TokenProvider {
    private var token: String? = initialToken

    var refreshCalls: Int = 0
        private set

    var authenticationLostCalls: Int = 0
        private set

    /** The `staleAccessToken` values the pipeline reported, so a test can assert what failed. */
    val staleTokens: MutableList<String?> = mutableListOf()

    override suspend fun accessToken(): String? = token

    override suspend fun refresh(staleAccessToken: String?): Boolean {
        refreshCalls++
        staleTokens += staleAccessToken
        if (!refreshSucceeds) return false
        token = rotatedToken
        return true
    }

    override suspend fun onAuthenticationLost() {
        authenticationLostCalls++
    }
}

/** Seeded so a jitter assertion is reproducible. */
private const val SEED = 20260727
