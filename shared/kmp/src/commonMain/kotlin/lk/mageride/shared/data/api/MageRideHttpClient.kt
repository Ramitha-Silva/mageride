package lk.mageride.shared.data.api

import io.ktor.client.HttpClient
import io.ktor.client.call.HttpClientCall
import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.plugins.HttpRequestTimeoutException
import io.ktor.client.plugins.HttpSend
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.logging.LogLevel
import io.ktor.client.plugins.logging.Logger
import io.ktor.client.plugins.logging.Logging
import io.ktor.client.plugins.plugin
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpMethod
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.util.AttributeKey
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.serialization.json.Json
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.serialization.MageRideJson
import kotlin.random.Random
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

// ------------------------------------------------------------------------------------------
// Request attributes. Set by the typed clients (see ApiTransport.kt), read by the send pipeline.
// ------------------------------------------------------------------------------------------

/** Which contract file the call belongs to. Keys the circuit breaker (D6' §8.3). */
internal val ApiServiceAttribute: AttributeKey<ApiService> = AttributeKey("MageRideApiService")

/** The contract's `operationId`, used to scope an attestation verdict and to label a log line. */
internal val OperationIdAttribute: AttributeKey<String> = AttributeKey("MageRideOperationId")

/** Which credential the operation's `security` block declares. */
internal val CredentialAttribute: AttributeKey<Credential> = AttributeKey("MageRideCredential")

/** Present when the operation declares `X-Attestation` (D-30). */
internal val AttestedAttribute: AttributeKey<Boolean> = AttributeKey("MageRideAttested")

/**
 * What the pipeline should put in `Authorization`, mirroring the operation's `security` block.
 */
internal enum class Credential {
    /** `bearerAuth` — the 30-minute RS256 access token, refreshed once on a `401`. */
    ACCESS_TOKEN,

    /** `security: []` — a deliberately public route; send nothing. */
    NONE,

    /**
     * The call set its own `Authorization` and owns it.
     *
     * `POST /v1/auth/refresh` presents the opaque refresh token this way; refreshing a refresh
     * would be a loop, so the pipeline neither overwrites the header nor retries the `401`.
     */
    PROVIDED,
}

/** Header names the platform edge defines (D3' §0, `_shared.yaml#/components/parameters`). */
internal object MageRideHeaders {
    const val IDEMPOTENCY_KEY: String = "Idempotency-Key"
    const val APP_VERSION: String = "X-App-Version"
    const val PLATFORM: String = "X-Platform"
    const val ATTESTATION: String = "X-Attestation"
}

/**
 * Builds the one `HttpClient` every typed client shares.
 *
 * The engine is a parameter because there is no multiplatform one: `androidMain` passes OkHttp,
 * `iosMain` passes Darwin, and `commonTest` passes `MockEngine`. Everything else — JSON, the
 * timeouts, the retry/breaker pipeline, the auth replay, the RFC 7807 mapping — is the same on
 * every target, which is the point of having it here rather than four times over.
 *
 * @param engine Platform HTTP engine.
 * @param config Gateway, build identity and the D6' §8.3 policies.
 * @param tokens Session credential; [TokenProvider.Anonymous] until C014 binds the real one.
 * @param attestation Play Integrity / App Attest supplier (D-30).
 * @param signals Where a `426` is published, in addition to being thrown.
 * @param breaker Shared per-service breaker. Pass one in to share it across clients.
 * @param json Wire format. Always [MageRideJson] outside tests — REST and MQTT must not drift.
 * @param logSink Sink for the Ktor logging plugin.
 * @param random Jitter source; seed it to make a backoff test deterministic.
 */
