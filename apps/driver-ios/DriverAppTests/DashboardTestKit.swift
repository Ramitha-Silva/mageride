import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures cluster 3's models are driven by.
///
/// Same rule as ``OnboardingTestKit`` and ``VehicleTestKit``: every seam here is a **Swift protocol**,
/// because the Kotlin types behind them are classes and interfaces Swift cannot stand in for. The DTOs
/// underneath are the real shared ones — a fixture is built with the same initialiser the gateway's
/// response deserialises into, so a contract change fails these tests rather than a driver's phone.

// MARK: - The ride and the offer every C088 test is about

let testDriverId = "01JDRIVER00000000000000001"
let testRideId = "01JRIDE0000000000000000001"
let testOfferId = "01JOFFER000000000000000001"
let testSessionId = "01JSESSION0000000000000001"

/// Colombo Fort, and a point 1.2 km from it — far enough that a haversine over the pair is a real
/// distance rather than floating-point noise.
let testHere = GeoPoint(lat: 6.9344, lng: 79.8428)
let testThere = GeoPoint(lat: 6.9452, lng: 79.8428)

func testFix(_ point: GeoPoint = testHere, headingDeg: Int? = nil) -> Fix {
    Fix(lat: point.lat, lng: point.lng, sampleTs: Date(), accuracyM: 8, speedMps: 0, headingDeg: headingDeg)
}

/// A ride as `GET /v1/rides/{rideId}` returns it.
/// - Parameter riderId: Who is travelling (Δ C092). `nil` is **P-01's proxy booking for an
///   unregistered rider**, which is the one completed ride SCR-DI-030 can never rate.
func rideDetail(
    state: RideState = RideState.accepted,
    version: Int32 = 3,
    kind: RideKind = RideKind.passenger,
    paymentMethod: RidePaymentMethod = RidePaymentMethod.cash,
    counterpartyPhone: String? = "+94771234567",
    packageSize: PackageSize? = nil,
    fareMinor: Int64 = 48_000,
    riderId: String? = nil
) -> RideDetail {
    RideDetail(
        rideId: testRideId,
        kind: kind,
        state: state,
        version: version,
        bookerId: nil,
        riderId: riderId,
        riderName: "Nimal",
        pickup: Place(lat: testHere.lat, lng: testHere.lng, address: "Galle Face"),
        dropoff: Place(lat: testThere.lat, lng: testThere.lng, address: "Nugegoda"),
        vehicleType: RideVehicleType.sedan,
        paymentMethod: paymentMethod,
        scheduledAt: nil,
        offerExpiresAt: nil,
        driver: nil,
        counterpartyPhone: counterpartyPhone,
        fare: FareEstimate(amountMinor: fareMinor, currency: Currency.lkr, surchargeMinor: nil),
        packageSize: packageSize,
        packageDescription: nil,
        packageStatus: nil,
        recipientName: nil,
        senderPhone: nil,
        recipientPhone: nil,
        createdAt: IosInstantKt.timestampFromEpochMillis(millis: 0)
    )
}

/// An offer as `offer.created` delivers it, with [secondsLeft] of its fifteen still to run.
func rideOffer(secondsLeft: TimeInterval = 15, isProxy: Bool = false, directionalMatched: Bool = false) -> RideOffer {
    RideOffer(
        offerId: testOfferId,
        rideId: testRideId,
        driverId: testDriverId,
        expiresAt: IosInstantKt.timestampFromEpochMillis(
            millis: Int64((Date().timeIntervalSince1970 + secondsLeft) * 1000)
        ),
        kind: RideKind.passenger,
        isProxy: isProxy,
        riderName: nil,
        riderPhoneMasked: nil,
        packageSize: nil,
        packageDescription: nil,
        directionalMatched: directionalMatched,
        fareEstimateMinor: 48_000,
        currency: Currency.lkr,
        paymentMethod: RidePaymentMethod.cash,
        pickup: nil,
        dropoff: nil,
        version: nil
    )
}

/// A Mode A/B tracking session.
func tripSession(
    state: SessionState = SessionState.active,
    startedSecondsAgo: TimeInterval = 0,
    isRestartable: Bool = false
) -> Session {
    Session(
        sessionId: testSessionId,
        vehicleId: testVehicleId,
        driverId: testDriverId,
        mode: ServiceMode.a,
        routeId: nil,
        state: state,
        autoEndAtDestination: KotlinBoolean(value: true),
        startedAt: IosInstantKt.timestampFromEpochMillis(
            millis: Int64((Date().timeIntervalSince1970 - startedSecondsAgo) * 1000)
        ),
        endedAt: nil,
        endReason: nil,
        restartableUntil: isRestartable
            ? IosInstantKt.timestampFromEpochMillis(millis: Int64(Date().timeIntervalSince1970 * 1000) + 300_000)
            : nil
    )
}

