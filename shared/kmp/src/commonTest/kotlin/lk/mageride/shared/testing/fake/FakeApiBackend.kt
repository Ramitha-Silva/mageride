package lk.mageride.shared.testing.fake

import io.ktor.client.engine.mock.MockEngine
import io.ktor.client.engine.mock.respond
import io.ktor.client.engine.mock.toByteArray
import io.ktor.http.Headers
import io.ktor.http.HttpStatusCode
import io.ktor.http.Parameters
import io.ktor.http.headersOf
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import lk.mageride.shared.data.api.MageRideHeaders
import lk.mageride.shared.data.api.OperationIdAttribute
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fixture.DtoFixtures
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.concurrent.Volatile

/**
 * A MockEngine that answers **every one of the 176 operations** without being told how.
 *
 * The usual shape of an API fake is a stub per screen, written when the screen is written and
 * stale by the time the next one lands. This one starts from [ApiOperations] — the contract's own
 * route table, joined to the typed client's own return types — and synthesises a fully-populated
 * body for each. So a view model can be exercised the moment it exists, and only the calls a test
 * is actually *about* need programming:
 *
 * ```kotlin
 * val backend = FakeApiBackend()
 * backend.fails("acceptRideOffer", HttpStatusCode.Gone, "offer-expired")
 * val api = backend.mageRideApi()
 *
 * assertFailsWith<MageRideError.Gone> { api.ride.acceptRideOffer(rideId, driverId, request) }
 * assertEquals(1, backend.callsTo("acceptRideOffer").size)
 * ```
 *
 * **Routing is by `operationId`, not by path.** C013's `ApiTransport` puts the contract's own
 * operation id in every request's attributes, so a stub cannot be attached to the wrong route by a
 * mistyped URL, and a call to an operation this kit does not know about fails loudly instead of
 * quietly returning a `404` a client would mistake for "not found".
 *
 * **Serving is guarded; programming it is not.** MockEngine serves concurrent requests on several
 * threads, so the recorder and the stub queues are behind a mutex — a test that fires five calls at
 * once (the D-29 single-rotation rule is exactly such a test) would otherwise lose an append and
 * fail intermittently. Call [always]/[next]/[fails] from the test coroutine, before the calls they
 * are meant to answer.
 */
public class FakeApiBackend {

    /**
     * What the fake has served, as an **immutable list replaced on each call** rather than a
     * mutable one appended to.
     *
     * The writer runs inside [lock], but [calls], [callsTo], [called] and [describeCalls] are not
     * `suspend` and so cannot take a coroutine `Mutex` — they read this field directly. Against a
     * `mutableListOf` that is a `ConcurrentModificationException` in whichever assertion happens to
     * iterate while a background coroutine is still recording, which is a flake with somebody
     * else's test name on it. Copy-on-write makes a reader's snapshot immutable by construction;
     * `@Volatile` is what makes it the *latest* snapshot.
     */
    @Volatile
    private var recorded: List<FakeCall> = emptyList()
    private val standing = mutableMapOf<String, FakeReply>()
    private val queued = mutableMapOf<String, ArrayDeque<FakeReply>>()
    private val synthesised = mutableMapOf<String, FakeReply>()
    private val lock = Mutex()

    /** Every call the fake has served, oldest first. */
    public val calls: List<FakeCall> get() = recorded

    /** The engine to hand to `mageRideHttpClient` — or use [mageRideApi], which does it for you. */
    public val engine: MockEngine = MockEngine { request ->
        val operationId = request.attributes.getOrNull(OperationIdAttribute)
            ?: error(
                "a request reached the fake without an operation id. Every MageRide call goes " +
                    "through ApiTransport, which sets one; a bare HttpClient call does not.",
            )
        val reply = lock.withLock {
            recorded += FakeCall(
                operationId = operationId,
                method = request.method.value,
                path = request.url.encodedPath,
                query = request.url.parameters,
                headers = request.headers,
                body = request.body.toByteArray().decodeToString(),
            )
            replyFor(operationId)
        }
        respond(reply.body ?: "", reply.status, reply.headers)
    }

    // ---- reading what happened -------------------------------------------------------------

    /** Every call to [operationId], oldest first. */
    public fun callsTo(operationId: String): List<FakeCall> = recorded.filter { it.operationId == operationId }

    /** The most recent call to [operationId]. Fails the test if there was none. */
    public fun lastCall(operationId: String): FakeCall =
        callsTo(operationId).lastOrNull() ?: error("nothing called $operationId; calls so far: ${describeCalls()}")

    /** Whether [operationId] was called at all. */
    public fun called(operationId: String): Boolean = recorded.any { it.operationId == operationId }

    private fun describeCalls(): String = if (recorded.isEmpty()) "(none)" else recorded.joinToString { it.operationId }

    // ---- programming it ----------------------------------------------------------------------

    /** Answers every call to [operationId] with [reply], replacing the synthesised default. */
    public fun always(operationId: String, reply: FakeReply): FakeApiBackend {
        known(operationId)
        standing[operationId] = reply
        return this
    }