@OptIn(ExperimentalTime::class)
public fun mageRideHttpClient(
    engine: HttpClientEngine,
    config: ApiConfig,
    tokens: TokenProvider = TokenProvider.Anonymous,
    attestation: AttestationProvider = AttestationProvider.Unavailable,
    signals: MageRideApiSignals = MageRideApiSignals(),
    breaker: CircuitBreaker = CircuitBreaker(
        config.circuitBreaker,
        { Clock.System.now().toEpochMilliseconds() },
    ),
    json: Json = MageRideJson,
    logSink: (String) -> Unit = ::println,
    random: Random = Random.Default,
): HttpClient {
    val client = HttpClient(engine) {
        // `expectSuccess` stays false: this client maps a non-2xx itself, in one place, into the
        // D3' §0 problem model. Ktor's own ClientRequestException would lose the kebab code.
        expectSuccess = false

        // No contract route redirects except `GET /v1/admin/transit/gtfs/versions/{id}/download`,
        // whose whole payload *is* the `Location` header — a short-lived signed object-storage
        // URL the operator's browser follows, not us. Following redirects automatically would
        // swallow that header and stream a GTFS zip through the JSON pipeline. Anywhere else a
        // `/v1/...` route answering `3xx` is a misconfigured gateway, and surfacing it beats
        // chasing it.
        followRedirects = false

        install(ContentNegotiation) {
            json(json)
            // 4xx/5xx are served as application/problem+json (D3' §0), which is a different
            // media type and would otherwise fail content negotiation on the way in.
            json(json, ContentType("application", "problem+json"))
        }

        install(HttpTimeout) {
            requestTimeoutMillis = config.timeouts.requestTimeout.inWholeMilliseconds
            connectTimeoutMillis = config.timeouts.connectTimeout.inWholeMilliseconds
            socketTimeoutMillis = config.timeouts.socketTimeout.inWholeMilliseconds
        }

        if (config.logLevel != ApiLogLevel.NONE) {
            install(Logging) {
                this.logger = object : Logger {
                    override fun log(message: String) = logSink(message)
                }
                level = config.logLevel.toKtorLevel()
                // A logged bearer token is a logged session, and a logged attestation verdict is
                // a replayable one. Redact both at every level, including BODY.
                sanitizeHeader { header -> header == HttpHeaders.Authorization }
                sanitizeHeader { header -> header == MageRideHeaders.ATTESTATION }
            }
        }
    }

    val pipeline = CallPipeline(config, tokens, attestation, signals, breaker, json, random)
    client.plugin(HttpSend).intercept { request ->
        val service = request.attributes.getOrNull(ApiServiceAttribute)
            ?: return@intercept execute(request)
        pipeline.run(service, request) { execute(it) }
    }

    return client
}

private fun ApiLogLevel.toKtorLevel(): LogLevel = when (this) {
    ApiLogLevel.NONE -> LogLevel.NONE
    ApiLogLevel.INFO -> LogLevel.INFO
    ApiLogLevel.HEADERS -> LogLevel.HEADERS
    ApiLogLevel.BODY -> LogLevel.BODY
}

/**
 * The whole send-side pipeline, in the one place where the ordering is visible.
 *
 * Outermost to innermost:
 * 1. **Attestation** (D-30) — resolved once per call, before anything is sent.
 * 2. **Circuit breaker** (D6' §8.3) — refuses the call outright while the service is out.
 * 3. **Retry with backoff and jitter** (D6' §8.3) — three attempts, only for a request that is
 *    safe to repeat.
 * 4. **Auth** (D-29) — attaches the bearer before every attempt and, on a `401`, refreshes once
 *    and replays. The replay is not one of the three attempts.
 * 5. **RFC 7807 mapping** (D3' §0) — a 4xx/5xx becomes a typed [MageRideError]; a `426` is also
 *    published on [MageRideApiSignals.upgradeRequired].
 *
 * The `Idempotency-Key` is not in this list on purpose: it is written into the request builder
 * *before* the pipeline runs (see `ApiTransport.kt`), and every attempt and every replay reuses
 * the same builder. That is what makes a retried POST a replay rather than a second command
 * (R-14, R-18).
 */
