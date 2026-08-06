package lk.mageride.passenger.live

import lk.mageride.shared.realtime.LiveHub

/** One client → server invocation, as the fake recorded it. */
internal data class HubCall(val method: String, val args: List<Any>) {

    /** The cell tokens a `JoinGeocells` / `LeaveGeocells` carried. */
    val cells: List<String>
        get() = (args.firstOrNull() as? Array<*>)?.filterIsInstance<String>().orEmpty()

    /** The id a `SubscribeRide` / `SubscribeLocRequest` carried. */
    val id: String get() = args.firstOrNull() as? String ?: ""
}

/**
 * The socket, faked.
 *
 * **Every rule C076 owns is asserted through this class**, because none of them is about SignalR:
 * the nineteen cells are `GeoCellSubscription`'s arithmetic, the hysteresis is a clock comparison,
 * the reconnect is a backoff loop, and the recovery is an ordered plan. What the real transport
 * does is call into a Microsoft library — see `SignalRLiveHubTransport` — and none of that is
 * testable on a build host with no server.
 *
 * The fake records what was sent, hands events back the way the hub would, and can refuse a
 * connect or drop one on demand.
 */
internal class FakeLiveHubTransport : LiveHubTransport {

    /** Every send, in order. */
    val calls = mutableListOf<HubCall>()

    /** How many times a connection has been opened. */
    var connects: Int = 0
        private set

    /** How many opens should fail before one succeeds. */
    var failNextConnects: Int = 0

    private var onEvent: ((String, String) -> Unit)? = null
    private var onClosed: ((Throwable?) -> Unit)? = null
    private var subscribed: Set<String> = emptySet()

    override suspend fun connect(
        events: Set<String>,
        onEvent: (String, String) -> Unit,
        onClosed: (Throwable?) -> Unit,
    ) {
        connects++
        if (failNextConnects > 0) {
            failNextConnects--
            error("the hub refused the handshake")
        }
        subscribed = events
        this.onEvent = onEvent
        this.onClosed = onClosed
    }

    override suspend fun send(method: String, vararg args: Any) {
        calls += HubCall(method, args.toList())
    }

    override suspend fun close() {
        onEvent = null
    }

    /** Delivers one server → client event, as JSON text — exactly what the real transport hands up. */
    fun emit(event: String, payload: String) {
        check(event in subscribed) { "$event was never subscribed to; the client would not hear it" }
        onEvent?.invoke(event, payload)
    }

    /** Drops the connection. */
    fun drop() {
        onClosed?.invoke(null)
    }

    /** The methods sent so far, deduplicated — for asserting that nothing outside the contract is. */
    fun methodsUsed(): Set<String> = calls.mapTo(LinkedHashSet(), HubCall::method)

    /** Every `JoinGeocells`, in order. */
    fun joins(): List<HubCall> = calls.filter { it.method == LiveHub.Method.JOIN_GEOCELLS }

    /** Every `LeaveGeocells`, in order. */
    fun leaves(): List<HubCall> = calls.filter { it.method == LiveHub.Method.LEAVE_GEOCELLS }

    /** Forgets the recording without touching the connection. */
    fun clearCalls() {
        calls.clear()
    }
}
