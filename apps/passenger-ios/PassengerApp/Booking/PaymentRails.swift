import Foundation
import MageRideShared

/// The payment rails that survive AL-57 and AL-59, and the reason two of them died.
///
/// **`onepay` and platform-`lankaqr` are no longer ride methods.** AL-57: OnePay has one merchant
/// account per merchant, so a card fare could only ever land in MageRide's own account — card
/// acceptance survives one step earlier, as a **wallet top-up** where MageRide legitimately is the
/// payee, and the fare is paid with `wallet`. AL-59: the `lankaqr` rail pointed at the *platform's*
/// merchant and collapses into `scan_driver_qr` — the **driver's own** bank QR, settled by AL-47
/// attestation because money into somebody else's bank produces no platform webhook.
///
/// **There is therefore no surcharge on any surviving rail**, and nothing in this file can carry
/// one. The SCR-PI-016 wireframe still draws OnePay at +5 %; it predates the 2026-08-01
/// payment-custody change set, exactly as its Android twin does.
///
/// **Cluster 3 needs the rail list first, so it lives here** — `apps/passenger-android` puts it in
/// `ride/`, which is C098's folder on this side and does not exist yet. C098 owns SCR-PI-016 and
/// should **extend this type** rather than open a second one: `preferable`, `caption(_:)`,
/// `storedValueOf(_:)` and `fromStored(_:)` are its four, and the Android original is the reference.
enum PaymentRails {

    /// What a **passenger ride** offers, in the wireframe's order. Cash first because it is the
    /// default and the one that always works.
    static let ride: [PaymentMethod] = [PaymentMethod.cash, PaymentMethod.wallet, PaymentMethod.scanDriverQr]

    /// A parcel adds COD (US-20.8) — the recipient pays the driver on delivery.
    static let parcel: [PaymentMethod] = ride + [PaymentMethod.cod]

    /// The rails this app will never offer again. Named so a reader knows the omission is a
    /// decision rather than an oversight.
    static let retired: Set<PaymentMethod> = [PaymentMethod.onepay, PaymentMethod.lankaqr]

    /// A stored wire value back to its rail, or `nil` for one this app no longer offers.
    ///
    /// Matched against ``parcel`` — the widest surviving set — rather than against the whole
    /// `PaymentMethod` domain, so a row still carrying `lankaqr` or `onepay` (written before
    /// AL-57/AL-59) answers `nil` and the caller falls back to Cash. Pre-selecting a rail no screen
    /// draws would be a booking on a method the passenger cannot see, let alone change.
    static func fromWire(_ wire: String) -> PaymentMethod? {
        parcel.first { $0.wire == wire }
    }

    /// The rail's trilingual label key.
    static func labelKey(_ method: PaymentMethod) -> String {
        switch method {
        case PaymentMethod.cash: return "payment_cash"
        case PaymentMethod.wallet: return "payment_wallet"
        case PaymentMethod.scanDriverQr: return "payment_driver_qr"
        case PaymentMethod.cod: return "payment_cod"
        // Unreachable through `ride`/`parcel`, but `PaymentMethod` still declares them because
        // `:shared` types the whole `fares.ride_payments.method` domain, historical rows included.
        default: return "payment_retired"
        }
    }

    /// The booking-time value for a chosen settlement rail.
    ///
    /// **A gap, expressed as honestly as it can be.** `RideRequest.paymentMethod` is typed against
    /// `ride.yaml`'s enum, which has no `wallet` and no `scan_driver_qr` — the AL-57/AL-59 change set
    /// updated `fares.ride_payments.method` and left the booking column behind. A booking therefore
    /// records `cod` for a parcel and `cash` for everything else, and the real rail is chosen on
    /// SCR-PI-016 *after* the ride, which is where D-10 starts anyway.
    ///
    /// This is not a silent downgrade: nothing about the ride depends on the booking-time value,
    /// fare-svc takes the settlement method from `POST /v1/fare/pay`, and the passenger picks it
    /// again on a screen that shows them the amount. What it costs is the *intent* — dispatch cannot
    /// know a passenger meant to pay by wallet. Recorded in the C080 handoff and restated in C097's.
    static func bookingValueOf(_ method: PaymentMethod) -> RidePaymentMethod {
        method == PaymentMethod.cod ? RidePaymentMethod.cod : RidePaymentMethod.cash
    }
}
