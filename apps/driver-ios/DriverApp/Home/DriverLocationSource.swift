import CoreLocation
import Foundation
import MageRideShared

/// One GNSS fix, as a **screen** needs it.
///
/// A local value rather than `:shared`'s `PositionSample`: that type carries the durable `seq`, the
/// phase and the publish bookkeeping R-17 needs, none of which a marker on a map has any business
/// holding. The same struct is `apps/driver-android/.../location/Fix.kt`'s.
///
/// The sentinels are dropped on the way in, not carried: CoreLocation reports "unknown" as a
/// **negative** value, and a `-1` accuracy that reached a screen would be read as a metre.
struct Fix: Equatable {

    let lat: Double
    let lng: Double
    let sampleTs: Date
    var accuracyM: Double?
    var speedMps: Double?
    var headingDeg: Int?

    /// The driver's position as a `GeoPoint` — what `GoOnlineRequest` and the Directional card take.
    var point: GeoPoint { GeoPoint(lat: lat, lng: lng) }
}

/// The handset's own GNSS, for a **screen**.
///
/// **Not the publisher.** ``PositionService`` owns the fixes that reach the broker — its cadence is
/// D5' §5.2's, it holds the background-location entitlement and it outlives every view. This is the
/// other half: SCR-DI-010's map draws the driver's own marker (AL-31 — the home map shows *only*
/// their own vehicle, and `LiveMapScope.DriverHomeMap` joins no geocell group, so the marker can come
/// from nowhere else), `POST /v1/standby/online` needs a position in its body, and SCR-DI-011
/// accumulates the journey's distance from it.
///
/// A second `CLLocationManager` is not a second GPS: CoreLocation multiplexes and hands the same
/// fixes to both. What it is, is a subscription that lives and dies with a **screen** rather than
/// with a shift — which is exactly why the publisher cannot supply it.
///
/// A protocol for the reason every seam in this target is one: a model test has no location manager,
/// and a fake is what makes a fix arrive on demand.
@MainActor
protocol DriverLocationSource: AnyObject {

    /// The last fix, or `nil` before the first one.
    var fix: Fix? { get }

    /// Starts delivering fixes to [onFix], beginning with the last known one where there is one.
    ///
    /// Idempotent: a second call replaces the handler rather than registering twice.
    func start(onFix: @escaping (Fix) -> Void)

    /// Stops. A screen that is not visible must not hold a GNSS subscription open.
    func stop()
}

/// ``DriverLocationSource`` over `CLLocationManager`.
///
/// **The accuracy is `kCLLocationAccuracyNearestTenMeters`, not navigation.** ``PositionService`` is
/// the one that owes the platform a rate and runs at `BestForNavigation`; this subscription draws a
/// marker and accumulates a kilometre count, and asking a five-year-old handset for two
/// best-for-navigation streams at once is battery spent on a dot that moves the same either way. It
/// is the same argument the Android twin makes by taking D5' §5.1's *slow* end for its screen source.
///
/// **It asks for nothing.** SCR-DI-007 (C086) is the permission screen and ``PositionService`` is
/// what escalates to Always; a screen source that raised a second prompt would ask a driver for a
/// permission they granted on the screen before. With no authorisation at all it simply never emits,
/// and SCR-DI-010's *"Waiting for GPS"* refusal is what the driver sees.
@MainActor
final class CoreLocationDriverLocationSource: NSObject, DriverLocationSource {

    private(set) var fix: Fix?

    private let manager: CLLocationManager
    private var onFix: ((Fix) -> Void)?

    override init() {
        manager = CLLocationManager()
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyNearestTenMeters
        manager.distanceFilter = Self.distanceFilterM
        manager.activityType = .automotiveNavigation
    }

    func start(onFix: @escaping (Fix) -> Void) {
        self.onFix = onFix

        // The last known fix first, so a dashboard opened in a car park draws the driver where they
        // are instead of over Colombo Fort while the first tick is waited for.
        if let last = fix ?? manager.location.map(Fix.init(location:)) {
            fix = last
            onFix(last)
        }

        let status = manager.authorizationStatus
        guard status == .authorizedAlways || status == .authorizedWhenInUse else { return }
        manager.startUpdatingLocation()
    }

    func stop() {
        onFix = nil
        manager.stopUpdatingLocation()
    }

    /// Ten metres. Fine enough that the marker tracks the vehicle, coarse enough that a parked
    /// handset stops waking the radio.
    private static let distanceFilterM: CLLocationDistance = 10
}

extension CoreLocationDriverLocationSource: CLLocationManagerDelegate {

    nonisolated func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        guard let last = locations.last else { return }
        Task { @MainActor [weak self] in
            guard let self else { return }
            let fix = Fix(location: last)
            self.fix = fix
            self.onFix?(fix)
        }
    }

    nonisolated func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        // A transient failure is normal in a tunnel. The last fix stands and the next one resumes the
        // stream; blanking the marker would move the driver to Colombo Fort every time they went
        // under a flyover.
    }

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor [weak self] in
            guard let self, let onFix = self.onFix else { return }
            self.start(onFix: onFix)
        }
    }
}

private extension Fix {

    init(location: CLLocation) {
        self.init(
            lat: location.coordinate.latitude,
            lng: location.coordinate.longitude,
            sampleTs: location.timestamp,
            accuracyM: location.horizontalAccuracy >= 0 ? location.horizontalAccuracy : nil,
            speedMps: location.speed >= 0 ? location.speed : nil,
            headingDeg: location.course >= 0 ? Int(location.course) : nil
        )
    }
}

/// Turning the position publisher on and off, without a `CLLocationManager` in a model.
///
/// Going online is two things that must happen together: `POST /v1/standby/online` puts the driver in
/// the candidate pool, and ``PositionService`` starts publishing on `veh/{vehicleId}/pos/live`. A
/// driver in the pool who is not publishing is a driver dispatch offers rides to and cannot find — so
/// the dashboard owns both, and this is the seam that lets it without holding the service.
///
/// The vehicle id is the argument because **the MQTT username is the vehicle id** (D6' §3): the
/// broker learns which of a driver's vehicles is live at CONNECT, which is also why
/// ``ActiveVehicleStore`` is local.
@MainActor
protocol PositionPublisher: AnyObject {

    /// Publishes for [vehicleId]. Idempotent — a second call for the same vehicle is a no-op.
    ///
    /// [mode] and [vehicleType] are the pipeline's, not the socket's: D5' §5.2's cadence table is
    /// keyed on them, so a bus on a route and a tuk on standby publish at the rates the spec fixes
    /// rather than at one rate for both.
    func start(vehicleId: String, mode: ServiceMode?, vehicleType: VehicleType?) async

    /// Stops publishing and announces `offline` on `veh/{vehicleId}/status`.
    func stop()
}

/// ``PositionPublisher`` over ``PositionService``.
@MainActor
final class ServicePositionPublisher: PositionPublisher {

    private let positions: PositionService

    init(positions: PositionService) {
        self.positions = positions
    }

    func start(vehicleId: String, mode: ServiceMode?, vehicleType: VehicleType?) async {
        await positions.start(vehicleId: vehicleId, mode: mode, vehicleType: vehicleType)
    }

    func stop() {
        positions.stop()
    }
}
