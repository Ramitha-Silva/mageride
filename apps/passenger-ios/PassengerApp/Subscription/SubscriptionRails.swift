import Foundation
import MageRideShared

/// How a Mode B month can be paid, after AL-59 — and why OnePay is not on the list.
///
/// **The money is the fleet owner's, not MageRide's** (AL-24, §18b). `payTo` is the owner's own bank
/// account or their bank-app LankaQR image (AL-49), and OnePay has **one merchant account per
/// merchant** — routing a subscription through it would land a passenger's payment in MageRide's
/// account and make a pass-through into platform revenue with a manual payout behind it. So AL-59
/// removed the rail, deleted `POST /v1/mode-b/pay/onepay/webhook` from the contract, and left
/// `subscription.payments.method` declaring `onepay` only for rows written before it.
///
/// **Nothing here carries a surcharge.** `passenger_ios.html`'s SCR-PI-025a still draws
/// `💳 OnePay · cards / wallets · +5%` and rebuilding the sheet from that drawing is the mistake
/// ``SubscriptionRailsTests`` exists to catch; the four below all move the face value of the fare and
/// no more. Same outcome C098's ``PaymentRails`` reached on the ride side, for the same reason, and
/// the wireframe needs the same micro-change-set the C082 handoff already asked for.
///
/// The two halves of the list behave completely differently and `SubscriptionPayMethod` does not say
/// so — `:shared`'s `ModeBPaymentRules` does, and this cluster asks it rather than re-deciding: the
/// LankaQR pair hand off to a bank app, while `online_transfer` and `cash` settle on the **owner's**
/// human confirmation and sit unpaid until it comes.
enum SubscriptionRails {

    /// What SCR-PI-025a offers, in the wireframe's order.
    ///
    /// Cash is last because it is the only one that cannot be completed in the app: the passenger
    /// hands money to a collector and waits for the owner to mark it received (US-23.6). It takes the
    /// row the retired OnePay rail left vacant, which is what D2' §16e and US-23.6 ask for.
    static let methods: [SubscriptionPayMethod] = [
        SubscriptionPayMethod.lankaqrDeeplink,
        SubscriptionPayMethod.lankaqrScan,
        SubscriptionPayMethod.onlineTransfer,
        SubscriptionPayMethod.cash,
    ]

    /// The rail this app will never offer again. Named so a reader knows the omission is a decision.
    static let retired: [SubscriptionPayMethod] = [SubscriptionPayMethod.onepay]

    /// The row's title.
    ///
    /// Every declared method has one, including the retired rail: `SubscriptionPayMethod` types the
    /// whole `subscription.payments.method` domain and SCR-PI-025b renders history rows that predate
    /// AL-59, so a table that fell through to an empty string would print a blank method on a real
    /// statement.
    static func labelKey(_ method: SubscriptionPayMethod) -> String {
        if method == SubscriptionPayMethod.lankaqrDeeplink { return "subscription_pay_lankaqr_deeplink" }
        if method == SubscriptionPayMethod.lankaqrScan { return "subscription_pay_lankaqr_scan" }
        if method == SubscriptionPayMethod.onlineTransfer { return "subscription_pay_transfer" }
        if method == SubscriptionPayMethod.cash { return "subscription_pay_cash" }
        return "subscription_pay_retired"
    }

    /// The one-line explanation under it.
    static func captionKey(_ method: SubscriptionPayMethod) -> String {
        if method == SubscriptionPayMethod.lankaqrDeeplink { return "subscription_pay_lankaqr_deeplink_caption" }
        if method == SubscriptionPayMethod.lankaqrScan { return "subscription_pay_lankaqr_scan_caption" }
        if method == SubscriptionPayMethod.onlineTransfer { return "subscription_pay_transfer_caption" }
        if method == SubscriptionPayMethod.cash { return "subscription_pay_cash_caption" }
        return "subscription_pay_retired"
    }

    /// The wireframe's leading glyph on each `.gr` row — `🏦 📷 🧾 💵`, as SF Symbols.
    ///
    /// A symbol rather than the emoji the mock draws, which is D2' §C's icon row for this platform and
    /// the call every other row in this app already makes (see ``PassengerMenuDestination/symbolName``).
    static func symbolName(_ method: SubscriptionPayMethod) -> String {
        if method == SubscriptionPayMethod.lankaqrDeeplink { return "building.columns.fill" }
        if method == SubscriptionPayMethod.lankaqrScan { return "qrcode.viewfinder" }
        if method == SubscriptionPayMethod.onlineTransfer { return "doc.text.fill" }
        if method == SubscriptionPayMethod.cash { return "banknote.fill" }
        return "xmark.circle.fill"
    }

    /// Whether a rail is one of the four this sheet offers.
    ///
    /// Written against ``methods`` rather than against `onepay` so that a rail a later contract adds
    /// is *excluded* until somebody puts it in the list — the safe direction for a table whose whole
    /// job is to keep one entry out.
    static func isOffered(_ method: SubscriptionPayMethod) -> Bool {
        methods.contains { $0 == method }
    }
}
