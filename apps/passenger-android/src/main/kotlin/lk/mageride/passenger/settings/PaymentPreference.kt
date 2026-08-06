package lk.mageride.passenger.settings

import lk.mageride.passenger.ride.PaymentRails
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod

/**
 * SCR-PA-027's **Default payment**, as the rest of the app reads it (US-22.4, AL-14).
 *
 * *"Pre-selected at booking/checkout (and still changeable per trip)"* is the whole of US-22.4, and
 * this is the one value that makes it true: `BookingDraft` starts every booking with [current], and
 * SCR-PA-009's payment chip is drawing the draft. Change it in Settings and the **next** booking
 * opens on the new rail; the one already on screen keeps whatever the passenger chose for it,
 * because a preference is not a command.
 *
 * **It is device-local on purpose, and only half of that is a choice.** `iam.users` has a column
 * for it and [SettingsRepository.saveDefaultPaymentMethod] writes it whenever the chosen rail is
 * expressible — but `DefaultPaymentMethod` is still `[cash, lankaqr, onepay]`, so the rail AL-57
 * introduced (`wallet`) has no value to be stored as. Holding the answer here is what lets the app
 * honour a choice the contract cannot yet carry; see [PaymentRails.storedValueOf].
 *
 * The same shape as `CallChoice`, and for the same reason: a preference that outlives every ride
 * belongs in the preference file, not in a view model that dies with its screen.
 */
internal class PaymentPreference(private val preferences: AppPreferences) {

    /** What the next booking starts with. Cash until the passenger says otherwise. */
    val current: PaymentMethod
        get() = preferences.defaultPaymentMethod
            ?.let(PaymentRails::fromWire)
            // A wire value this build does not know is a downgrade from a newer one, or a rail
            // that has since been retired. Either way it is not something to pre-select.
            ?.takeIf { it in PaymentRails.PREFERABLE }
            ?: PaymentMethod.CASH

    /** SCR-PA-027 chose a rail. */
    fun remember(method: PaymentMethod) {
        preferences.defaultPaymentMethod = method.wire
    }

    /**
     * Takes the account's stored default, on a handset that has none of its own.
     *
     * US-22.6 — the preference is *"tied to the passenger account and restored on the device"* — so
     * the profile wins on a fresh install and loses afterwards: a passenger who changed the rail on
     * **this** device last week should not have it silently reverted by a profile read.
     */
    fun adopt(stored: DefaultPaymentMethod?) {
        if (preferences.defaultPaymentMethod == null) remember(PaymentRails.fromStored(stored))
    }
}
