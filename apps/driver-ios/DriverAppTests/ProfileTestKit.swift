import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures C092's four screens are driven by.
///
/// Same rule as ``DashboardTestKit`` and ``WalletTestKit``: every seam here is a **Swift protocol**,
/// because the Kotlin types behind them are interfaces Swift cannot stand in for. The DTOs underneath
/// are the real shared ones — a fixture is built with the same initialiser the gateway's response
/// deserialises into, so a contract change fails these tests rather than a driver's phone.
///
/// **Every repository is recorded, not counted.** Half of what C092's rules say is *which* call ran and
/// in what order: pairing stops the publisher, a selector change re-reads **that vehicle's** two lists,
/// an accept re-reads both, and a rating goes to the session route and no other. A counter cannot say
/// any of that.

/// A second driver's passenger, so a grant and a request are about different people.
// 26 characters, which is `_shared.yaml`'s `Ulid` minimum; the old one was 24 and `SharingModel`
// validates it.
let testPassengerId = "01JPASSENGER00000000000001"

let testRequestId = "01JREQUEST00000000000001"

let testTripId = "01JTRIP00000000000000001"

// `timestamp(_:)` is ``JobsTestKit``'s and is used here as it is: one helper, not two spellings of the
// same millisecond conversion.

/// One incoming Mode B access request (`GET /v1/mode-b/{vehicleId}/access-requests`).
func accessRequest(
    requestId: String = testRequestId,
    vehicleId: String = testVehicleId,
    status: AccessRequestStatus = AccessRequestStatus.pending
) -> AccessRequest {
    AccessRequest(
        requestId: requestId,
        vehicleId: vehicleId,
        passengerId: testPassengerId,
        passengerName: "Sunethra",
        passengerMobileMasked: "+94 77 ••• 0345",
        status: status,
        createdAt: timestamp(Date())
    )
}

/// One current grantee (`GET /v1/vehicles/{vehicleId}/subscribers`).
func subscriber(
    userId: String = testPassengerId,
    status: GrantStatus = GrantStatus.active
) -> Subscriber {
    Subscriber(
        userId: userId,
        name: "Ramith de Silva",
        phoneMasked: "+94 77 ••• 4567",
        status: status,
        grantedAt: nil
    )
}

/// The signed-in driver as `GET /v1/users/me` returns them.
func userProfile(
    firstName: String? = "K. Fernando",
    language: Language? = Language.si,
    notifPrefs: [String: Bool]? = nil
) -> UserProfile {
    UserProfile(
        userId: testDriverId,
        phone: "+94771234567",
        email: nil,
        firstName: firstName,
        photoUrl: nil,
        role: Role.driver,
        roles: nil,
        fleetRole: nil,
        language: language,
        operatingCityCode: "colombo",
        defaultPaymentMethod: nil,
        notifPrefs: notifPrefs.map { $0.mapValues { KotlinBoolean(value: $0) } },
        createdAt: nil
    )
}

/// AL-13's stored contact.
func emergencyContact(
    contactId: String = "01JCONTACT00000000000001",
    isPrimary: Bool = true,
    name: String = "Amma",
    phone: String = "+94770001111"
) -> EmergencyContact {
    EmergencyContact(contactId: contactId, isPrimary: isPrimary, name: name, phone: phone)
}

/// One row of `GET /v1/trips/{driverId}`.
func tripSummary(
    tripId: String = testTripId,
    plane: TripPlane = TripPlane.ride,
    fareMinor: Int64? = 48_000,
    startedAt: Date = Date(timeIntervalSince1970: 1_781_000_000)
) -> TripSummary {
    TripSummary(
        tripId: tripId,
        plane: plane,
        mode: plane == TripPlane.ride ? ServiceMode.c : ServiceMode.a,
        pickup: Place(lat: 6.9271, lng: 79.8612, address: "Galle Face"),
        dropoff: Place(lat: 6.8649, lng: 79.8997, address: "Nugegoda"),
        fareMinor: fareMinor.map { KotlinLong(value: $0) },
        currency: Currency.lkr,
        startedAt: timestamp(startedAt),
        endedAt: nil
    )
}

