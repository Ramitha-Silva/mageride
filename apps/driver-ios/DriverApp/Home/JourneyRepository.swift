import Foundation
import MageRideShared

/// What this handset remembers about a Mode A/B journey between launches.
///
/// Two values, both about *this device* rather than about the platform, which is why neither has an
/// endpoint:
///
/// * **``startedSessionId``** — the session the driver started **from the dashboard**. AL-32 needs the
///   difference between a journey the driver began and one a paired GPS tracker began on ignition,
///   and `trip-state.yaml`'s `Session` carries an `endReason` but **no start reason**: nothing on the
///   wire says who opened it. Recording our own is the only thing that can tell them apart across a
///   process death, and it is what raises the *"started automatically by GPS device"* banner for a
///   session this app did not open.
/// * **``routeId``** — the bus route last run. `transit.yaml` has no *"routes I drive"* read (see the
///   C070 handoff), so re-typing the number every morning is the alternative.
protocol JourneyPreferences: AnyObject {

    /// The session id this dashboard started, or `nil` when it started none.
    var startedSessionId: String? { get set }

    /// The GTFS `route_id` last run on this handset.
    var routeId: String? { get set }
}

/// ``JourneyPreferences`` over the app's own `UserDefaults` suite. Nothing here is a secret.
final class UserDefaultsJourneyPreferences: JourneyPreferences {

    private let store: UserDefaults

    init(store: UserDefaults = .standard) {
        self.store = store
    }

    var startedSessionId: String? {
        get { store.string(forKey: Keys.startedSession) }
        set { store.set(newValue, forKey: Keys.startedSession) }
    }

    var routeId: String? {
        get { store.string(forKey: Keys.route) }
        set { store.set(newValue, forKey: Keys.route) }
    }

    /// The same two values `driver_journey`'s `SharedPreferences` file holds on Android, prefixed so
    /// they cannot collide with anything else in the standard suite.
    private enum Keys {
        static let startedSession = "mageride.journey.started_session_id"
        static let route = "mageride.journey.route_id"
    }
}

/// SCR-DI-011's journey, as the dashboard sees it.
///
/// - Parameters:
///   - session: The live or just-ended tracking window; `nil` for a parked vehicle.
///   - route: The Mode A bus route, resolved for the *"138 — Pettah ↔ Maharagama"* card.
///   - startedByDevice: Whether a paired GPS tracker opened this session on ignition (AL-32) rather
///     than the driver opening it here.
struct JourneyStanding {

    var session: Session?
    var route: TransitRoute?
    var startedByDevice = false

    /// Whether the vehicle is live on its route — the state that offers **End Journey**.
    var isRunning: Bool { session?.state == SessionState.active }

    /// Whether US-5.10's five-minute grace is open.
    ///
    /// Only an **automatic** end qualifies (the 30-minute idle timer, the 100 m destination geofence,
    /// the broker's last will), which is exactly what `restartableUntil` being present means —
    /// trip-state-svc sets it on `AUTO_ENDED` and on nothing else.
    var isRestartable: Bool {
        session?.state == SessionState.autoEnded && session?.restartableUntil != nil
    }

    /// When the running session began, as a `Date`.
    ///
    /// `kotlin.time.Instant` reaches Swift as an opaque object, so the conversion happens here rather
    /// than at each of the three call sites that would otherwise spell `toEpochMilliseconds()`.
    var startedAt: Date? {
        session.map { Date(timeIntervalSince1970: TimeInterval($0.startedAt.toEpochMilliseconds()) / 1000) }
    }

    /// *"138 — Pettah ↔ Maharagama"*, or `nil` when no route has been chosen.
    var routeLabel: String? {
        guard let route else { return nil }
        guard let long = route.routeLongName, !long.isEmpty else { return route.routeShortName }
        return route.routeShortName + " — " + long
    }
}