func directionalFilter(
    active: Bool = false,
    usesRemaining: Int32 = 2,
    secondsLeft: TimeInterval = 0,
    label: String? = nil
) -> DirectionalFilterState {
    DirectionalFilterState(
        active: active,
        destination: active ? testThere : nil,
        label: label,
        expiresAt: active
            ? IosInstantKt.timestampFromEpochMillis(
                millis: Int64((Date().timeIntervalSince1970 + secondsLeft) * 1000)
            )
            : nil,
        timeRemainingSec: Int32(secondsLeft),
        usesRemaining: usesRemaining
    )
}

func todaysFee(
    status: DailyFeeDayStatus = DailyFeeDayStatus.unpaid,
    dailyRateMinor: Int64 = 10_000,
    tripsToday: Int32 = 0,
    firstTripFree: Bool = true
) -> TodaysDailyFee {
    TodaysDailyFee(
        vehicleType: VehicleType.threeWheeler,
        vehicleId: testVehicleId,
        dailyRateMinor: dailyRateMinor,
        status: status,
        deductedMinor: nil,
        tripsToday: tripsToday,
        firstTripFree: firstTripFree,
        feeDate: IosBusinessDateKt.colomboBusinessDateNow(),
        feeDateTzAt: nil
    )
}

func driverWallet(availableMinor: Int64 = 100_000) -> Wallet {
    Wallet(
        userId: testDriverId,
        balanceMinor: availableMinor,
        availableMinor: availableMinor,
        outstandingDebtMinor: nil,
        currency: Currency.lkr,
        updatedAt: nil
    )
}

func todaysEarnings(trips: Int32 = 4, netMinor: Int64 = 318_000) -> EarningsSummary {
    EarningsSummary(
        period: EarningsPeriod.today,
        rangeFrom: nil,
        rangeTo: nil,
        grossMinor: netMinor,
        dailyFeeMinor: nil,
        penaltyMinor: nil,
        tipMinor: nil,
        netMinor: netMinor,
        currency: Currency.lkr,
        trips: trips
    )
}

// MARK: - Seams

/// ``DriverIdentity`` with no session and no gateway.
final class FakeDriverIdentity: DriverIdentity {

    var driverId: String? = testDriverId
    var live = LiveVehicle()
    var nextFailure: Error?

    private(set) var reads = 0

    func liveVehicle() async throws -> LiveVehicle {
        reads += 1
        if let failure = nextFailure {
            nextFailure = nil
            throw failure
        }
        return live
    }
}

/// ``StandbyRepository`` with no gateway.
///
/// Every call is recorded rather than only counted: half of what C088's rules say is *which* request
/// went out and in what order — going online publishes only after `POST /v1/standby/online` is
/// accepted, going offline stops publishing first — and a counter cannot tell those apart.
final class FakeStandbyRepository: StandbyRepository {

    var standing = DashboardStanding()
    var filter = directionalFilter()
    var created = DirectionalFilterCreated(
        filterId: "01JFILTER00000000000000001",
        expiresAt: IosInstantKt.timestampFromEpochMillis(
            millis: Int64(Date().timeIntervalSince1970 * 1000) + 7_200_000
        ),
        usesRemaining: 1,
        maxDurationSec: 7_200
    )
    var cleared = DirectionalFilterCleared(active: false, usesRemaining: 1)
    var shortcuts: [SavedAddress] = []
    var places: [GeocodedPlace] = []
    var nextFailure: Error?

    private(set) var goneOnline: [(vehicleId: String, position: GeoPoint)] = []
    private(set) var goneOfflineCount = 0
    private(set) var setDirectionals: [(destination: GeoPoint, label: String?)] = []
    private(set) var clearedCount = 0
    private(set) var searches: [String] = []

    func standing(driverId: String) async -> DashboardStanding { standing }

    func goOnline(vehicleId: String, position: GeoPoint) async throws -> PresenceState {
        goneOnline.append((vehicleId, position))
        try throwIfProgrammed()
        return PresenceState.available
    }

    func goOffline() async throws -> PresenceState {
        goneOfflineCount += 1
        try throwIfProgrammed()
        return PresenceState.offline
    }

    func directional() async throws -> DirectionalFilterState {
        try throwIfProgrammed()
        return filter
    }