/// The same trip with the two facts a summary does not carry.
func tripDetail(
    tripId: String = testTripId,
    distanceKm: Double? = 8,
    rating: Int32? = nil,
    geometrySource: GeometrySource = GeometrySource.telemetry
) -> TripDetail {
    TripDetail(
        tripId: tripId,
        plane: TripPlane.ride,
        mode: ServiceMode.c,
        pickup: nil,
        dropoff: nil,
        fareMinor: nil,
        currency: nil,
        startedAt: timestamp(Date(timeIntervalSince1970: 1_781_000_000)),
        endedAt: nil,
        polyline: nil,
        distanceKm: distanceKm.map { KotlinDouble(value: $0) },
        durationSec: nil,
        driver: nil,
        rating: rating.map { KotlinInt(value: $0) },
        geometrySource: geometrySource
    )
}

// MARK: - Seams

/// ``TrackerBindingStore`` in memory.
final class FakeTrackerBindingStore: TrackerBindingStore {

    private(set) var bindings: [String: TrackerBinding] = [:]

    func bindingFor(vehicleId: String) -> TrackerBinding? { bindings[vehicleId] }

    func remember(_ binding: TrackerBinding) {
        bindings[binding.vehicleId] = binding
    }
}

/// ``TrackerRepository`` with no gateway.
final class FakeTrackerRepository: TrackerRepository {

    let store = FakeTrackerBindingStore()
    var nextFailure: Error?
    var bindingId = "01JBINDING00000000000001"

    private(set) var pairs: [(vehicleId: String, imei: String)] = []

    func pair(vehicleId: String, imei: String) async throws -> TrackerBinding {
        pairs.append((vehicleId, imei))
        if let failure = nextFailure {
            nextFailure = nil
            throw failure
        }
        let binding = TrackerBinding(vehicleId: vehicleId, imei: imei, bindingId: bindingId)
        store.remember(binding)
        return binding
    }

    func bindingFor(vehicleId: String) -> TrackerBinding? { store.bindingFor(vehicleId: vehicleId) }
}

/// ``PositionPublisher`` with no service behind it, recording the order of the two calls.
@MainActor
final class FakePositionPublisher: PositionPublisher {

    private(set) var started: [String] = []
    private(set) var stopCount = 0

    func start(vehicleId: String, mode: ServiceMode?, vehicleType: VehicleType?) async {
        started.append(vehicleId)
    }

    func stop() {
        stopCount += 1
    }
}

/// ``SharingRepository`` with no gateway.
///
/// The two list reads are recorded **with the vehicle they were made for**, because AL-35's rule is
/// about scope: a queue read for the vehicle that is no longer selected must not be folded in.
final class FakeSharingRepository: SharingRepository {

    var requestsByVehicle: [String: [AccessRequest]] = [:]
    var granteesByVehicle: [String: [Subscriber]] = [:]
    var nextGrantFailure: Error?
    var nextDecisionFailure: Error?
    var nextListFailure: Error?

    private(set) var grants: [(vehicleId: String, userId: String, expiresAt: Timestamp?)] = []
    private(set) var revokes: [(vehicleId: String, userId: String)] = []
    private(set) var requestReads: [String] = []
    private(set) var granteeReads: [String] = []
    private(set) var accepts: [String] = []
    private(set) var rejects: [String] = []

    func grant(vehicleId: String, userId: String, expiresAt: Timestamp?) async throws -> String {
        grants.append((vehicleId, userId, expiresAt))
        try throwIf(&nextGrantFailure)
        return "01JGRANT0000000000000001"
    }

    func grantees(vehicleId: String) async throws -> [Subscriber] {
        granteeReads.append(vehicleId)
        try throwIf(&nextListFailure)
        return granteesByVehicle[vehicleId] ?? []
    }

    func revoke(vehicleId: String, userId: String) async throws {
        revokes.append((vehicleId, userId))
    }

    func requests(vehicleId: String) async throws -> [AccessRequest] {
        requestReads.append(vehicleId)
        try throwIf(&nextListFailure)
        return requestsByVehicle[vehicleId] ?? []
    }

    func accept(requestId: String) async throws {
        accepts.append(requestId)
        try throwIf(&nextDecisionFailure)
    }

    func reject(requestId: String) async throws {
        rejects.append(requestId)
        try throwIf(&nextDecisionFailure)
    }

    private func throwIf(_ failure: inout Error?) throws {
        guard let programmed = failure else { return }
        failure = nil
        throw programmed
    }
}

/// ``ProfileRepository`` with no gateway.
final class FakeProfileRepository: ProfileRepository {

