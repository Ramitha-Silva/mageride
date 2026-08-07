import MageRideShared

/// The two labels a package job is read by, on the two screens that show one.
///
/// SCR-DI-014's offer badges it before the driver accepts (`Package · M`, `Cash on delivery`) and
/// C089's SCR-DI-016a review sheet repeats both after they have. One table rather than two, because a
/// delivery whose size or payment reads differently on the two screens is a delivery the driver has to
/// check twice.
///
/// `S`/`M`/`L` and `cod` are **codes**; their names are copy, and copy lives in the three
/// `Localizable.strings` files. The same table is `apps/driver-android/.../ui/PackageLabels.kt`.
enum PackageLabels {

    /// The trilingual name of a package's size (US-20.3).
    static func size(_ size: PackageSize) -> String {
        switch size {
        case PackageSize.s: return "package_size_small"
        case PackageSize.m: return "package_size_medium"
        // `L`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default: return "package_size_large"
        }
    }

    /// How the passenger or sender chose to pay, as the driver reads it (AL-22).
    ///
    /// `nil` is `cash`: the offer envelope carries no payment method and the badge is drawn before the
    /// enrichment read lands, so the default has to be the one D5' treats as the default — a ride with
    /// no digital instrument attached is a cash ride.
    static func payment(_ method: RidePaymentMethod?) -> String {
        // Unwrapped before the switch rather than matched as an optional: a Kotlin enum reaches Swift
        // as a **class**, so a `case` over one is an `Equatable` comparison rather than a case pattern,
        // and matching a class through an `Optional` is a promotion the reader has to know about. Every
        // switch over a shared enum in this cluster has the same shape for the same reason.
        guard let method else { return "payment_cash" }
        switch method {
        case RidePaymentMethod.lankaqr: return "payment_lankaqr"
        case RidePaymentMethod.onepay: return "payment_onepay"
        case RidePaymentMethod.cod: return "payment_cod"
        default: return "payment_cash"
        }
    }
}
