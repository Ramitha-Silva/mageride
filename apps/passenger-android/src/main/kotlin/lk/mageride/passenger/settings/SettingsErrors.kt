package lk.mageride.passenger.settings

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 7's code table — the address book, the profile, its two preferences and the SOS list.
 *
 * **D-26 again: never a `ProblemDetails` title or detail.** Every arm below is a code `iam.yaml`
 * declares on one of the eleven operations this cluster reaches. C079's `BookingErrors`, C080's
 * `RideErrors` and C082's `ModeBErrors` are the other three tables and stay separate for the reason
 * each of theirs gives: one `when` over the whole platform is a function nobody can check against a
 * contract.
 *
 * `GET /v1/geo/reverse` is deliberately absent. It is the one call in this cluster whose failure is
 * not shown — `AddressBook.describe` answers `null` and the sheet opens with empty lines, because a
 * geocoder that cannot name a coordinate has not stopped the passenger saving it.
 */
internal object SettingsErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    /**
     * The same, for `DELETE /v1/users/me` — where a `409` means something else entirely.
     *
     * **One code, two meanings, and they are not interchangeable.** A conflict on a saved address
     * is *"you already have a Home"*; a conflict on the erasure request is *"one is already open"*
     * (E-06). A single arm would have to pick one of those sentences and be wrong on the other
     * screen, so the delete path names its own rather than the table guessing from a code.
     */
    @StringRes
    fun deletionMessageFor(cause: Throwable): Int = if (cause is MageRideError && cause.code == ErrorCode.CONFLICT) {
        R.string.settings_delete_already_requested
    } else {
        messageFor(cause)
    }

    @StringRes
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // `POST /v1/me/saved-addresses` — the C003 partial unique indexes refuse a **second** Home
        // or Work. Reachable only from a stale list: the screen edits the row it already has.
        ErrorCode.CONFLICT -> R.string.error_address_shortcut_taken

        // The address or the contact is gone — deleted on a second handset, most often.
        ErrorCode.NOT_FOUND -> R.string.error_address_not_found

        // `POST` / `PUT /v1/me/emergency-contacts` on a number iam-svc cannot parse as E.164.
        ErrorCode.INVALID_PHONE -> R.string.error_phone_invalid

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        else -> R.string.error_generic
    }
}
