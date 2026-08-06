import CoreLocation
import Foundation
import UIKit
import UserNotifications

/// The two rows of SCR-DI-007, and what each of them actually asks the OS for.
///
/// **Δ Section C — two rows here, five on Android, and the spec says so.** D2' §SCR-DA/DI-007
/// `[DELTA:PLATFORM]` names the two sets separately: *"Android: ACCESS_BACKGROUND_LOCATION +
/// FOREGROUND_SERVICE_LOCATION + POST_NOTIFICATIONS + battery-optimization intent. **iOS:
/// `requestAlwaysAuthorization` + notification auth**"*, and `driver_ios.html` draws exactly those
/// two rows where `driver_android.html` draws four. The missing three are not omissions:
///
/// * **Foreground and background location are one grant on iOS.** `requestAlwaysAuthorization` is a
///   single authorisation with a single row; Android has to ask twice because from API 30 the
///   background grant is only offered after the foreground one exists.
/// * **There is no battery-optimisation exemption.** iOS has no equivalent of Doze's allow-list and
///   no intent to open one. What buys a driver app its background runtime is the `location`
///   background mode, which is declared in `Info.plist` and needs no permission of its own.
/// * **There is no "display over other apps".** An iOS app cannot draw over another; the offer
///   takeover Android puts in an overlay is a notification here, which is the notifications row.
///
/// @property titleKey The row's label.
/// @property rationaleKey The one line under it — *why*, in the driver's language, before the sheet.
/// @property symbolName The wireframe's row glyph, as an SF Symbol.
enum DriverPermission: String, CaseIterable, Identifiable {

    /// `CLLocationManager.requestAlwaysAuthorization` — publishing with the app behind the lock
    /// screen, which is where a mounted handset spends a ride (D6' §3).
    case locationAlways

    /// Notification authorisation — E-01's offers live 15 seconds; an unnotifiable driver is
    /// undispatchable.
    case notifications

    var id: String { rawValue }

    var titleKey: String {
        switch self {
        case .locationAlways: return "permission_location_always_title"
        case .notifications: return "permission_notifications_title"
        }
    }

    var rationaleKey: String {
        switch self {
        case .locationAlways: return "permission_location_always_rationale"
        case .notifications: return "permission_notifications_rationale"
        }
    }

    var symbolName: String {
        switch self {
        case .locationAlways: return "location.fill"
        case .notifications: return "bell.fill"
        }
    }
}

/// Reads and asks for the SCR-DI-007 set.
///
/// A protocol so the screen can be driven by a fake: every member below is a system API whose
/// answer in a unit test is whatever the test host happens to have been granted, and a screen that
/// believed those answers would report a permission state nobody set.
///
/// `@MainActor` because the implementation is: `CLLocationManager` and its delegate are main-thread
/// APIs, and a main-actor method cannot satisfy a nonisolated protocol requirement.
@MainActor
protocol DriverPermissions: AnyObject {

    /// Re-reads whatever the system currently says. Call on appear and on every return to the
    /// foreground — a Settings trip reports back no other way.
    func refresh() async

    /// Whether [permission] is currently held.
    func isGranted(_ permission: DriverPermission) -> Bool

    /// Whether the system will still show a sheet for [permission], or whether only Settings can
    /// change it now — D2's *"denied → Settings deep-link"*.
    func isDeniedPermanently(_ permission: DriverPermission) -> Bool

    /// Asks for [permission]. Answers whether it is held afterwards.
    func request(_ permission: DriverPermission) async -> Bool

    /// Opens this app's own page in Settings.
    func openSettings()
}

/// ``DriverPermissions`` over CoreLocation and `UNUserNotificationCenter`.
///
/// Its own `CLLocationManager` rather than ``PositionService``'s: this one exists to ask a question
/// on one screen, and the publisher's manager is configured for background navigation and starts
/// updating the moment it is authorised. Two managers is the supported shape — authorisation is a
/// property of the app, not of a manager instance.
@MainActor
final class SystemDriverPermissions: NSObject, DriverPermissions, CLLocationManagerDelegate {

    private let manager = CLLocationManager()
    private let pushTokens: PushTokenProvider
    private var pendingLocation: CheckedContinuation<Bool, Never>?

    /// The last notification authorisation read. `UNUserNotificationCenter` answers asynchronously
    /// and a SwiftUI row needs a value now, so it is cached and refreshed by ``refresh()``.
    private var notificationStatus: UNAuthorizationStatus = .notDetermined

    init(pushTokens: PushTokenProvider) {
        self.pushTokens = pushTokens
        super.init()
        manager.delegate = self
    }

    func refresh() async {
        notificationStatus = await UNUserNotificationCenter.current().notificationSettings()
            .authorizationStatus
    }

    func isGranted(_ permission: DriverPermission) -> Bool {
        switch permission {
        case .locationAlways:
            return manager.authorizationStatus == .authorizedAlways
        case .notifications:
            return notificationStatus == .authorized || notificationStatus == .provisional
        }
    }

    func isDeniedPermanently(_ permission: DriverPermission) -> Bool {
        switch permission {
        case .locationAlways:
            // `.authorizedWhenInUse` counts as answered: iOS shows the "keep using in the
            // background?" prompt on its own schedule and a second `requestAlwaysAuthorization`
            // does nothing, so Settings is the only place the driver can complete the upgrade.
            return manager.authorizationStatus != .notDetermined
                && manager.authorizationStatus != .authorizedAlways
        case .notifications:
            return notificationStatus == .denied
        }
    }

    /// Asks the system, once.
    ///
    /// A permission iOS has already refused shows no sheet at all — the call returns immediately
    /// and nothing happens on screen — which is why the screen sends a permanently-denied row to
    /// Settings instead of calling this.
    func request(_ permission: DriverPermission) async -> Bool {
        switch permission {
        case .locationAlways:
            return await requestAlwaysLocation()
        case .notifications:
            // `.alert`, `.sound` and `.badge` — a fifteen-second offer that arrives silently is an
            // offer the driver loses. Provisional authorisation is deliberately not requested: it
            // delivers quietly to the notification centre, which is the one place a ride offer must
            // not go. Registering with APNs on success is ``PushTokenProvider``'s half.
            let granted = await pushTokens.requestAuthorisation()
            await refresh()
            return granted
        }
    }

    func openSettings() {
        guard let url = URL(string: UIApplication.openSettingsURLString) else { return }
        UIApplication.shared.open(url)
    }

    // MARK: - CoreLocation

    /// `requestAlwaysAuthorization` reports through the delegate, not a completion handler, so the
    /// call is bridged onto one continuation. Only ever one in flight: the row that started it is
    /// disabled while it runs.
    private func requestAlwaysLocation() async -> Bool {
        guard manager.authorizationStatus == .notDetermined
            || manager.authorizationStatus == .authorizedWhenInUse
        else {
            return isGranted(.locationAlways)
        }

        return await withCheckedContinuation { continuation in
            pendingLocation = continuation
            manager.requestAlwaysAuthorization()
        }
    }

    nonisolated func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) {
        Task { @MainActor [weak self] in
            guard let self, let continuation = self.pendingLocation else { return }
            self.pendingLocation = nil
            continuation.resume(returning: self.isGranted(.locationAlways))
        }
    }
}