private class CallPipeline(
    private val config: ApiConfig,
    private val tokens: TokenProvider,
    private val attestation: AttestationProvider,
    private val signals: MageRideApiSignals,
    private val breaker: CircuitBreaker,
    private val json: Json,
    private val random: Random,
) {

    suspend fun run(
        service: ApiService,
        request: HttpRequestBuilder,
        send: suspend (HttpRequestBuilder) -> HttpClientCall,
    ): HttpClientCall {
        applyAttestation(request)
        breaker.onCallStarted(service)
        val budget = RetryBudget(config.retry, request.isRetrySafe(), random)
        val call = attempt(service, request, send, budget, refreshed = false)
        throwOnProblem(call)
        return call
    }

    /** Resolves the `X-Attestation` header for the twenty operations D3' §0 calls sensitive. */
    private suspend fun applyAttestation(request: HttpRequestBuilder) {
        if (request.attributes.getOrNull(AttestedAttribute) != true) return
        val operationId = request.attributes.getOrNull(OperationIdAttribute).orEmpty()
        val token = attestation.attestationToken(operationId) ?: return
        request.headers[MageRideHeaders.ATTESTATION] = token
    }

    /**
     * One send, and the decision about what to do with its outcome.
     *
     * Written as tail recursion rather than a loop: "retry after a backoff" and "refresh and
     * replay" are two different reasons to send again, and a loop with a flag for each reads worse
     * than a call that says which one it is. The depth is bounded by
     * [RetryPolicy.maxAttempts] plus the single refresh replay.
     *
     * @param refreshed Whether the credential has already been refreshed for this call. The
     *   replay after a refresh does not consume a retry attempt, and there is never a second one.
     */
    @Suppress("ReturnCount", "TooGenericExceptionCaught")
    private suspend fun attempt(
        service: ApiService,
        request: HttpRequestBuilder,
        send: suspend (HttpRequestBuilder) -> HttpClientCall,
        budget: RetryBudget,
        refreshed: Boolean,
    ): HttpClientCall {
        attachCredential(request)
        val call = try {
            send(request)
        } catch (cause: CancellationException) {
            breaker.onCallFinished(service, failed = false)
            throw cause
        } catch (cause: Throwable) {
            if (!budget.canRetry()) {
                breaker.onCallFinished(service, failed = true)
                throw cause.asMageRideError()
            }
            budget.await(retryAfterSeconds = null)
            return attempt(service, request, send, budget, refreshed)
        }

        val status = call.response.status
        if (status == HttpStatusCode.Unauthorized && request.credential() == Credential.ACCESS_TOKEN) {
            if (!refreshed && tokens.refresh()) {
                return attempt(service, request, send, budget, refreshed = true)
            }
            tokens.onAuthenticationLost()
            breaker.onCallFinished(service, failed = false)
            return call
        }

        if (budget.canRetry() && status.isTransient()) {
            budget.await(call.response.retryAfterSeconds())
            return attempt(service, request, send, budget, refreshed)
        }

        breaker.onCallFinished(service, failed = status.value >= HttpStatusCode.InternalServerError.value)
        return call
    }

    private suspend fun attachCredential(request: HttpRequestBuilder) {
        if (request.credential() != Credential.ACCESS_TOKEN) return
        val token = tokens.accessToken() ?: return
        // `set`, not `append`: the same builder is reused by every attempt and by the refresh
        // replay, and appending would send two Authorization headers on the second try.
        request.headers[HttpHeaders.Authorization] = "Bearer $token"
    }

    private suspend fun throwOnProblem(call: HttpClientCall) {
        val response = call.response
        if (response.status.value < HttpStatusCode.BadRequest.value) return
        val problem = response.readProblem(json)
        val error = MageRideError.of(problem, response.retryAfterSeconds())
        if (error is MageRideError.UpgradeRequired) signals.publishUpgradeRequired(error.toSignal())
        throw error
    }

    private fun HttpRequestBuilder.credential(): Credential =
        attributes.getOrNull(CredentialAttribute) ?: Credential.ACCESS_TOKEN
}

/**
 * The remaining retry allowance for one logical call (D6' §8.3).
 *
 * Holds the attempt counter so [CallPipeline.attempt] does not have to thread it through its own
 * recursion, and so "is this request even safe to repeat?" is decided once, up front.
 */
private class RetryBudget(
    private val policy: RetryPolicy,
    private val retrySafe: Boolean,
    private val random: Random,
) {
    private var attempts = 1

    /** Whether another send is both allowed by the policy and safe for this request. */
    fun canRetry(): Boolean = retrySafe && attempts < policy.maxAttempts

    /** Waits out the backoff and spends one attempt. */
    suspend fun await(retryAfterSeconds: Int?) {
        delay(policy.backoffFor(attempts, retryAfterSeconds, random))
        attempts++
    }
}