    func setDirectional(destination: GeoPoint, label: String?) async throws -> DirectionalFilterCreated {
        setDirectionals.append((destination, label))
        try throwIfProgrammed()
        return created
    }

    func clearDirectional() async throws -> DirectionalFilterCleared {
        clearedCount += 1
        try throwIfProgrammed()
        return cleared
    }

    func savedShortcuts() async -> [SavedAddress] { shortcuts }

    func searchPlaces(query: String, near: Fix?) async throws -> [GeocodedPlace] {
        searches.append(query)
        return places
    }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

/// ``JourneyRepository`` with no gateway.
final class FakeJourneyRepository: JourneyRepository {

    var standing = JourneyStanding()
    var nextSession = tripSession()
    var route: TransitRoute?
    var nextFailure: Error?

    private(set) var started: [(vehicleId: String, routeId: String?, autoEnd: Bool)] = []
    private(set) var ended: [String] = []
    private(set) var restarted: [String] = []

    func standing(vehicleId: String) async throws -> JourneyStanding {
        try throwIfProgrammed()
        return standing
    }

    func start(
        vehicleId: String,
        mode: ServiceMode,
        routeId: String?,
        autoEndAtDestination: Bool
    ) async throws -> Session {
        started.append((vehicleId, routeId, autoEndAtDestination))
        try throwIfProgrammed()
        return nextSession
    }

    func end(sessionId: String) async throws -> Session {
        ended.append(sessionId)
        try throwIfProgrammed()
        return nextSession
    }

    func restart(sessionId: String) async throws -> Session {
        restarted.append(sessionId)
        try throwIfProgrammed()
        return nextSession
    }

    func routeOf(routeId: String?) async -> TransitRoute? { route }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

/// ``ActiveRideRepository`` with no gateway.
final class FakeActiveRideRepository: ActiveRideRepository {

    var activeRide: RideDetail?
    var detailToReturn = rideDetail()
    var snapshotToReturn: RideStateSnapshot?
    var nextMove: RideStateSnapshot?
    var qrState = PaymentState.driverConfirmedQR
    var nextFailure: Error?

    private(set) var arrived: [Int32] = []
    private(set) var started: [(kind: RideKind, version: Int32, otp: String)] = []
    private(set) var completed: [Int32] = []
    private(set) var confirmedQr = 0
    private(set) var disputedQr = 0

    func active(driverId: String) async throws -> RideDetail? {
        try throwIfProgrammed()
        return activeRide
    }

    func detail(rideId: String) async throws -> RideDetail {
        try throwIfProgrammed()
        return detailToReturn
    }

    func snapshot(rideId: String) async throws -> RideStateSnapshot {
        guard let snapshotToReturn else { throw CancellationError() }
        return snapshotToReturn
    }

    func arrive(rideId: String, version: Int32) async throws -> RideStateSnapshot {
        arrived.append(version)
        try throwIfProgrammed()
        return nextMove ?? snapshot(RideState.driverArrived, version + 1)
    }

    func start(rideId: String, kind: RideKind, version: Int32, otp: String) async throws -> RideStateSnapshot {
        started.append((kind, version, otp))
        try throwIfProgrammed()
        return nextMove ?? snapshot(RideState.inProgress, version + 1)
    }

    func complete(rideId: String, version: Int32) async throws -> RideStateSnapshot {
        completed.append(version)
        try throwIfProgrammed()
        return nextMove ?? snapshot(RideState.paymentPending, version + 1)
    }

    func confirmQrPayment(rideId: String) async throws -> PaymentState {
        confirmedQr += 1
        try throwIfProgrammed()
        return qrState
    }

    func disputeQrPayment(rideId: String, note: String?) async throws {
        disputedQr += 1
        try throwIfProgrammed()
    }

    private func snapshot(_ state: RideState, _ version: Int32) -> RideStateSnapshot {
        RideStateSnapshot(state: state, version: version, offerExpiresAt: nil)
    }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

/// ``RideContact`` with no dialler and no voip-svc.
@MainActor
final class FakeRideContact: RideContact {

    /// What ``dial(_:)`` answers — `false` is CallKit's "a call is already up".
    var dialSucceeds = true

    private(set) var calls: [(kind: RideKind, type: CallType)] = []

    /// Calls made through the role-named overload (AL-33, Δ C089). Kept **apart** from ``calls`` on
    /// purpose: a delivery sheet must never reach the kind-based form, because a package's kind cannot
    /// say whether the sender or the recipient was the one rung.
    private(set) var roleCalls: [(calleeRole: CalleeRole, type: CallType)] = []
    private(set) var dialled: [String] = []

