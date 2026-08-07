import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// The socket, faked.
///
/// **This is what makes the live plane assertable at all.** Every rule C094 owns — the 19-cell join,
/// the 30 s hysteresis, the reconnect budget, the recovery *order*, dropping a revoked Mode B
/// vehicle — lives above ``LiveHubTransport`` and is checked here with no server and no network.
/// C076 makes the same split on the Android side; the difference is that this one records the
/// invocations as an `Equatable` value, which the Java client's vararg `Object...` could not.
actor FakeLiveHubTransport: LiveHubTransport {

    /// The shared, ordered record of what the client did. **D6' §5.4's recovery is an *order***
    /// — rejoin the groups, then snapshot — so the two fakes append to one log rather than each
    /// counting its own calls, and "the joins came first" is an assertion rather than a comment.
    let log: CallLog

    init(log: CallLog = CallLog()) {
        self.log = log
    }

    /// Every `send`, in order. The recovery *order* is the contract (D6' §5.4), so the sequence
    /// matters as much as the contents.
    private(set) var sent: [(method: String, argument: HubArgument)] = []

    /// How many times a connection has been opened. A reconnect is `2`.
    private(set) var connects = 0

    private(set) var closes = 0

    /// Set to make the next `connect` throw — a handshake that fails, which is what drives the
    /// backoff.
    var failNextConnects = 0

    private var onEvent: ((String, String) -> Void)?
    private var onClosed: ((Error?) -> Void)?
    private(set) var subscribedEvents: [String] = []

    func connect(
        events: [String],
        onEvent: @escaping (String, String) -> Void,
        onClosed: @escaping (Error?) -> Void
    ) async throws {
        connects += 1
        if failNextConnects > 0 {
            failNextConnects -= 1
            throw FakeTransportError.refused
        }
        subscribedEvents = events
        self.onEvent = onEvent
        self.onClosed = onClosed
    }

    func send(_ method: String, _ argument: HubArgument) async {
        sent.append((method, argument))
        log.record(method)
    }

    func close() async {
        closes += 1
        onEvent = nil
        onClosed = nil
    }

    // MARK: - Driving the fake

    /// Delivers one server → client event.
    func deliver(event name: String, payload: String) {
        onEvent?(name, payload)
    }

    /// Drops the connection, as the client's `connectionDidClose` would.
    func drop() {
        onClosed?(FakeTransportError.dropped)
    }

    func clearSent() {
        sent = []
    }

    /// The methods invoked, in order — what most assertions actually want.
    var methods: [String] { sent.map(\.method) }

    /// The cell tokens of the first invocation of `method`, or `nil`.
    func cells(of method: String) -> [String]? {
        for entry in sent where entry.method == method {
            if case .texts(let values) = entry.argument { return values }
        }
        return nil
    }
}

/// One ordered record of everything the client did, across both fakes.
///
/// A lock rather than an actor because both writers are already inside one — the transport is an
/// actor and the snapshot fake is called from one — and an `await` inside them to record a line
/// would be a suspension point in the middle of the sequence under test.
final class CallLog: @unchecked Sendable {

    /// What ``FakeNearbySnapshots`` appends. Not a hub method, so it cannot collide with one.
    static let nearbySnapshot = "GET /v1/nearby"

    private let lock = NSLock()
    private var entries: [String] = []

    func record(_ entry: String) {
        lock.lock()
        entries.append(entry)
        lock.unlock()
    }

    var all: [String] {
        lock.lock()
        defer { lock.unlock() }
        return entries
    }

    func clear() {
        lock.lock()
        entries = []
        lock.unlock()
    }
}

enum FakeTransportError: Error {
    case refused
    case dropped
}

/// `GET /v1/nearby`, faked.
///
/// One method, because ``NearbySnapshots`` is one method — see its documentation for why the live
/// plane does not take `QueryApi` itself.
final class FakeNearbySnapshots: NearbySnapshots, @unchecked Sendable {

    /// The same log the transport writes to — see ``FakeLiveHubTransport/log``.
    let log: CallLog

    init(log: CallLog = CallLog()) {
        self.log = log
    }

    /// What the next call answers. `nil` is the failure path, which must cost freshness and not the
    /// map.
    var response: [NearbyVehicle]?

    /// Every call, so the *order* of the recovery can be asserted against the joins.
    private(set) var calls: [(point: GeoPoint, radiusMetres: Int)] = []

    func nearby(around point: GeoPoint, radiusMetres: Int) async -> [NearbyVehicle]? {
        calls.append((point, radiusMetres))
        log.record(CallLog.nearbySnapshot)
        return response
    }
}

/// A backoff that never sleeps.
///
/// `IosReconnectBackoff` is a Kotlin class and cannot be subclassed usefully from Swift, so the
/// schedule is left alone and the *sleep* is what a test avoids — `PassengerLiveMap` multiplies
/// `nextSeconds()` by a whole second, and R-09's first delay is up to 1.25 s. Tests that drive a
/// reconnect therefore assert the transport's own counters rather than waiting for the curve, and
/// the curve itself is `:shared`'s to test.
enum LiveFixtures {

    /// Colombo Fort — the same coordinate `MapCamera.colombo` and `H3ContractTests` use.
    static let colombo = GeoPoint(lat: 6.9344, lng: 79.8428)

    /// About 4 km north-east: far enough to be a different res-7 cell (a hexagon is ~1.2 km across),
    /// which is what makes it a boundary crossing rather than a jitter.
    static let borella = GeoPoint(lat: 6.9700, lng: 79.8800)

    /// A `VehiclePositions` batch as the server sends it — the wire spellings, not the Kotlin enum
    /// names. `three_wheeler` is the value that broke a Gson binding on the Android side.
    static func vehiclePositions(_ ids: [String], type: String = "three_wheeler") -> String {
        let frames = ids.map { id in
            """
            {"vehicleId":"\(id)","lat":\(colombo.lat),"lng":\(colombo.lng),\
            "heading":90,"speed":8.5,"type":"\(type)","mode":"C"}
            """
        }
        return "[\(frames.joined(separator: ","))]"
    }

    static func vehicleRemoved(_ id: String, reason: String = "stale") -> String {
        #"{"vehicleId":"\#(id)","reason":"\#(reason)"}"#
    }

    static func shareRevoked(_ id: String) -> String {
        #"{"vehicleId":"\#(id)","userId":"01JQ9F8Z6N0000000000000001"}"#
    }
}

extension XCTestCase {

    /// Waits for `predicate` to hold, polling the run loop.
    ///
    /// The live plane hops between two actors and the main one, so a fact is true *shortly* after
    /// the call that causes it rather than on the next line. A poll rather than an expectation
    /// because the interesting assertions are about actor state rather than about a callback.
    func eventually(
        timeout: TimeInterval = 2,
        file: StaticString = #filePath,
        line: UInt = #line,
        _ description: String = "condition",
        _ predicate: @escaping () async -> Bool
    ) async {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await predicate() { return }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTFail("timed out waiting for \(description)", file: file, line: line)
    }
}
