import Foundation
import MageRideShared

/// The rail SCR-PI-016 confirmed, on its way to SCR-PI-017.
///
/// ### Why this type exists
///
/// `PassengerRoute.payFare(rideId:)` carries a ride and nothing else, and it cannot carry more: the
/// route table is **diffed against `PassengerRoute.kt`'s** by `NavigationShellTests`, so an
/// associated value added here alone would fail the build on the other side of a comparison this app
/// deliberately keeps. So the answer goes through a process-lifetime holder instead — the same shape
/// ``PackageOtps`` takes for P-07's code, and the same shape `apps/driver-ios`'s
/// `VehicleOnboardingSession` takes for exactly this reason.
///
/// ### The defect it closes
///
/// **On `apps/passenger-android` the chosen rail never reaches SCR-PA-017.** `PaymentMethodViewModel`
/// records `confirmed`, the screen calls `onConfirmed(method)`, and `PassengerNavHost`'s arm
/// **discards the argument** and navigates with the ride id alone — while `PayFareViewModel.method`
/// defaults to `SCAN_DRIVER_QR` and `setMethod` has no production caller. A passenger who chooses
/// **Cash** or **Wallet** therefore lands on a screen that has already posted
/// `POST /v1/fare/pay {method: scan_driver_qr}` and is asking them to scan a QR. Recorded in the C098
/// handoff as a defect found in C080.
///
/// `@MainActor` rather than an actor: both ends are already on it — a `confirm()` tap and a screen
/// initialiser — and a dictionary lookup is not work worth a hop.
@MainActor
final class PaymentSelection {

    private var chosen: [String: PaymentMethod] = [:]

    /// SCR-PI-016's Confirm.
    func choose(rideId: String, method: PaymentMethod) {
        chosen[rideId] = method
    }

    /// What SCR-PI-017 should settle on.
    ///
    /// **Falls back to the driver's QR, which is what SCR-PI-017's cell draws.** A passenger reaches
    /// that screen without passing through SCR-PI-016 in exactly one way — a cold start straight onto
    /// a `PaymentPending` ride — and the QR path is the one that asks before it acts: it renders a
    /// scan panel and settles nothing until the passenger scans or claims. Cash would mark a fare
    /// settled that nobody had handed over, and the wallet would move money on a screen the passenger
    /// had not chosen a rail on.
    func rail(for rideId: String) -> PaymentMethod {
        chosen[rideId] ?? PaymentMethod.scanDriverQr
    }

    /// Forgets a ride's answer, once it is settled. Nothing depends on this — the holder dies with
    /// the process — but a settled ride's rail is not something a later screen should be able to read.
    func forget(rideId: String) {
        chosen[rideId] = nil
    }
}