/// trip-state-svc — Mode A/B tracking sessions, and **nothing else** (R-01).
///
/// **This is not ride-svc.** A session is a *vehicle running its route*: it has no fare, no offer and
/// no passenger of its own, and `ck_sessions_mode` refuses `mode: C` at the database. SCR-DI-011
/// talks here; SCR-DI-010, SCR-DI-014 and SCR-DI-015 talk to ride-svc and dispatch-svc. The boundary
/// is never crossed in either direction.
///
/// **AL-32 — the dashboard outranks the device, in both directions.** A tracker that sees ignition ON
/// opens a session through `POST /v1/internal/sessions/ignition`, and this dashboard shows it as
/// already started; the driver may still End it here. A driver who starts a journey by hand gets a
/// session the tracker's ignition-OFF will not close, because trip-state-svc closes only *"one the
/// device started — never one the driver started from the dashboard"*.
protocol JourneyRepository: AnyObject {

    /// `GET /v1/sessions/{vehicleId}/active` plus the route card, for one vehicle.
    func standing(vehicleId: String) async throws -> JourneyStanding

    /// `POST /v1/sessions/start` — the green **Start Journey**.
    func start(vehicleId: String, mode: ServiceMode, routeId: String?, autoEndAtDestination: Bool) async throws -> Session

    /// `POST /v1/sessions/{sessionId}/end` — the red **End Journey**, device session or not.
    func end(sessionId: String) async throws -> Session

    /// `POST /v1/sessions/{sessionId}/restart` — US-5.10's five-minute grace.
    func restart(sessionId: String) async throws -> Session

    /// `GET /v1/transit/routes/{routeId}` — the route card's long name.
    func routeOf(routeId: String?) async -> TransitRoute?
}

/// ``JourneyRepository`` over trip-state-svc and transit-svc.
final class ApiJourneyRepository: JourneyRepository {

    private let tripState: TripStateApi
    private let transit: TransitApi
    private let preferences: JourneyPreferences

    init(tripState: TripStateApi, transit: TransitApi, preferences: JourneyPreferences) {
        self.tripState = tripState
        self.transit = transit
        self.preferences = preferences
    }

    func standing(vehicleId: String) async throws -> JourneyStanding {
        let session = try await tripState.getActiveSession(vehicleId: vehicleId)
        return JourneyStanding(
            session: session,
            route: await routeOf(routeId: session?.routeId ?? preferences.routeId),
            // A live session whose id this device never recorded is the tracker's (AL-32).
            startedByDevice: session != nil && session?.sessionId != preferences.startedSessionId
        )
    }

    /// The session id is recorded locally on the way out, because that record is the only thing that
    /// can later say this journey was not the tracker's.
    ///
    /// `409 driver-already-live` when this driver already has a session running (D-03's mutex);
    /// `403 vehicle-not-approved` for a vehicle the Fleet Portal has not approved.
    func start(
        vehicleId: String,
        mode: ServiceMode,
        routeId: String?,
        autoEndAtDestination: Bool
    ) async throws -> Session {
        let session = try await tripState.startSession(
            request: StartSessionRequest(
                vehicleId: vehicleId,
                mode: mode,
                routeId: routeId,
                autoEndAtDestination: KotlinBoolean(value: autoEndAtDestination)
            ),
            idempotencyKey: nil
        )
        preferences.startedSessionId = session.sessionId
        preferences.routeId = routeId
        return session
    }

    func end(sessionId: String) async throws -> Session {
        let session = try await tripState.endSession(sessionId: sessionId, idempotencyKey: nil)
        if preferences.startedSessionId == sessionId { preferences.startedSessionId = nil }
        return session
    }

    /// `410 Gone` once the window has passed, which is a real answer rather than a failure: the
    /// journey has to be started again from scratch.
    func restart(sessionId: String) async throws -> Session {
        let session = try await tripState.restartSession(sessionId: sessionId, idempotencyKey: nil)
        preferences.startedSessionId = session.sessionId
        return session
    }

    /// Best-effort: an unknown route number, or a feed that has not been activated, leaves the card
    /// showing the number the driver typed. Losing the long name must not stop a bus going live.
    func routeOf(routeId: String?) async -> TransitRoute? {
        guard let id = routeId?.trimmingCharacters(in: .whitespacesAndNewlines), !id.isEmpty else { return nil }
        guard let route = try? await transit.getTransitRoute(routeId: id, lat: nil, lng: nil) else {
            return TransitRoute(
                routeId: id,
                routeShortName: id,
                routeLongName: nil,
                agencyName: nil,
                shape: nil,
                stops: [],
                nearestStops: nil
            )
        }
        return route
    }
}