    var storedProfile = userProfile()
    var contacts: [EmergencyContact] = []
    var jobStanding = JobStanding()
    var language: Language? = Language.si

    var nextFailure: Error?

    private(set) var profileReads = 0
    private(set) var savedNames: [String] = []
    private(set) var savedPreferences: [[String: Bool]] = []
    private(set) var savedLanguages: [Language] = []
    private(set) var savedContacts: [(existing: EmergencyContact?, name: String, phone: String)] = []
    private(set) var logOutCount = 0

    func profile() async throws -> UserProfile {
        profileReads += 1
        try throwIfProgrammed()
        return storedProfile
    }

    func emergencyContacts() async throws -> [EmergencyContact] {
        try throwIfProgrammed()
        return contacts
    }

    func standing(driverId: String) async -> JobStanding { jobStanding }

    /// Δ MCS-27 — empty by default, which is a fresh install and the only honest fixture for a
    /// test whose assertions are about what the network reads produce.
    var cached: CachedDriverProfile?

    var photoBytes: Data?

    func cachedProfile(driverId: String) async -> CachedDriverProfile? { cached }

    func cacheIdentity(driverId: String, name: String?, level: Int32?, registration: String?) async {}

    func driverPhoto(driverId: String) async -> Data? { photoBytes }

    func saveName(_ name: String) async throws -> UserProfile {
        savedNames.append(name)
        try throwIfProgrammed()
        storedProfile = userProfile(firstName: name, language: language)
        return storedProfile
    }

    func saveNotificationPreferences(_ preferences: [String: Bool]) async throws -> UserProfile {
        savedPreferences.append(preferences)
        try throwIfProgrammed()
        storedProfile = userProfile(firstName: storedProfile.firstName, language: language, notifPrefs: preferences)
        return storedProfile
    }

    func saveLanguage(_ language: Language) async throws {
        savedLanguages.append(language)
        try throwIfProgrammed()
        self.language = language
        storedProfile = userProfile(firstName: storedProfile.firstName, language: language)
    }

    func storedLanguage() -> Language? { language }

    func saveEmergencyContact(
        existing: EmergencyContact?,
        name: String,
        phone: String
    ) async throws -> EmergencyContact {
        savedContacts.append((existing, name, phone))
        try throwIfProgrammed()
        return emergencyContact(contactId: existing?.contactId ?? "01JCONTACT00000000000002", name: name, phone: phone)
    }

    func logOut() async {
        logOutCount += 1
    }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

/// ``RideHistoryRepository`` with no gateway.
final class FakeRideHistoryRepository: RideHistoryRepository {

    var summaries: [TripSummary] = []
    var details: [String: TripDetail] = [:]
    var ride = rideDetail()
    var nextTripsFailure: Error?
    var nextDetailFailure: Error?
    var nextRideFailure: Error?
    var nextRatingFailure: Error?

    private let recordLock = NSLock()
    private(set) var detailReads: [String] = []
    private(set) var rideReads: [String] = []
    private(set) var ratings: [(subjectId: String, passengerId: String, stars: Int, comment: String?)] = []

    func trips(driverId: String) async throws -> [TripSummary] {
        try throwIf(&nextTripsFailure)
        return summaries
    }

    func detail(driverId: String, tripId: String) async throws -> TripDetail {
        // Locked: ``RideHistoryModel/refresh()`` reads the details in a `withTaskGroup`, so this
        // runs on several tasks at once and a bare `append` loses one of them — the test asserting
        // that EVERY row was read then fails on whichever it lost, differently per build.
        recordLock.lock()
        detailReads.append(tripId)
        recordLock.unlock()
        try throwIf(&nextDetailFailure)
        guard let detail = details[tripId] else { throw CancellationError() }
        return detail
    }

    func rideParties(rideId: String) async throws -> RideDetail {
        rideReads.append(rideId)
        try throwIf(&nextRideFailure)
        return ride
    }

    func ratePassenger(
        subjectId: String,
        passengerId: String,
        stars: Int,
        comment: String?
    ) async throws -> Rating {
        ratings.append((subjectId, passengerId, stars, comment))
        try throwIf(&nextRatingFailure)
        return Rating(
            ratingId: "01JRATING000000000000001",
            stars: Int32(stars),
            text: comment,
            createdAt: timestamp(Date())
        )
    }

    private func throwIf(_ failure: inout Error?) throws {
        guard let programmed = failure else { return }
        failure = nil
        throw programmed
    }
}
