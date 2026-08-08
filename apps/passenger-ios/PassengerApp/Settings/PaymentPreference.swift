import Foundation
import MageRideShared

/// SCR-PI-027's **Default payment**, as the rest of the app reads it (US-22.4, AL-14).
///
/// *"Pre-selected at booking/checkout (and still changeable per trip)"* is the whole of US-22.4, and
/// this is the one value that makes it true: ``BookingDraft`` starts every booking with
/// ``preferredRail`` and SCR-PI-009's payment chip draws the draft. Change it in Settings and the
/// **next** booking opens on the new rail; the one already on screen keeps whatever the passenger
/// chose for it, because a preference is not a command.
///
/// **It is device-local on purpose, and only half of that is a choice.** `iam.users` has a column
/// for it and ``PassengerProfileRepository/saveDefaultPaymentMethod(_:)`` writes it whenever the
/// chosen rail is expressible — but `DefaultPaymentMethod` is still `[cash, lankaqr, onepay]`, so
/// the rail AL-57 introduced (`wallet`) has no value to be stored as. Holding the answer here is
/// what lets the app honour a choice the contract cannot yet carry; see
/// ``PaymentRails/storedValueOf(_:)``.
///
/// ### Why this is an extension rather than the class `apps/passenger-android` has
///
/// `settings/PaymentPreference.kt` is a small class over `AppPreferences` because on that side it is
/// the *first* reader. Here it is the third: ``BookingDraft`` and ``PaymentMethodModel`` were both
/// already spelling `preferences.defaultPaymentMethod.flatMap(PaymentRails.fromWire) ?? .cash`, and a
/// wrapper object introduced by this component would have been a **third** path onto one key unless
/// both of their constructors changed with it. Three members on the one preference protocol is the
/// same *"one door"* with no constructor churn — and it is literally the argument the Android class's
/// own KDoc makes: a preference that outlives every ride belongs in the preference file. Both call
/// sites now read ``preferredRail``.
extension AppPreferences {

    /// What the next booking starts with. Cash until the passenger says otherwise.
    ///
    /// A wire value this build does not know is a downgrade from a newer one, or a rail that has
    /// since been retired; either way it is not something to pre-select, and
    /// ``PaymentRails/fromWire(_:)`` answers `nil` for it. A rail outside ``PaymentRails/preferable``
    /// is refused for the same reason — the driver QR is a *settlement* choice the contract itself
    /// excludes from a stored preference, so it can never be what a booking opens on.
    ///
    /// `contains(where:)` rather than `contains(_:)`: a Kotlin enum reaches Swift as an
    /// `NSObject` subclass whose `Equatable` conformance comes from the ObjC overlay, and an explicit
    /// `==` is what the rest of this app already uses on one (`PaymentRails.storedValueOf`).
    var preferredRail: PaymentMethod {
        guard
            let wire = defaultPaymentMethod,
            let rail = PaymentRails.fromWire(wire),
            PaymentRails.preferable.contains(where: { $0 == rail })
        else {
            return PaymentMethod.cash
        }
        return rail
    }

    /// SCR-PI-027 chose a rail.
    func rememberRail(_ method: PaymentMethod) {
        defaultPaymentMethod = method.wire
    }

    /// Takes the account's stored default, on a handset that has none of its own.
    ///
    /// US-22.6 — the preference is *"tied to the passenger account and restored on the device"* — so
    /// the profile wins on a fresh install and loses afterwards: a passenger who changed the rail on
    /// **this** device last week should not have it silently reverted by a profile read.
    func adoptRail(_ stored: DefaultPaymentMethod?) {
        guard defaultPaymentMethod == nil else { return }
        rememberRail(PaymentRails.fromStored(stored))
    }
}