/**
 * Whether repeating this exact request is safe.
 *
 * GET/HEAD/PUT/DELETE/OPTIONS are idempotent by HTTP. A POST is only safe because it carries an
 * `Idempotency-Key` and the service replays the original response from its command log
 * (D3' §0, R-14/R-18) — so the six HMAC-signed provider callbacks, which are `x-idempotency-exempt`
 * and dedupe on `provider_transaction_id` instead (R-19), are never retried here.
 */
private fun HttpRequestBuilder.isRetrySafe(): Boolean =
    method in IDEMPOTENT_METHODS || headers.contains(MageRideHeaders.IDEMPOTENCY_KEY)

private val IDEMPOTENT_METHODS = setOf(
    HttpMethod.Get,
    HttpMethod.Head,
    HttpMethod.Put,
    HttpMethod.Delete,
    HttpMethod.Options,
)

/** The statuses D6' §8.3 calls transient. Any other 4xx is the service working and saying no. */
private fun HttpStatusCode.isTransient(): Boolean = this in TRANSIENT_STATUSES

private val TRANSIENT_STATUSES = setOf(
    HttpStatusCode.RequestTimeout,
    HttpStatusCode.TooManyRequests,
    HttpStatusCode.InternalServerError,
    HttpStatusCode.BadGateway,
    HttpStatusCode.ServiceUnavailable,
    HttpStatusCode.GatewayTimeout,
)

private fun HttpResponse.retryAfterSeconds(): Int? = headers[HttpHeaders.RetryAfter]?.trim()?.toIntOrNull()

/**
 * Reads the RFC 7807 body, or synthesises one.
 *
 * A gateway, a load balancer or a captive portal can answer a MageRide URL with something that
 * is not problem+json. Failing to parse that must not replace the status the caller needs with a
 * `SerializationException`, so the status is preserved and the kebab code falls back to the
 * kernel code for that status class (C002).
 */
@Suppress("TooGenericExceptionCaught")
private suspend fun HttpResponse.readProblem(json: Json): ProblemDetails {
    val text = try {
        bodyAsText()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        ""
    }
    if (text.isNotBlank()) {
        val parsed = try {
            json.decodeFromString(ProblemDetails.serializer(), text)
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            null
        }
        if (parsed != null) return parsed
    }
    return ProblemDetails(
        type = ProblemDetails.TYPE_PREFIX + fallbackCode(status),
        title = status.description,
        status = status.value,
    )
}

/**
 * The kernel error code (C002) that stands in when the body was not a Problem.
 *
 * Never invents a code: every value here is already in
 * `_shared.yaml#/components/schemas/ErrorCode`, so [ProblemDetails.errorCode] still resolves and
 * the caller's `when` still works.
 */
private fun fallbackCode(status: HttpStatusCode): String = when (status) {
    HttpStatusCode.BadRequest -> ErrorCode.BAD_REQUEST.wire
    HttpStatusCode.Unauthorized -> ErrorCode.UNAUTHORIZED.wire
    HttpStatusCode.Forbidden -> ErrorCode.FORBIDDEN.wire
    HttpStatusCode.NotFound -> ErrorCode.NOT_FOUND.wire
    HttpStatusCode.MethodNotAllowed -> ErrorCode.METHOD_NOT_ALLOWED.wire
    HttpStatusCode.Conflict -> ErrorCode.CONFLICT.wire
    HttpStatusCode.PayloadTooLarge -> ErrorCode.PAYLOAD_TOO_LARGE.wire
    HttpStatusCode.UnsupportedMediaType -> ErrorCode.UNSUPPORTED_MEDIA_TYPE.wire
    HttpStatusCode.UpgradeRequired -> ErrorCode.UPGRADE_REQUIRED.wire
    HttpStatusCode.TooManyRequests -> ErrorCode.RATE_LIMITED.wire
    HttpStatusCode.ServiceUnavailable -> ErrorCode.SERVICE_UNAVAILABLE.wire
    HttpStatusCode.GatewayTimeout -> ErrorCode.UPSTREAM_TIMEOUT.wire
    else -> ErrorCode.INTERNAL_ERROR.wire
}

/** Maps whatever the engine threw onto the transport arm of [MageRideError]. */
internal fun Throwable.asMageRideError(): MageRideError = when (this) {
    is MageRideError -> this
    is HttpRequestTimeoutException -> MageRideError.Timeout(this)
    else -> MageRideError.Network(this)
}
