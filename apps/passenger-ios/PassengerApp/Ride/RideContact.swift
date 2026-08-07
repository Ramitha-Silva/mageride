import Foundation
import UIKit

/// Placing a call to the driver, and the one platform difference AL-48 turns on.
///
/// **A `tel:` URL on iOS *places* the call; Android's `ACTION_DIAL` only opens the dialler.** That is
/// a real Section C delta and the driver app records it (`SystemRideContact`, C088): dialling over a
/// call that is already up hangs it up, so the dial is gated on `CXCallObserver`. This app has the
/// **passenger's** half of the same problem and answers it the same way — C102's SCR-PI-028 is where
/// a VoIP call actually exists, and *"Call normally instead?"* after a failed one is precisely the
/// moment a stale reported call would silently swallow the tap.
///
/// A protocol because `UIApplication` is not something a model test can hold, and *"the sheet dials
/// the number the contract gave us, and nothing else"* is the whole of what AL-48 asks a client to
/// get right.
///
/// `@MainActor` because the implementation is: `UIApplication.open` is a main-thread call.
@MainActor
protocol RideContact: AnyObject {

    /// Dials `phone` — E.164, as `RideDetail.counterpartyPhone` carries it.
    ///
    /// - Returns: `false` when the handset refused. A device with no cellular radio (an iPad, a
    ///   simulator) claims no `tel:` URL at all, and a screen that assumed success would leave the
    ///   passenger believing a call was ringing.
    @discardableResult
    func dial(_ phone: String) async -> Bool
}

/// ``RideContact`` over `UIApplication.open`.
///
/// The number is percent-encoded before it becomes a URL. `PhoneE164` is `+94…` and `+` is legal in a
/// `tel:` path, but the value arrives from the network and a client that pasted it straight into a
/// `URL(string:)` would answer `nil` for anything a contract change later allowed through.
@MainActor
final class SystemRideContact: RideContact {

    private let application: UIApplication

    init(application: UIApplication = .shared) {
        self.application = application
    }

    @discardableResult
    func dial(_ phone: String) async -> Bool {
        let allowed = CharacterSet(charactersIn: "+0123456789")
        let digits = phone.addingPercentEncoding(withAllowedCharacters: allowed) ?? phone
        guard let url = URL(string: "tel:\(digits)") else { return false }

        // The completion-handler form wrapped by hand rather than the `async` overload, which is what
        // `apps/driver-ios`'s `SystemPaymentHandoff` does and for the same reason: the completion is
        // the only honest answer to *"did anything take it"*, and this app's floor is iOS 16.
        return await withCheckedContinuation { continuation in
            application.open(url, options: [:]) { opened in
                continuation.resume(returning: opened)
            }
        }
    }
}

/// Leaving the app to pay — a bank app on AL-15's LankaQR link.
///
/// **This is not a MageRide payment page, and there is none to open.** AL-59 retired the platform's
/// own merchant: the passenger pays the **driver's** QR, so what leaves the app is a link the
/// passenger's own bank app claims, and the money never passes through the platform. That is also why
/// there is no `SFSafariViewController` here where `apps/driver-ios` has one — the driver's wallet
/// top-up has a hosted page and a ride fare has not.
///
/// A protocol for ``RideContact``'s reason, and because *"nothing claimed the link"* is a real state
/// SCR-PI-017 has to draw: AL-15 makes the QR scan the fallback for exactly that handset.
@MainActor
protocol BankAppHandoff: AnyObject {

    /// Opens the LankaQR *Pay* link in whichever bank app claims it.
    ///
    /// - Returns: `false` when nothing on the handset did — the condition that sends the passenger
    ///   back to the camera.
    func openBankApp() async -> Bool

    /// The same hand-off for a link the **server minted** (Δ C100).
    ///
    /// SCR-PI-017 has no URL to open — see ``SystemBankAppHandoff/lankaQrScheme``, and AL-59's reason
    /// for it — but SCR-PI-025a does: `POST /v1/mode-b/subscriptions/{id}/pay` answers a
    /// `redirectUrl`, which `:shared`'s `PaymentMethods.lankaQrAction` resolves to
    /// `FarePaymentAction.OpenBankApp(url)`. Extending this seam rather than opening a second one is
    /// the call `apps/passenger-ios/CLAUDE.md` records for ``PaymentRails``: one place in this app
    /// leaves it for a bank, and *"nothing claimed the link"* means the same thing on both screens.
    ///
    /// - Returns: `false` when nothing on the handset claimed it, which is AL-15's fallback condition.
    func openBankApp(url: String) async -> Bool
}

/// ``BankAppHandoff`` over `UIApplication.open`.
///
/// ### Why "try, then fall back" rather than "ask, then open"
///
/// `canOpenURL` is the ask, and on this platform it answers `false` for any scheme not listed in
/// `LSApplicationQueriesSchemes` — a list capped at fifty entries, which a LankaQR *Pay* link cannot
/// be enumerated into because its scheme is the **issuing bank's**. `apps/driver-ios`'s
/// `SystemPaymentHandoff` hit the same wall and made the same call; the Android twin hits it from the
/// other direction through `targetSdk` 30 package visibility. **Opening is never filtered, only
/// asking is**, so this opens and reports what happened.
@MainActor
final class SystemBankAppHandoff: BankAppHandoff {

    private let application: UIApplication

    init(application: UIApplication = .shared) {
        self.application = application
    }

    func openBankApp() async -> Bool {
        await openBankApp(url: SystemBankAppHandoff.lankaQrScheme)
    }

    func openBankApp(url: String) async -> Bool {
        guard let target = URL(string: url) else { return false }
        // A custom scheme, opened plainly: the completion is `false` when no app is registered for
        // it, which is AL-15's fallback condition and the whole reason this returns a `Bool`.
        return await withCheckedContinuation { continuation in
            application.open(target, options: [:]) { opened in
                continuation.resume(returning: opened)
            }
        }
    }

    /// AL-15's deep link, with no payload.
    ///
    /// The passenger's bank app opens and they complete the payment there against the driver's QR.
    /// **This app has no merchant reference to pre-fill** — the driver's is on their own payout
    /// profile and not in this client — which is exactly the AL-59 arrangement, and the same constant
    /// `apps/passenger-android`'s `PayFareScreen` carries.
    static let lankaQrScheme = "lankaqr://pay"
}
