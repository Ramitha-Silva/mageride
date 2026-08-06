import FirebaseCore
import FirebaseMessaging
import Foundation
import UIKit
import UserNotifications

/// This install's push registration token, or `nil`.
///
/// `POST /v1/auth/otp/request` carries it (`RequestOtpRequest.fcmToken`) so the first notification a
/// new driver is sent — the vehicle-approval push, US-2.14 — has somewhere to land before they have
/// opened the app a second time.
///
/// **APNs via FCM** (D2' §C's push row), which on this platform means: iOS mints the APNs device
/// token, `FirebaseMessaging` swaps it for an FCM registration token, and the platform stores that.
/// notification-svc addresses one token type, so an iOS build that registered its raw APNs token
/// would be a second address format the server has no branch for.
///
/// **`nil` is a supported answer, not a failure.** There is no `GoogleService-Info.plist` in this
/// repository — C124 owns the Firebase project and D7' §13's secrets — so `FirebaseApp.configure()`
/// has nothing to configure from and is deliberately not called on a build without it. Failing a
/// driver's login over a push token that would be inert anyway is the wrong trade, so the absence
/// is swallowed here and the sign-in goes ahead without one.
final class PushTokenProvider {

    /// Whether a Firebase project is present in this build. `false` on every build produced today.
    ///
    /// Checked by looking for the plist rather than by catching the exception `configure()` raises:
    /// that failure is an Objective-C exception, which Swift cannot catch, so the app would
    /// terminate rather than degrade.
    static var isConfigured: Bool {
        Bundle.main.path(forResource: "GoogleService-Info", ofType: "plist") != nil
    }

    /// Configures Firebase if this build has a project. Called once, from the app delegate.
    static func configureIfAvailable() {
        guard isConfigured, FirebaseApp.app() == nil else { return }
        FirebaseApp.configure()
    }

    /// Hands the APNs device token to Firebase, which is what mints the FCM token.
    ///
    /// Without this call `Messaging.messaging().token` never resolves on a real device: FCM has no
    /// address to map until iOS has given the app one.
    static func onApnsToken(_ deviceToken: Data) {
        guard isConfigured else { return }
        Messaging.messaging().apnsToken = deviceToken
    }

    /// Reads the registration token. Answers `nil` rather than throwing.
    func current() async -> String? {
        guard PushTokenProvider.isConfigured else { return nil }
        return try? await Messaging.messaging().token()
    }

    /// Asks for the authorisation E-01 needs, and registers with APNs on success.
    ///
    /// `.alert`, `.sound` and `.badge` — a fifteen-second offer that arrives silently is an offer
    /// the driver loses. Provisional authorisation is deliberately not requested: it delivers
    /// quietly to the notification centre, which is the one place a ride offer must not go.
    @discardableResult
    func requestAuthorisation() async -> Bool {
        let centre = UNUserNotificationCenter.current()
        let granted = (try? await centre.requestAuthorization(options: [.alert, .sound, .badge])) ?? false
        if granted {
            await MainActor.run { UIApplication.shared.registerForRemoteNotifications() }
        }
        return granted
    }
}
