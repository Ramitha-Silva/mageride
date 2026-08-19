import Foundation
import MageRideShared
import SignalRClient

/// One argument of a `/hubs/live` invocation.
///
/// `signalr-hub.md` §2 declares exactly two shapes and there is no third: `JoinGeocells(cells:
/// string[])` and `LeaveGeocells(cells: string[])` take an array, `SubscribeRide(rideId)` and
/// `SubscribeLocRequest(requestId)` take a scalar. Modelling that as an enum rather than as
/// `[Encodable]` buys two things — the SignalR call site switches once instead of every caller
/// choosing an encoding, and a fake can be `Equatable`, which is what lets every rule in this
/// package be asserted with no server and no network.
enum HubArgument: Equatable {

    /// A single string — a ride id or a location-request id.
    case text(String)

    /// An array of strings — a set of `H3Cell.token`s.
    case texts([String])
}

/// The socket, as everything above it needs to see it.
///
/// **This protocol is the reason the live plane is testable at all.** Every rule this component owns
/// — the 19-cell join, the 30 s hysteresis, the reconnect budget, the recovery *order*, dropping a
/// revoked Mode B vehicle — lives in ``PassengerLiveMap`` above this line and is asserted against a
/// fake. Below it there is exactly one implementation and it is almost entirely calls into a
/// third-party library. Same split C076 made on Android and C085 made between `PositionPipeline` and
/// the MQTT client.
protocol LiveHubTransport: AnyObject {

    /// Opens the connection and subscribes to [events].
    ///
    /// Suspends until the hub handshake completes; throws if it does not. **The caller owns the
    /// retry** — see ``PassengerLiveMap``.
    ///
    /// - Parameters:
    ///   - events: The server → client method names to listen for. `LiveHub.Event`'s constants;
    ///     SignalR resolves them by string, so a typo is a handler that is never invoked.
    ///   - onEvent: `(eventName, payloadJson)`. The payload is handed up as raw JSON text rather
    ///     than as a bound object — see ``SignalRLiveHubTransport``.
    ///   - onClosed: The connection dropped, with the cause when the client knows one.
    func connect(
        events: [String],
        onEvent: @escaping (String, String) -> Void,
        onClosed: @escaping (Error?) -> Void
    ) async throws

    /// A client → server invocation. `LiveHub.Method`'s constants.
    ///
    /// Never throws: every send this app makes is a group membership, and group membership is
    /// re-established wholesale by the recovery sequence on the next connect. A caller that had to
    /// handle a failure would be handling a case that already has one answer.
    func send(_ method: String, _ argument: HubArgument) async

    /// Closes the connection. Never throws — a socket that will not close cleanly is still closed.
    func close() async
}

/// D6' §5 — *"Client = SignalR Java client (Android) / **SignalR Swift client** (iOS)"*.
///
/// **This file has not been compiled by anybody.** It is written against `SignalR-Client-Swift`'s
/// documented API on a host that cannot build for iOS (root `CLAUDE.md`), exactly as C085's
/// MapLibre, CocoaMQTT and Firebase call sites were. It is the first file to read at the first
/// `xcodebuild` on macOS, and the C094 handoff says so.
///
/// **Payloads come back as raw JSON and are decoded by `:shared`, not by this client's binder.**
/// The client binds an argument through `Decodable`, and a `Decodable` mirror of `VehicleFrame` in
/// this target is precisely what `signalr-hub.md` §3 forbids — *"a client can share one set of
/// models between the socket and the API"* — and would mean spelling all eighteen `RideState`
/// values twice. That is the same wall C076 hit from the other side (Gson binds an enum by its
/// Kotlin `name()`, so a bound `VehicleFrame` throws on the first `three_wheeler` in Colombo) and
/// the remedy is the same shape: bind the **identity**, hand the text up, and let
/// `IosLiveHubPayloadsKt` decode it with the platform's own `Json`. Gson has `JsonElement` for that;
/// Swift has nothing equivalent in the standard library, so ``AnyJSON`` is it. See ``LiveHubInbox``.
///
/// **The reconnect is ours, and this client's own policy is deliberately not used.** C076's warning
/// was to *audit* rather than to assume, and the audit's answer is that R-09's curve is a platform
/// rule and not a client setting: it is the same jittered exponential 1–60 s ±25 % the driver app's
/// MQTT client uses — because a regional outage ends for both planes at the same instant — and it
/// lives in `:shared`'s `ReconnectBackoff`, where both platforms read it. A library policy here
/// would be a third schedule nobody can see, and it would restart the connection *without* running
/// D6' §5.4's recovery, which is the part that actually matters. ``PassengerLiveMap`` runs the loop;
/// its first retry lands inside 1.25 s, which is what makes SCR-PI-032's *"auto-clears on reconnect
/// < 5 s"* an achievable promise rather than a hope.
///
/// **The credential is the ordinary 30-minute API access token (D-29), never an MQTT session JWT**
/// (E-02) — which this app does not have and which would not be accepted here. It travels in the
/// `access_token` query parameter because a browser `WebSocket` cannot set an `Authorization`
/// header and the Passenger Web subview shares this contract's shape. The provider closure the
/// client calls is **synchronous** and `TokenProvider.accessToken()` is `suspend`, so the value is
/// read immediately before each handshake — which is what makes a reconnect after a proactive
/// refresh carry the new token rather than the one that expired.
final class SignalRLiveHubTransport: NSObject, LiveHubTransport {

    private let baseUrl: String
    private let tokens: TokenProvider

    /// The contract, as Swift can reach it — the endpoint, the two timings and every method and
    /// event name. This file spells none of them; see `IosLiveHub`.
    private let hubContract = IosLiveHub()

    private var connection: HubConnection?
    private var delegate: HubDelegate?

