import Combine
import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 2's seams, faked, plus the fixtures its two suites are written against.
///
/// Same rule as ``OnboardingTestKit``: **every one of these stands in for a Swift protocol, never for
/// a Kotlin class.** That is why ``PassengerPlaces``, ``RecentPlaces`` and ``PassengerLocationSource``
/// exist at all — `QueryApi` and `IamApi` are Kotlin interfaces with `suspend` methods and Swift can
/// implement neither, and `PassengerDatabase` opens a real protected file.
///
/// The live plane is deliberately **not** faked: ``LiveMapModelTests`` runs the real
/// ``PassengerLiveMap`` over ``FakeLiveHubTransport``, because every rule it asserts lives on the
/// boundary between the two and a stubbed plane would let the suite assert a shape production does
/// not have. C078 makes the same call on the Android side.
///
/// **Nothing here constructs a boxed Kotlin primitive.** `heading`, `speed`, `etaSeconds` and
/// `isHome` are all optional `Int`/`Double`/`Boolean` on the wire and cross as `KotlinInt?` /
/// `KotlinDouble?` / `KotlinBoolean?`; `nil` needs no initialiser, and the two apps currently
/// disagree about how one is spelled (see the C096 handoff).

// MARK: - Places

final class FakePassengerPlaces: PassengerPlaces, @unchecked Sendable {

    /// Programmable answers. `nil` succeeds; a value is thrown.
    var searchFailure: Error?
    var savedFailure: Error?

    var searchResults: [GeocodedPlace] = []
    var saved: [SavedAddress] = []

    /// Every call, in order — several rules here are about *what was sent* rather than about state.
    private(set) var searches: [(text: String, around: GeoPoint?, limit: Int)] = []
    private(set) var savedReads = 0

    func search(_ text: String, around: GeoPoint?, limit: Int) async throws -> [GeocodedPlace] {
        searches.append((text, around, limit))
        if let searchFailure { throw searchFailure }
        return searchResults
    }

    func savedAddresses() async throws -> [SavedAddress] {
        savedReads += 1
        if let savedFailure { throw savedFailure }
        return saved
    }
}

// MARK: - Recents

/// §2.2's table, in memory — the real one is a protected SQLite file behind an actor.
final class FakeRecentPlaces: RecentPlaces, @unchecked Sendable {

    private(set) var rows: [GeocodedPlace]

    init(_ rows: [GeocodedPlace] = []) {
        self.rows = rows
    }

    func recent(limit: Int) async -> [GeocodedPlace] {
        Array(rows.prefix(limit))
    }

    func remember(_ place: GeocodedPlace) async {
        rows.insert(place, at: 0)
    }
}

// MARK: - Location

/// Fixes a test hands over by name, rather than a satellite.
final class FakePassengerLocationSource: PassengerLocationSource, @unchecked Sendable {

    private let subject = PassthroughSubject<PassengerFix, Never>()

    var fixes: AnyPublisher<PassengerFix, Never> { subject.eraseToAnyPublisher() }

    func emit(_ fix: PassengerFix) {
        subject.send(fix)
    }
}

// MARK: -

/// The canonical values cluster 2's tests are written against.
enum HomeFixtures {

    static let colombo = GeoPoint(lat: 6.9344, lng: 79.8428)

    static let busId = "01JVEH0000000000000000001"
    static let vanId = "01JVEH0000000000000000002"
    static let tukId = "01JVEH0000000000000000003"

    /// A marker id that is not on the map — a tap on one that has since left.
    static let departedId = "01JVEH0000000000000000009"

    /// One of each mode, so a filter assertion always has something on both sides of it.
    static let threeVehicles = """
        [{"vehicleId":"\(busId)","lat":6.9344,"lng":79.8428,"heading":90,"type":"bus","mode":"A"},
         {"vehicleId":"\(vanId)","lat":6.9350,"lng":79.8430,"type":"van","mode":"B"},
         {"vehicleId":"\(tukId)","lat":6.9360,"lng":79.8440,"type":"three_wheeler","mode":"C"}]
        """

    static func removed(_ vehicleId: String, reason: String) -> String {
        #"{"vehicleId":"\#(vehicleId)","reason":"\#(reason)"}"#
    }

    /// A `VehicleFrame` as ``MapFilterTests`` needs one — the two nullable numbers are `nil`, which
    /// is also what the socket sends for a vehicle that reported neither.
    static func frame(_ type: VehicleType?, _ mode: ServiceMode?, id: String = "V1") -> VehicleFrame {
        VehicleFrame(
            vehicleId: id,
            lat: colombo.lat,
            lng: colombo.lng,
            heading: nil,
            speed: nil,
            type: type,
            mode: mode
        )
    }

    /// What `GET /v1/nearby` knows about the bus that the socket frame does not.
    ///
    /// `etaSeconds` is `nil` on purpose: the *formatting* of an ETA is pinned by ``MapFormatTests``
    /// against a plain `Int`, and a non-nil one here would need a boxed `KotlinInt` this kit does not
    /// build. What this fixture pins is the pair of fields the socket genuinely cannot carry.
    static func busDetail(driver: String? = "K. Perera", plate: String? = "NB-4521") -> NearbyVehicle {
        NearbyVehicle(
            vehicleId: busId,
            type: VehicleType.bus,
            mode: ServiceMode.a,
            lat: colombo.lat,
            lng: colombo.lng,
            heading: nil,
            speed: nil,
            driverName: driver,
            etaSeconds: nil,
            registrationNumber: plate
        )
    }

    static func address(id: String, label: String, lat: Double, lng: Double) -> SavedAddress {
        SavedAddress(
            addressId: id,
            label: label,
            line1: "1 Union Place",
            line2: nil,
            line3: "Colombo",
            lat: lat,
            lng: lng,
            isHome: nil,
            isWork: nil
        )
    }

    static let home = address(id: "01JADDR000000000000000001", label: "Home", lat: 6.9271, lng: 79.8612)
    static let work = address(id: "01JADDR000000000000000002", label: "Work", lat: 6.9200, lng: 79.8600)

    /// Saved on SCR-PI-026 while the map sat behind it — see the re-read test.
    static let gym = address(id: "01JADDR000000000000000003", label: "Gym", lat: 6.9000, lng: 79.8550)

    static func place(
        _ name: String,
        lat: Double,
        lng: Double,
        source: GeocodedPlaceSource? = nil
    ) -> GeocodedPlace {
        GeocodedPlace(lat: lat, lng: lng, displayName: name, line1: "High Level Road", city: nil, source: source)
    }

    static let nugegoda = place("Nugegoda Junction", lat: 6.8649, lng: 79.8997, source: GeocodedPlaceSource.recent)
    static let maharagama = place("Maharagama Town", lat: 6.8480, lng: 79.9265, source: GeocodedPlaceSource.recent)
    static let fort = place("Fort Railway Station", lat: 6.9337, lng: 79.8500, source: GeocodedPlaceSource.nominatim)
    static let pettah = place("Pettah Bus Stand", lat: 6.9360, lng: 79.8560, source: GeocodedPlaceSource.nominatim)
}

/// A failure with nothing behind it — what an unreachable service looks like to a model.
enum HomeFakeError: Error {
    case unreachable
}
