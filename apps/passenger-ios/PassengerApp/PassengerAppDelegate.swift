import Foundation
import UIKit
import UserNotifications

/// The four callbacks SwiftUI has no equivalent for.
///
/// 1. **The APNs device token.** `didRegisterForRemoteNotificationsWithDeviceToken` is the only
///    delivery point, and without handing it to Firebase there is no FCM registration token to send
///    to notification-svc (D2' §C: *"Push — APNs via FCM"*).
/// 2. **A push that arrives while the app is running.** `UNUserNotificationCenterDelegate` decides
///    whether it is shown.
/// 3. **A push that is tapped.** The deep link is on the response and nowhere else.
/// 4. **A silent push.** `content-available` is how P-02's `location_request` reaches a backgrounded
///    app, and `didReceiveRemoteNotification` is the only place it lands. That push carries no
///    deeplink and no alert — it is a *"someone is booking a ride for you, where are you?"* message
///    the app renders itself as SCR-PI-011 — so without this callback the whole proxy round trip has
///    no path into the app while it is in the background.
///
/// **Two notification categories, not one per type** — the same split as the Android channels, for
/// the same reason C076 gives: the unit a user silences must not put a ride's progress and a
/// promotional broadcast in one bucket. iOS has no per-category mute in Settings, so the categories
/// here buy the *interruption level* rather than a user control: a ride in progress is
/// `.timeSensitive`, which pierces a Focus, and nothing else is. That is a Section C asymmetry worth
/// knowing about — on Android the passenger can silence announcements and keep ride updates; on iOS
/// they cannot. C085 recorded the same from the driver side.
final class PassengerAppDelegate: NSObject, UIApplicationDelegate {

    private weak var graph: PassengerGraph?

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
    ///
    /// **It does not ask for notification authorisation**, unlike the driver app's. SCR-PI-005 is
    /// the wireframe's rationale screen and C095 owns it; asking here would put a system prompt in
    /// front of a passenger who has not seen the app yet, which is the one thing a rationale screen
    /// exists to prevent.
    @MainActor
    func attach(graph: PassengerGraph) {
        self.graph = graph
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
        // No push on this install. Not fatal and not worth surfacing: a passenger can still book,
        // and what they lose is the background half of the proxy round trip and the scheduled
        // reminder — both of which are visible on screen when the app is open.
    }

    /// The two categories, declared at launch so they exist before the first push arrives.
    private func registerCategories() {
        let rides = UNNotificationCategory(
            identifier: PushCategory.rides,
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

extension PassengerAppDelegate: UNUserNotificationCenterDelegate {

    /// A push while the app is in the foreground.
    ///
    /// Shown, always. *"Your driver has arrived"* while the passenger is reading the fare breakdown
    /// on the same screen is still news; suppressing a foreground notification is the default iOS
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
}

extension PassengerAppDelegate {

    /// A **silent** push — one carrying `content-available`.
    ///
    /// P-02's `location_request` is the push this callback exists for: notification-svc writes
    /// `{kind:'location_request', requestId, bookerName, ttl:300}` and nothing else, with no alert
    /// block and no deeplink, and the three hundred seconds start when it is sent. A rider whose app
    /// is backgrounded has no other path to SCR-PI-011.
    ///
    /// **Δ iOS.** `onMessageReceived` fires for every data message on Android; iOS hands a push to
    /// the app in three cases only — presented in the foreground, tapped, or `content-available` and
    /// the system chose to wake us. A silent push the system declines to deliver is never seen, and
    /// no client on this platform can do better. C085 recorded the same asymmetry.
    ///
    /// `.newData` rather than `.noData`, always: the app *did* do work — it resolved a destination —
    /// and reporting otherwise is how iOS learns to stop waking this app.
    func application(
        _ application: UIApplication,
        didReceiveRemoteNotification userInfo: [AnyHashable: Any],
        fetchCompletionHandler completionHandler: @escaping (UIBackgroundFetchResult) -> Void
    ) {
        let message = PushMessage.from(userInfo: userInfo)
        Task { @MainActor in
            self.deliver(message)
            completionHandler(.newData)
        }
    }

    /// Hands one push to the router, and keeps the one value a push is the only carrier of.
    ///
    /// **Two subscribers, not the driver app's three.** There is no offer inbox on this surface
    /// (E-01's fifteen-second takeover is the driver's) and no local notification inbox — there is
    /// no SCR-PI-034, and `mobile_db_schema.md` §2 gives this app no `notifications` table. So a
    /// push either names a screen or it does not, and ``PushRouter`` is what decides.
    ///
    /// **US-20.5's delivery code arrives here and nowhere else** (Δ C099). No read returns it —
    /// `RideDetail` has no field and §2.3's `rides` projection has no column — so a push that is not
    /// captured on arrival is a code SCR-PI-021 can never show. Kept **before** the routing, so that
    /// a tapped notification finds it already there by the time the screen reads it. The Android twin
    /// does the same in `PassengerMessagingService.onMessageReceived`; see ``PackageOtps``.
    @MainActor
    func deliver(_ message: PushMessage) {
        if let rideId = message.data[PushMessage.Keys.rideId] {
            graph?.packageOtps.rememberDelivery(
                rideId: rideId,
                otp: message.data[PushMessage.Keys.deliveryOtp]
            )
        }
        graph?.pushes.offer(message)
    }
}

/// The notification category ids this app publishes on.
///
/// Two, mirroring `apps/passenger-android/.../push/PushChannels.kt`. The split follows
/// notification-svc's own `priority` field — `high` for anything about a ride in progress, `normal`
/// for everything else — so the server needs no per-platform branch to address them.
enum PushCategory {
    /// Ride-state changes, the proxy round trip, package handoffs.
    static let rides = "rides"

    /// Announcements, subscriptions, support replies.
    static let general = "general"

    /// Which category a `data.kind` belongs to.
    static func category(for kind: String?) -> String {
        kind == PushMessage.kindLocationRequest ? rides : general
    }
}
