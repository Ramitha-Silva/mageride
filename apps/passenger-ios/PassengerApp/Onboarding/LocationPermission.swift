import CoreLocation
import Foundation
import UIKit

/// Where the one grant this app asks for stands.
///
/// **Three states, not two**, because iOS makes a distinction Android does not: a grant that has
/// never been asked for can still be asked for, and one that has been refused **never can be
/// again** — `requestWhenInUseAuthorization()` is a no-op after the first refusal, for the life of
/// the install. That is what makes ``denied`` a different CTA rather than a retry.
enum LocationAuthorisation: Equatable {

    /// The system dialog has not been shown. ``LocationPermission/request()`` will show it.
    case notDetermined

    /// When-in-use or always. Either is enough — this app never asks for the second.
    case granted

    /// Refused, or restricted by a profile or parental control. The only way to a grant from here
    /// is Settings.
    case denied
}

/// The one runtime permission this app asks for.
///
/// **When-in-use location, and nothing else.** The driver app asks for background location and a
/// notification grant at launch because it publishes a position stream through a shift; a passenger
/// publishes nothing (D3' §3.3). What the grant is for is the R-06 geocell anchor, MAP-02's accuracy
/// circle and a pickup that defaults to where the passenger is — all of which happen with the app
/// open, which is why `Info.plist` declares only `NSLocationWhenInUseUsageDescription` and no
/// `location` background mode.
///
/// **Notifications are deliberately not asked for here.** SCR-PI-005 is about location and says so;
/// asking for two things behind one rationale is how a passenger denies both. ``PushTokenProvider``
/// owns that prompt.
///
/// A protocol because `CLLocationManager` asks the user for something the moment it is called, which
/// a model test must not do.
protocol LocationPermission: AnyObject {

    /// Where the grant stands right now. Read on every appearance — a passenger can change it in
    /// Settings while the app is backgrounded, and a cached answer would be stale exactly then.
    var authorisation: LocationAuthorisation { get }

    /// Shows the system dialog, and answers where the grant ended up.
    ///
    /// A no-op that answers ``LocationAuthorisation/denied`` when it has already been refused — see
    /// the enum's note. Callers do not need to check first; the state is what decides which CTA is
    /// drawn, not whether this may be called.
    func request() async -> LocationAuthorisation

    /// Opens this app's page in Settings — SCR-PI-005's *"Open Settings"* on a denial.
    func openSettings()
}

/// ``LocationPermission`` over Core Location.
final class SystemLocationPermission: NSObject, LocationPermission, CLLocationManagerDelegate {

    private let manager = CLLocationManager()
    private var pending: CheckedContinuation<LocationAuthorisation, Never>?

    override init() {
        super.init()
        manager.delegate = self
    }

    var authorisation: LocationAuthorisation {
        Self.authorisation(for: manager.authorizationStatus)
    }

    /// **Resumed from the delegate, not from a poll.** `requestWhenInUseAuthorization()` returns
    /// immediately and the answer arrives on `locationManagerDidChangeAuthorization`; a caller that
    /// read `authorizationStatus` on the next line would read the state *before* the dialog.
    ///
    /// Answers at once when there is nothing to ask — a grant already given, or a refusal that
    /// cannot be re-asked. Without that guard the continuation would never be resumed, because the
    /// delegate does not fire for a call the system ignored.
    func request() async -> LocationAuthorisation {
        let current = authorisation
        guard current == .notDetermined else { return current }

        return await withCheckedContinuation { continuation in
            pending = continuation
            manager.requestWhenInUseAuthorization()
        }
    }

    func openSettings() {
        guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
        Task { @MainActor in UIApplication.shared.open(url) }
    }

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        let resolved = Self.authorisation(for: manager.authorizationStatus)
        // Still undetermined means the callback fired for something else — the delegate is called on
        // registration too, before the passenger has answered anything.
        guard resolved != .notDetermined, let continuation = pending else { return }
        pending = nil
        continuation.resume(returning: resolved)
    }

    /// **Reduced accuracy counts as granted.** iOS 14+ lets a passenger grant *approximate* location
    /// from the system dialog, and a ~3 km live map works perfectly well at that precision — the
    /// nineteen cells are res-7 hexagons about 1.2 km across. Treating it as denied would put the
    /// rationale in front of somebody who has already said yes. Same call C077 made about Android's
    /// COARSE.
    private static func authorisation(for status: CLAuthorizationStatus) -> LocationAuthorisation {
        switch status {
        case .notDetermined: return .notDetermined
        case .authorizedWhenInUse, .authorizedAlways: return .granted
        default: return .denied
        }
    }
}