    init(baseUrl: String, tokens: TokenProvider) {
        self.baseUrl = baseUrl
        self.tokens = tokens
    }

    func connect(
        events: [String],
        onEvent: @escaping (String, String) -> Void,
        onClosed: @escaping (Error?) -> Void
    ) async throws {
        await close()

        guard let url = URL(string: baseUrl.trimmedTrailingSlash + hubContract.path) else {
            throw LiveHubError.badBaseUrl(baseUrl)
        }

        // Read before the handshake, not inside the closure: see this type's documentation.
        // `try` because `TokenProvider.accessToken()` is a Kotlin `suspend` function, and a suspend
        // function exports with an `NSError**` out-parameter — so it reaches Swift as `async throws`
        // whether or not it can actually fail. The error is propagated rather than swallowed: this
        // function is already `throws`, and `?? ""` is there for a **nil** token (a signed-out
        // passenger has none), which is a different thing from the read having failed.
        let accessToken = try await tokens.accessToken() ?? ""

        let handshake = HubDelegate(onClosed: onClosed)
        let hub = HubConnectionBuilder(url: url)
            .withHttpConnectionOptions { options in
                options.accessTokenProvider = { accessToken }
            }
            // `signalr-hub.md` §1's keepalive, through the one door a `Duration` may cross this
            // bridge — see `IosLiveHub`. Read as a raw integer it would be about 3.9 × 10^13
            // seconds and the client would never ping. `keepAliveInterval` is a `Double` of
            // seconds, which is exactly what `IosLiveHub` hands over.
            //
            // **`serverTimeoutSeconds` is deliberately NOT set, because this client has nowhere to
            // put it.** SignalR-Client-Swift 1.2.1 models no server timeout at all: its
            // `HubConnectionOptions` carries `keepAliveInterval` and `callbackQueue` and nothing
            // else, and the only timeout anywhere in the package is
            // `HttpConnectionOptions.requestTimeout`, which bounds a single HTTP request rather
            // than the silence after which a live connection is presumed dead. The builder methods
            // this line used to call — `withKeepAlive` and `withServerTimeout` — do not exist on it
            // in any version this package has shipped.
            //
            // The contract still carries the value and `IosLiveHub` still publishes it, so nothing
            // upstream needs changing when a client that can honour it arrives. What is lost until
            // then is only the *early* detection of a hung connection: the delegate's `onClosed`
            // still fires on a transport-level drop, which is the common case, and C093's
            // reconnect path is driven from there.
            .withHubConnectionOptions { options in
                options.keepAliveInterval = hubContract.keepAliveSeconds
            }
            .withHubConnectionDelegate(delegate: handshake)
            .build()

        for name in events {
            // The identity binding. `AnyJSON` accepts whatever the server sent and re-serialises it
            // byte-equivalently; there is no shape here that can be wrong.
            hub.on(method: name) { (payload: AnyJSON) in
                guard let text = payload.jsonText else { return }
                onEvent(name, text)
            }
        }

        self.delegate = handshake
        self.connection = hub

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            handshake.onHandshake = { error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            }
            hub.start()
        }
    }

    func send(_ method: String, _ argument: HubArgument) async {
        guard let connection else { return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            // The completion carries an error this app has nothing to do with — see the protocol.
            switch argument {
            case .text(let value):
                connection.send(method: method, value) { _ in continuation.resume() }
            case .texts(let values):
                connection.send(method: method, values) { _ in continuation.resume() }
            }
        }
    }

    func close() async {
        guard let open = connection else { return }
        connection = nil
        // `onHandshake` and `onClosed` are dropped with the delegate, which is what stops the
        // connection being replaced from signalling a drop into the supervisor that is about to
        // await one. ``PassengerLiveMap`` drains the signal as well — belt and braces, because the
        // stop is asynchronous and the callback may already be in flight.
        delegate?.onHandshake = nil
        delegate?.onClosed = { _ in }
        delegate = nil
        open.stop()
    }
}

/// The client's delegate, as two closures.
///
/// `HubConnectionDelegate` is a protocol with three callbacks and no context, so the alternative is
/// making the transport itself the delegate and holding the continuation on it — which breaks the
/// moment a connection is replaced, because the old socket's `connectionDidClose` would resume the
/// new socket's continuation. One delegate per connection attempt, dropped with it.
private final class HubDelegate: HubConnectionDelegate {

    /// Resumed exactly once, by whichever of open/failed fires. `nil` after the handshake.
    var onHandshake: ((Error?) -> Void)?

    var onClosed: (Error?) -> Void

    init(onClosed: @escaping (Error?) -> Void) {
        self.onClosed = onClosed
    }

    func connectionDidOpen(hubConnection: HubConnection) {
        let handshake = onHandshake
        onHandshake = nil
        handshake?(nil)
    }

    func connectionDidFailToOpen(error: Error) {
        let handshake = onHandshake
        onHandshake = nil
        handshake?(error)
    }

    func connectionDidClose(error: Error?) {
        // A failure *during* the handshake arrives here on some transports rather than through
        // `connectionDidFailToOpen`. Resuming the continuation from both places is why `onHandshake`
        // is cleared before it is called — a continuation resumed twice is a crash, not a warning.
        if let handshake = onHandshake {
            onHandshake = nil
            handshake(error ?? LiveHubError.closedDuringHandshake)
            return
        }
        onClosed(error)
    }
}

enum LiveHubError: Error, Equatable {
    /// The gateway origin in `Info.plist` is not a URL. A build error dressed as a runtime one.
    case badBaseUrl(String)

    /// The socket closed before the hub handshake completed.
    case closedDuringHandshake
}

private extension String {
    var trimmedTrailingSlash: String {
        hasSuffix("/") ? String(dropLast()) : self
    }
}
