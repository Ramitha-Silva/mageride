import Combine
import CoreLocation
import Foundation
import MageRideShared

/// One fix.
///
/// - `accuracyMetres` is MAP-02's circle radius. `0` when the platform did not report one, which
///   draws no halo rather than a halo of unknown size — Core Location's own sentinel is a *negative*
///   accuracy and it is mapped to zero here rather than passed on.
struct PassengerFix: Equatable {
    let lat: Double
    let lng: Double
    var accuracyMetres: Double = 0

    /// The fix as a `GeoPoint` — what the geocell subscription and every booking body take.
    var point: GeoPoint { GeoPoint(lat: lat, lng: lng) }
}

/// Where the passenger is.
///
/// **This app publishes nothing.** The driver's equivalent sits beside a background-location mode
/// that owes the platform a cadence (D5' §5.2) and runs through a whole shift; this one is a plain
/// foreground subscription with exactly three consumers, and all three are on screen:
///
/// 1. **The R-06 geocell anchor.** ``PassengerLiveMap/onPosition(_:)`` turns a fix into the nineteen
///    res-7 cells the live map subscribes to, and into the 30 s hysteresis that decides when to move
///    them.
/// 2. **MAP-02's accuracy circle** and §0.3's blue user dot.
/// 3. **A pickup default** — SCR-PI-009's and SCR-PI-013's *"current location"*, and SCR-PI-011's
///    live GPS pin.
///
/// A protocol with a Core Location implementation because a model test needs to feed positions
/// rather than wait for a satellite, and because `CLLocationManager` asks the user for something the
/// moment it is started.
protocol PassengerLocationSource: AnyObject {

    /// Fixes while something is subscribed, starting with the last known one.
    ///
    /// **Cold**: the manager starts on the first subscriber and stops with the last, so a screen
    /// that is not visible does not hold a location subscription open. That is the same contract the
    /// Android `callbackFlow` has, and on this platform it matters more — the blue status-bar
    /// indicator stays lit for as long as anything is subscribed.
    var fixes: AnyPublisher<PassengerFix, Never> { get }
}

/// ``PassengerLocationSource`` over Core Location.
///
/// **`kCLLocationAccuracyHundredMeters`, not `Best`, and the reason is R-06.** A passenger's
/// position decides which **res-7 cell** they are in — a hexagon about 1.2 km across — and moves the
/// subscription at most once every thirty seconds anyway (`GeoCells.BOUNDARY_HYSTERESIS`). Asking
/// for the driver's metre-level accuracy would spend a phone's battery computing the same nineteen
/// cells over and over. The one screen that genuinely needs precision is SCR-PI-011's pickup pin
/// (P-02), and it asks for its own.
///
/// **`distanceFilter` rather than a timer.** Δ Section C: the Android twin sets a ten-second
/// interval on the fused provider, and `CLLocationManager` has no interval at all — it has a
/// *distance* filter, which is the better fit here anyway. 250 m is well inside a res-7 hexagon, so
/// a boundary crossing is never missed, and a passenger standing still produces no callbacks at all,
/// which is what ``PassengerLiveMap/refreshCells()`` exists to cover.
///
/// **It asks for nothing.** `requestWhenInUseAuthorization` is SCR-PI-005's, and nothing on this
/// path is reachable before it — starting updates without a grant simply produces no fixes rather
/// than an error, which is why the authorisation state is not checked here.
final class CoreLocationPassengerSource: NSObject, PassengerLocationSource, CLLocationManagerDelegate {

    private let manager = CLLocationManager()
    private let subject = PassthroughSubject<PassengerFix, Never>()
    private var subscribers = 0

    override init() {
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyHundredMeters
        manager.distanceFilter = CoreLocationPassengerSource.distanceFilterMetres
        // A passenger's map is portrait and the fix is not a heading; turning this off stops the
        // system rotating coordinates for a course this app never reads.
        manager.pausesLocationUpdatesAutomatically = true
    }

    var fixes: AnyPublisher<PassengerFix, Never> {
        subject
            .handleEvents(
                receiveSubscription: { [weak self] _ in self?.start() },
                receiveCancel: { [weak self] in self?.stop() }
            )
            .eraseToAnyPublisher()
    }

    private func start() {
        subscribers += 1
        guard subscribers == 1 else { return }
        // The last known fix first, so a map opened indoors joins the right nineteen cells
        // immediately instead of sitting over Colombo Fort until the first callback arrives.
        if let last = manager.location { publish(last) }
        manager.startUpdatingLocation()
    }

    private func stop() {
        subscribers = max(0, subscribers - 1)
        guard subscribers == 0 else { return }
        manager.stopUpdatingLocation()
    }

    /// `nonisolated` in spirit: Core Location calls this on the thread the manager was created on,
    /// which is the main one here. Nothing in it touches the main actor's state beyond the subject.
    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {
        guard let last = locations.last else { return }
        publish(last)
    }

    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {
        // Not surfaced. A denied grant, a device in aeroplane mode and a momentary failure all land
        // here, and the passenger's answer to all three is the same: the map opens on Colombo and
        // the destination field still works. SCR-PI-005 is where a denial is explained.
    }

    private func publish(_ location: CLLocation) {
        let coordinate = location.coordinate
        // **A non-finite coordinate never reaches the grid.** `SharedH3Grid` cannot throw — the
        // Kotlin interface has no failure channel — so the one input H3 genuinely refuses is
        // filtered at the source instead.
        guard coordinate.latitude.isFinite, coordinate.longitude.isFinite else { return }

        subject.send(
            PassengerFix(
                lat: coordinate.latitude,
                lng: coordinate.longitude,
                // Core Location reports a **negative** accuracy when it has none, which drawn as a
                // radius would be an inverted circle.
                accuracyMetres: location.horizontalAccuracy > 0 ? location.horizontalAccuracy : 0
            )
        )
    }

    /// Well inside a res-7 hexagon (~1.2 km across), so a boundary crossing is never missed.
    private static let distanceFilterMetres: CLLocationDistance = 250
}
