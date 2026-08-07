import Foundation

/// The two package OTPs, and the reason this type exists at all.
///
/// **Neither OTP can be read back from the platform.**
///
/// - The **pickup** OTP (P-07, US-20.4) comes back on `POST /v1/rides/request` and *"is shown once
///   to the sender and never returned again; only its hash is stored"*. `RideDetail` has no field
///   for it and `mobile_db_schema.md` §2.3's `rides` projection has no column.
/// - The **delivery** OTP (US-20.5) arrives only in the FCM `package_picked_up` payload —
///   ``PushRouter`` documents `data.deliveryOtp` — and there is no read for it either.
///
/// C099's SCR-PI-020 and SCR-PI-021 both draw one. So this holds what the app has actually been told,
/// and says nothing when it has not been told. **Process-lifetime only**: persisting a handover code
/// would need a schema change, and inventing one would be worse than admitting the gap — a screen
/// that showed a number the driver's app would reject is the one failure a handover cannot survive.
///
/// **Written here and read by C099**, which is the reverse of the Android split (`history/` owns the
/// file there because C081 added it after C079 had shipped). It lives in this folder because
/// SCR-PI-012's booking response is the only moment the sender's code exists, and the type that
/// catches it should be next to the screen that catches it.
///
/// `@MainActor` rather than an actor: both writers are already on it — a booking response and a push
/// delegate — and a dictionary lookup is not work worth a hop.
@MainActor
final class PackageOtps {

    private var pickup: [String: String] = [:]
    private var delivery: [String: String] = [:]

    /// SCR-PI-012's booking response, on its way past. The only time the sender's code exists.
    func rememberPickup(rideId: String, otp: String?) {
        guard let otp, !otp.isEmpty else { return }
        pickup[rideId] = otp
    }

    /// The `package_picked_up` push, on its way to C099's SCR-PI-021.
    func rememberDelivery(rideId: String, otp: String?) {
        guard let otp, !otp.isEmpty else { return }
        delivery[rideId] = otp
    }

    /// The sender's code, or `nil` — which is what a reinstalled app or a cold start has.
    func pickupFor(rideId: String) -> String? { pickup[rideId] }

    /// The recipient's code, or `nil`.
    func deliveryFor(rideId: String) -> String? { delivery[rideId] }
}