    /**
     * Queues [replies] for the next calls to [operationId], in order.
     *
     * How a retry, a refresh-and-replay or a poll that eventually changes state is expressed:
     * `next("getRideState", offered, accepted)` answers `Offered` once and `Accepted` after.
     * Once the queue drains, the standing reply (or the synthesised default) takes over again.
     */
    public fun next(operationId: String, vararg replies: FakeReply): FakeApiBackend {
        known(operationId)
        queued.getOrPut(operationId) { ArrayDeque() }.addAll(replies)
        return this
    }

    /** Answers [operationId] with [value], encoded as the platform encodes it. */
    public inline fun <reified T> returns(operationId: String, value: T): FakeApiBackend =
        always(operationId, FakeReply.value(value, statusOf(operationId)))

    /** Answers [operationId] with an RFC 7807 problem. */
    public fun fails(
        operationId: String,
        status: HttpStatusCode,
        code: String,
        extensions: JsonObject = JsonObject(emptyMap()),
        headers: Headers = Headers.Empty,
    ): FakeApiBackend = always(operationId, FakeReply.problem(status, code, extensions, headers))

    /** The success status the contract declares for [operationId] — what [returns] replies with. */
    public fun statusOf(operationId: String): HttpStatusCode = HttpStatusCode.fromValue(known(operationId).status)

    /** Forgets every recorded call and every stub. The synthesised defaults are unaffected. */
    public fun reset() {
        recorded = emptyList()
        standing.clear()
        queued.clear()
    }

    // ---- defaults ------------------------------------------------------------------------------

    private fun replyFor(operationId: String): FakeReply = queued[operationId]?.removeFirstOrNull()
        ?: standing[operationId]
        ?: synthesised.getOrPut(operationId) { synthesise(known(operationId)) }

    private fun known(operationId: String): FakeOperation = ApiOperations.BY_ID[operationId]
        ?: error(
            "$operationId is not in the operation table. Either the contract gained an operation " +
                "and ApiOperations was not regenerated (ApiOperationTableTest would say so), or " +
                "the id is misspelled.",
        )

    // One return per shape a success response can take, and the three are genuinely different
    // answers: bytes, nothing, or a document synthesised from a schema. Collapsing them into one
    // expression would hide which of the three a row resolved to.
    @Suppress("ReturnCount")
    private fun synthesise(operation: FakeOperation): FakeReply {
        val status = HttpStatusCode.fromValue(operation.status)
        // Bytes, not a document: the client returns a `ByteArray` and there is no schema to build
        // a fixture from. Checked before `response`, which is null for both this and a real 204.
        if (operation.binary) return FakeReply.binary(status = status)
        val serializer = operation.response
            ?: return FakeReply.empty(status, headers = bodilessHeaders(operation))
        return FakeReply.json(singlePage(DtoFixtures.jsonOf(serializer.descriptor)), status)
    }

    /**
     * `downloadGtfsFeed` is a `302` whose payload is its `Location` header — the client reads the
     * header and never follows the redirect (`followRedirects = false`). Every other bodiless
     * operation is a plain `204`.
     */
    private fun bodilessHeaders(operation: FakeOperation): Headers =
        if (operation.status == FOUND) headersOf("Location", Fixtures.ASSET_URL) else Headers.Empty

    /**
     * Closes a synthesised page.
     *
     * [DtoFixtures] populates every field, which for a `Page` means `hasMore = true` and a cursor —
     * a page that says there is another one. Left alone, `CursorPagedSource` would follow it
     * forever. The default here is therefore one complete page; a test that is *about* paging
     * stubs the two calls it wants with [next].
     */
    @Suppress("ReturnCount")
    private fun singlePage(document: JsonElement): JsonElement {
        val obj = document as? JsonObject ?: return document
        if (PAGE_KEYS.any { it !in obj }) return document
        return JsonObject(obj + mapOf("cursor" to JsonNull, "hasMore" to JsonPrimitive(false)))
    }

    /**
     * The **JSON** body this fake would serve for [operationId], without making the call.
     *
     * `null` for an operation that answers with nothing, and for one that answers with bytes — a
     * binary reply has no `JsonObject` form and parsing one would throw rather than answer.
     */
    public fun defaultBodyOf(operationId: String): JsonObject? {
        val operation = known(operationId)
        if (operation.binary) return null
        return synthesise(operation).body?.let { MageRideJson.parseToJsonElement(it).jsonObject }
    }

    private companion object {
        const val FOUND = 302
        val PAGE_KEYS = listOf("items", "cursor", "hasMore")
    }
}

/**
 * One call as the fake saw it.
 *
 * @property operationId The contract operation, straight from the request's attributes.
 * @property method HTTP verb.
 * @property path The concrete path, parameters substituted.
 * @property query The query string, parsed.
 * @property headers Request headers.
 * @property body The request body as text; empty for a GET or a bodiless POST.
 */
public class FakeCall internal constructor(
    public val operationId: String,
    public val method: String,
    public val path: String,
    public val query: Parameters,
    public val headers: Headers,
    public val body: String,
) {
    /** The `Idempotency-Key` this call carried, or `null` (R-14/R-18). */
    public val idempotencyKey: String? get() = headers[MageRideHeaders.IDEMPOTENCY_KEY]

    /** The bearer token this call carried, or `null`. */
    public val authorization: String? get() = headers["Authorization"]

    /** The body parsed as a JSON object. Fails if the call carried none. */
    public val json: JsonObject get() = MageRideJson.parseToJsonElement(body).jsonObject

    override fun toString(): String = "$method $path ($operationId)"
}
