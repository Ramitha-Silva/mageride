import Foundation
import UIKit

/// The OnePay return leg, spelled once.
///
/// Both halves of the payment hand-off name this host: ``ApiTopUpRepository`` sends it to OnePay as
/// the `returnUrl`, and ``SafariView`` dismisses the sheet when the hosted page redirects onto it. It
/// is the domain `DriverApp.entitlements` declares under `applinks:`, so a payment a driver finishes
/// in real Safari instead comes back to the app through the same URL (D2' §C).
enum PaymentReturn {

    /// The associated domain. A redirect onto this host means the gateway is finished with the driver.
    static let host = "pay.mageride.lk"

    /// The path OnePay is asked to return to.
    static let url = "https://\(host)/driver/topup/return"

    /// Whether `url` is the return leg — a match on the **host**, never on the whole string.
    ///
    /// The gateway appends its own query (`?status=`, `?ref=`) and may return to a sibling path, and a
    /// sheet that only closed on a byte-identical URL would leave the driver looking at a blank page
    /// with a Done button they have to find. Nothing is read out of the query: what became of the
    /// session is `GET /v1/wallet/topup/{topupId}`'s answer, not the gateway's redirect.
    static func isReturn(_ url: URL) -> Bool { url.host == host }
}

/// Leaving the app to pay — a bank app on a LankaQR link (AL-15).
///
/// **OnePay is deliberately not here.** On Android both rails are one `ACTION_VIEW` and one seam,
/// because the hosted page needs a browser and a browser is another app. On this platform the hosted
/// page is an `SFSafariViewController` the *screen* presents — `driver_ios.html`'s own `Δ iOS` clause
/// for SCR-DI-022 — so there is nothing for a model to ask a system to do, and the "no app could open
/// the payment page" failure the Android twin has to report simply does not exist: every iPhone has
/// SafariServices. What is left behind this seam is the one hand-off that really does leave the app.
///
/// A protocol for the reason every seam in this target is one: `UIApplication` is not something a
/// model test can hold, and whether a bank app answered is the whole of AL-15's fallback rule.
///
/// `@MainActor`, as ``RideContact`` is: `UIApplication.open` is a main-thread call and the model that
/// makes it is main-isolated already.
@MainActor
protocol PaymentHandoff: AnyObject {

    /// Opens `url` in the driver's own bank app.
    ///
    /// - Returns: `false` when nothing on the handset claimed it — AL-15's fallback condition, and
    ///   what makes the screen render the code instead.
    func openBankApp(_ url: String) async -> Bool
}

/// ``PaymentHandoff`` over `UIApplication.open`.
///
/// ### Why "try, then fall back" rather than "ask, then open"
///
/// AL-15 makes the QR a fallback *for a handset no bank app can open the link on*, so the natural
/// shape is to ask first. `canOpenURL` is the ask, and on this platform it answers `false` for any
/// scheme not listed in `LSApplicationQueriesSchemes` — a list capped at fifty entries, which a
/// LankaQR *"Pay"* link cannot be enumerated into because its scheme is the **issuing bank's**. That
/// is the same wall the Android twin hits with `targetSdk` 30 package-visibility filtering, reached by
/// a different road. **Opening is never filtered**, only asking is, so this opens and reports what
/// happened.
///
/// `open(_:options:completionHandler:)`'s completion is the honest answer on both branches:
///
/// - An **https** link is opened `universalLinksOnly`, which succeeds only if an installed app claims
///   that domain. Without the flag iOS would hand it to Safari and report success, and the driver
///   would be looking at a bank's web page instead of their bank app — a false positive that would
///   suppress the fallback AL-15 exists to provide.
/// - A **custom-scheme** link is opened plainly, and the completion is `false` when no app is
///   registered for the scheme.
@MainActor
final class SystemPaymentHandoff: PaymentHandoff {

    private let application: UIApplication

    init(application: UIApplication = .shared) {
        self.application = application
    }

    func openBankApp(_ url: String) async -> Bool {
        guard let target = URL(string: url), let scheme = target.scheme?.lowercased() else { return false }
        let isWeb = scheme == "http" || scheme == "https"
        let options: [UIApplication.OpenExternalURLOptionsKey: Any] = isWeb ? [.universalLinksOnly: true] : [:]

        return await withCheckedContinuation { continuation in
            application.open(target, options: options) { opened in
                continuation.resume(returning: opened)
            }
        }
    }
}