    /// The outcomes SCR-DI-031 reported (Δ C093), oldest first.
    private(set) var outcomes: [(callId: String, outcome: CallOutcome)] = []

    /// The alarms SCR-DI-032 raised (Δ C093), oldest first.
    private(set) var alarms: [(rideId: String, at: GeoPoint)] = []

    /// What ``startCall(rideId:calleeRole:type:)`` answers. `nil` is voip-svc refusing the call —
    /// which is `VoipFailure.signalling` on SCR-DI-031 and a silent no-op everywhere else.
    var nextCall: StartCallResponse?

    /// What ``triggerSos(rideId:at:)`` answers, or the failure it throws first.
    var nextDispatch = SosDispatched(sosId: "01JQSOS0000000000000000001", dispatchedAt: nil, smsStatus: .dispatched)
    var nextSosFailure: Error?

    @discardableResult
    func startCall(rideId: String, kind: RideKind, type: CallType) async -> StartCallResponse? {
        calls.append((kind, type))
        return nextCall
    }

    @discardableResult
    func startCall(rideId: String, calleeRole: CalleeRole, type: CallType) async -> StartCallResponse? {
        roleCalls.append((calleeRole, type))
        return nextCall
    }

    func reportCallOutcome(callId: String, outcome: CallOutcome) async {
        outcomes.append((callId, outcome))
    }

    func triggerSos(rideId: String, at: GeoPoint) async throws -> SosDispatched {
        alarms.append((rideId, at))
        if let failure = nextSosFailure {
            nextSosFailure = nil
            throw failure
        }
        return nextDispatch
    }

    @discardableResult
    func dial(_ phone: String) -> Bool {
        dialled.append(phone)
        return dialSucceeds
    }
}

/// ``DriverLocationSource`` with no CoreLocation. ``emit(_:)`` is what makes a fix arrive.
@MainActor
final class FakeDriverLocationSource: DriverLocationSource {

    private(set) var fix: Fix?
    private(set) var startCount = 0
    private(set) var stopCount = 0

    private var onFix: ((Fix) -> Void)?

    func start(onFix: @escaping (Fix) -> Void) {
        startCount += 1
        self.onFix = onFix
        if let fix { onFix(fix) }
    }

    func stop() {
        stopCount += 1
        onFix = nil
    }

    /// Delivers one fix, exactly as the delegate would.
    func emit(_ fix: Fix) {
        self.fix = fix
        onFix?(fix)
    }
}

/// ``PositionPublisher`` with no MQTT socket. The **order** of these calls is the rule under test.
@MainActor
final class FakePositionPublisher: PositionPublisher {

    private(set) var events: [String] = []

    func start(vehicleId: String, mode: ServiceMode?, vehicleType: VehicleType?) async {
        events.append("start:" + vehicleId)
    }

    func stop() {
        events.append("stop")
    }
}

/// ``OfferSlot`` with no `OfferSession` behind it.
@MainActor
final class FakeOfferSlot: OfferSlot {

    var liveOfferId: String?
    var acceptOutcome: OfferOutcome = OfferOutcomeTaken.shared
    var declineOutcome: OfferOutcome = OfferOutcomeDeclined.shared

    private(set) var acceptCount = 0
    private(set) var declineCount = 0
    private(set) var expireCount = 0
    private(set) var versions: [Int32] = []

    private var onChange: ((OfferSlotState) -> Void)?

    func observe(_ onChange: @escaping (OfferSlotState) -> Void) {
        self.onChange = onChange
    }

    func stopObserving() {
        onChange = nil
    }

    /// Pushes a slot state, exactly as `OfferSession.state` would.
    func emit(_ state: OfferSlotState) {
        if case .live(let offer) = state { liveOfferId = offer.offerId } else { liveOfferId = nil }
        onChange?(state)
    }

    func accept() async -> OfferOutcome? {
        acceptCount += 1
        return acceptOutcome
    }

    func decline() async -> OfferOutcome? {
        declineCount += 1
        return declineOutcome
    }

    func expire() {
        expireCount += 1
        liveOfferId = nil
    }

    func onVersionKnown(_ version: Int32) {
        versions.append(version)
    }
}

/// ``JourneyPreferences`` in memory. `UserDefaults` would leak between tests through the standard
/// suite, and AL-32's "who started this session" rule is a function of exactly these two values.
final class FakeJourneyPreferences: JourneyPreferences {
    var startedSessionId: String?
    var routeId: String?
}
