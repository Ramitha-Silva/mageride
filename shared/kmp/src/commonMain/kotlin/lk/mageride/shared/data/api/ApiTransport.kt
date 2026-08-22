package lk.mageride.shared.data.api

import io.ktor.client.HttpClient
import io.ktor.client.plugins.timeout
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.header
import io.ktor.client.request.request
import io.ktor.client.statement.HttpResponse
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpMethod
import kotlinx.serialization.json.Json
import lk.mageride.shared.serialization.MageRideJson
import kotlin.time.Duration

/**
 * The one place a MageRide HTTP request is built.
 *
 * Every typed client goes through here, so the conventions D3' §0 makes universal are applied
 * once instead of 176 times: the absolute URL, the `X-App-Version`/`X-Platform` version-gate
 * pair (D-31), the `Idempotency-Key` on POST mutations (R-14/R-18), and the attributes the send
 * pipeline reads to pick a credential, a breaker and an attestation scope.
 *
 * @property http The configured client from [mageRideHttpClient].
 * @property config Gateway and build identity.
 * @property idempotencyKeys Mints the `Idempotency-Key` when the caller does not supply one.
 */
public class ApiTransport(
    internal val http: HttpClient,
    internal val config: ApiConfig,
    internal val idempotencyKeys: IdempotencyKeyGenerator = UlidIdempotencyKeyGenerator(),
    internal val json: Json = MageRideJson,
)

// ----------------------------------------------------------------------------------------------
// Request builders — one per HTTP verb the contracts use.
//
// `operationId` is the contract's own, verbatim: it scopes the attestation verdict, labels the
// log line, and makes a client function traceable back to the YAML that justifies it.
// ----------------------------------------------------------------------------------------------

internal suspend fun ApiTransport.apiRequest(
    service: ApiService,
    operationId: String,
    method: HttpMethod,
    path: String,
    idempotencyKey: String? = null,
    keyed: Boolean = false,
    attested: Boolean = false,
    credential: Credential = Credential.ACCESS_TOKEN,
    requestTimeout: Duration? = null,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = http.request(config.urlFor(path)) {
    this.method = method
    attributes.put(ApiServiceAttribute, service)
    attributes.put(OperationIdAttribute, operationId)
    attributes.put(CredentialAttribute, credential)
    if (attested) attributes.put(AttestedAttribute, true)

    // Minted here, before the request is ever sent, so every retry and the post-refresh replay
    // reuse this exact value. A key minted per attempt would turn a retry into a second command.
    if (keyed) header(MageRideHeaders.IDEMPOTENCY_KEY, idempotencyKey ?: idempotencyKeys.next())

    header(MageRideHeaders.APP_VERSION, config.appVersion)
    header(MageRideHeaders.PLATFORM, config.platform.wire)
    config.userAgent?.let { header(HttpHeaders.UserAgent, it) }
    // Δ MCS-14 — the SOCKET deadline moves with the request deadline, and raising one without the
    // other is the same failure with a different name. `socketTimeout` is the idle gap between two
    // reads: while the server is running OCR, or waiting on a payment provider, the connection is
    // silent by definition, so a 90-second call whose socket may go quiet for only 15 would still
    // be killed at 15 — just by a different plugin. Both are the whole-call deadline here.
    requestTimeout?.let { deadline ->
        timeout {
            requestTimeoutMillis = deadline.inWholeMilliseconds
            socketTimeoutMillis = deadline.inWholeMilliseconds
        }
    }
    configure()
}

internal suspend fun ApiTransport.apiGet(
    service: ApiService,
    operationId: String,
    path: String,
    credential: Credential = Credential.ACCESS_TOKEN,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = apiRequest(service, operationId, HttpMethod.Get, path, credential = credential, configure = configure)

/** A POST mutation. Always carries an `Idempotency-Key` — the contract requires one (D3' §0). */
internal suspend fun ApiTransport.apiPost(
    service: ApiService,
    operationId: String,
    path: String,
    idempotencyKey: String? = null,
    attested: Boolean = false,
    credential: Credential = Credential.ACCESS_TOKEN,
    requestTimeout: Duration? = null,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = apiRequest(
    service = service,
    operationId = operationId,
    method = HttpMethod.Post,
    path = path,
    idempotencyKey = idempotencyKey,
    keyed = true,
    attested = attested,
    credential = credential,
    requestTimeout = requestTimeout,
    configure = configure,
)

/**
 * A POST that carries no `Idempotency-Key` because the contract marks it `x-idempotency-exempt`.
 *
 * Exactly the six HMAC-signed payment-provider callbacks, which dedupe on
 * `provider_transaction_id` (R-19) because an external gateway cannot send our header. They are
 * inbound to the platform, not outbound from an app — see each client's KDoc.
 */
internal suspend fun ApiTransport.apiPostExempt(
    service: ApiService,
    operationId: String,
    path: String,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = apiRequest(
    service = service,
    operationId = operationId,
    method = HttpMethod.Post,
    path = path,
    credential = Credential.PROVIDED,
    configure = configure,
)

/**
 * `PUT`.
 *
 * [requestTimeout] overrides the API budget for the call, exactly as [apiPost]'s does. Δ MCS-14
 * added it because the two multipart onboarding arms are `PUT`s: they carry the image bytes and
 * their response waits on the OCR extraction, so 15 seconds is shorter than the server's own
 * deadline for the same work. Until then only `POST` could say so, which is why the profile
 * upload could not.
 */
internal suspend fun ApiTransport.apiPut(
    service: ApiService,
    operationId: String,
    path: String,
    requestTimeout: Duration? = null,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = apiRequest(
    service = service,
    operationId = operationId,
    method = HttpMethod.Put,
    path = path,
    requestTimeout = requestTimeout,
    configure = configure,
)

internal suspend fun ApiTransport.apiDelete(
    service: ApiService,
    operationId: String,
    path: String,
    configure: HttpRequestBuilder.() -> Unit = {},
): HttpResponse = apiRequest(service, operationId, HttpMethod.Delete, path, configure = configure)
