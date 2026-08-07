import Foundation
import UIKit
import UserNotifications

/// The three callbacks SwiftUI has no equivalent for.
///
/// 1. **The APNs device token.** `didRegisterForRemoteNotificationsWithDeviceToken` is the only
///    delivery point, and without handing it to Firebase there is no FCM registration token to send
///    to notification-svc (D2' §C: "Push — APNs via FCM").
/// 2. **A push that arrives while the app is running.** `UNUserNotificationCenterDelegate` decides
///    whether it is shown; E-01's fifteen-second offer must be, even in the foreground, because a
///    driver looking at their wallet still has to see it.
/// 3. **A push that is tapped.** The deep link is on the response and nowhere else.
///
/// **Two notification categories, not one per type** — the same split as the Android channels, for
/// the same reason the C067 handoff gives: the unit a user silences must not put a ride offer and a
/// wallet reminder in one bucket. iOS has no per-category mute in Settings, so the categories here
/// buy the *interruption level* rather than a user control: E-01 is `.timeSensitive`, which pierces
/// a Focus, and nothing else is. That is a Section C asymmetry worth knowing about — on Android the
/// driver can silence wallet notices and keep offers; on iOS they cannot.
final class DriverAppDelegate: NSObject, UIApplicationDelegate {

    private weak var graph: DriverGraph?

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        registerCategories()
        return true
    }

    /// Hands the delegate the graph once SwiftUI has built it.
    ///
    /// The delegate is constructed by UIKit before `@StateObject` runs, so it cannot take the graph
    /// in an initialiser. `weak` because the graph owns the app's lifetime and this must not extend
    /// it.
    @MainActor
    func attach(graph: DriverGraph) {
        self.graph = graph
        Task { await graph.pushTokens.requestAuthorisation() }
    }

    func application(
        _ application: UIApplication,
        didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data
    ) {
        PushTokenProvider.onApnsToken(deviceToken)
    }

    func application(
        _ application: UIApplication,
        didFailToRegisterForRemoteNotificationsWithError error: Error
    ) {
        // No push on this install. Not fatal and not worth surfacing: a driver can still work, and
        // the only thing they lose is the background offer — which `signalr-hub.md` §6 already says
        // a backgrounded app cannot receive any other way.
    }

    /// The two categories, declared at launch so they exist before the first push arrives.
    private func registerCategories() {
        let rides = UNNotificationCategory(
            identifier: PushCategory.rideOffers,
            actions: [],
            intentIdentifiers: [],
            options: []
        )
        let general = UNNotificationCategory(
            identifier: PushCategory.general,
            actions: [],
            intentIdentifiers: [],
            options: []
        )
        UNUserNotificationCenter.current().setNotificationCategories([rides, general])
    }
}

extension DriverAppDelegate: UNUserNotificationCenterDelegate {

    /// A push while the app is in the foreground.
    ///
    /// Shown, always. E-01's offer has a fifteen-second life and a driver reading their earnings is
    /// not "already looking at it"; suppressing a foreground notification is the default iOS
    /// behaviour and it is the wrong one here.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        let message = PushMessage.from(userInfo: notification.request.content.userInfo)
        Task { @MainActor in self.deliver(message) }
        completionHandler([.banner, .sound, .list])
    }

    /// A push that was tapped.
    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let message = PushMessage.from(userInfo: response.notification.request.content.userInfo)
        Task { @MainActor in self.deliver(message) }
        completionHandler()
    }

    /// Hands one push to **both** subscribers (Δ C088).
    ///
    /// ``PushRouter`` decides which screen to open — for a `ride_offer` that is Home, because the offer
    /// is a takeover the dashboard presents rather than a destination. ``OfferInbox`` is what puts the
    /// offer *in* `OfferSession`, which is the slot SCR-DI-014 is drawn from. Neither can do the
    /// other's half: routing without the inbox opens an empty dashboard, and the inbox without routing
    /// raises the takeover behind whatever screen the driver was on.
    @MainActor
    private func deliver(_ message: PushMessage) {
        graph?.offers.receive(message)
        graph?.pushes.offer(message)
    }
}

/// The notification category ids this app publishes on.
///
/// Two, mirroring `apps/driver-android/.../push/PushChannels.kt`. The split follows
/// notification-svc's own `priority` field — `high` for E-01's fifteen-second offer, `normal` for
/// everything else — so the server needs no per-platform branch to address them.
enum PushCategory {
    /// E-01 ride offers and ride-state changes.
    static let rideOffers = "ride_offers"
    /// Wallet, daily fee, documents, announcements.
    static let general = "general"

    /// Which category a `data.kind` belongs to.
    static func category(for kind: String?) -> String {
        kind == PushMessage.kindRideOffer ? rideOffers : general
    }
}
