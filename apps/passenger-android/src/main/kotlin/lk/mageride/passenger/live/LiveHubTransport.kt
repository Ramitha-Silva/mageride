package lk.mageride.passenger.live

import com.google.gson.JsonElement
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import io.reactivex.rxjava3.core.Single
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import lk.mageride.shared.data.api.TokenProvider
import lk.mageride.shared.realtime.LiveHub

/**
 * The socket, as everything above it needs to see it.
 *
 * **This interface is the reason the live plane is testable at all.** Every rule C076 owns — the
 * 19-cell join, the 30 s hysteresis, the reconnect budget, the recovery order, dropping a revoked
 * Mode B vehicle — lives in [PassengerLiveMap] above this line and is asserted on the JVM against
 * a fake, with no server and no network. Below it there is exactly one implementation and it is
 * almost entirely calls into a Microsoft library. Same split C067 made between `PositionPipeline`
 * and the MQTT client.
 *
 * @see SignalRLiveHubTransport
 */
internal interface LiveHubTransport {

    /**
     * Opens the connection and subscribes to [events].
     *
     * Suspends until the hub handshake completes; throws if it does not. The caller owns the
     * retry — see [PassengerLiveMap].
     *
     * @param events The server → client method names to listen for. `LiveHub.Event`'s constants;
     *   SignalR resolves them by string, so a typo is a handler that is never invoked.
     * @param onEvent `(eventName, payloadJson)`. The payload is handed up as raw JSON text rather
     *   than as a bound object — see [SignalRLiveHubTransport] for why.
     * @param onClosed The connection dropped. Carries the cause when the client knows one.
     */
    suspend fun connect(events: Set<String>, onEvent: (String, String) -> Unit, onClosed: (Throwable?) -> Unit)

    /** A client → server invocation. `LiveHub.Method`'s constants. */
    suspend fun send(method: String, vararg args: Any)

    /** Closes the connection. Never throws — a socket that will not close cleanly is still closed. */
    suspend fun close()
}

/**
 * D6' §5 — *"Client = SignalR Java client (Android)"*.
 *
 * **Payloads come back as raw JSON and are decoded by `:shared`'s `MageRideJson`, not by Gson.**
 * The hub protocol is Gson and Gson binds an enum by its Kotlin `name()`; C012's enums carry
 * `@SerialName` wire spellings that differ — `three_wheeler`, not `THREE_WHEELER`; `stale`, not
 * `STALE` — so a Gson-bound `VehicleFrame` would throw on the first three-wheeler in Colombo. The
 * fix is not a second set of Gson DTOs with `@SerializedName` on all eighteen `RideState` values:
 * `:shared` ships `realtime/LiveHubEvents` precisely so *"a client can share one set of models
 * between the socket and the API"* (`signalr-hub.md` §3), and two spellings of a ride state is the
 * failure that contract exists to prevent. So the binding here is `JsonElement` — Gson's identity
 * binding, which cannot be wrong — and [PassengerLiveMap] decodes its text.
 *
 * **The client has no automatic reconnect.** `HttpHubConnectionBuilder` offers
 * `withServerTimeout`, `withKeepAliveInterval` and `onClosed`, and nothing else: unlike the
 * JavaScript and .NET clients there is no `withAutomaticReconnect()`. D6' §5.4 and
 * `signalr-hub.md` §1.2 still require jittered exponential reconnect (R-09), so [PassengerLiveMap]
 * runs that loop with `:shared`'s `ReconnectBackoff` — the same curve the MQTT client uses.
 *
 * @param baseUrl The gateway origin. `LiveHub.PATH` is appended; the hub is not a separate host.
 * @param tokens The session's access token. **The ordinary 30-minute API token (D-29)** — never
 *   an MQTT session JWT, which this app does not have and would not be accepted here (E-02).
 */
internal class SignalRLiveHubTransport(private val baseUrl: String, private val tokens: TokenProvider) :
    LiveHubTransport {

    @Volatile
    private var connection: HubConnection? = null

    override suspend fun connect(
        events: Set<String>,
        onEvent: (String, String) -> Unit,
        onClosed: (Throwable?) -> Unit,
    ) = withContext(Dispatchers.IO) {
        close()

        val hub = HubConnectionBuilder
            .create(baseUrl.trimEnd('/') + LiveHub.PATH)
            // SignalR's own convention, and unavoidable: a browser WebSocket cannot set an
            // `Authorization` header, so the token travels in `access_token`. `Single.fromCallable`
            // is re-evaluated on every subscription, which is what makes a reconnect after a
            // proactive refresh carry the NEW token rather than the one that expired.
            .withAccessTokenProvider(Single.fromCallable { runBlocking { tokens.accessToken().orEmpty() } })
            .withKeepAliveInterval(LiveHub.KEEP_ALIVE.inWholeMilliseconds)
            .withServerTimeout(LiveHub.SERVER_TIMEOUT.inWholeMilliseconds)
            .build()

        events.forEach { name ->
            hub.on(name, { payload: JsonElement -> onEvent(name, payload.toString()) }, JsonElement::class.java)
        }
        hub.onClosed { error -> onClosed(error) }

        // The Java client is RxJava-based and has no coroutine surface. `blockingAwait` on the IO
        // dispatcher is the honest bridge; the alternative is a callback-to-continuation wrapper
        // around a Completable that is already being awaited on a background thread.
        hub.start().blockingAwait()
        connection = hub
    }

    override suspend fun send(method: String, vararg args: Any) {
        withContext(Dispatchers.IO) {
            // Dropped rather than thrown when the socket is not up: every send this app makes is a
            // group membership, and group membership is re-established wholesale by the recovery
            // sequence on the next connect. Throwing here would make each caller handle a case
            // that already has one answer.
            connection?.send(method, *args)
        }
    }

    override suspend fun close() {
        val open = connection ?: return
        connection = null
        withContext(Dispatchers.IO) {
            runCatching { open.stop().blockingAwait() }
            runCatching { open.close() }
        }
    }
}
