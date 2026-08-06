package lk.mageride.passenger.safety

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 8's safety half — the codes `safety.yaml` and `voip.yaml` declare.
 *
 * **D-26 again: never a `ProblemDetails` title or detail.** Every arm below is a code one of the two
 * contracts SCR-PA-028 and SCR-PA-029 reach actually declares; C080's `RideErrors` is the ride and
 * money set and stays separate for the reason its own KDoc gives. `SupportErrors` is the third
 * table in this cluster, over support-svc.
 *
 * **`no-emergency-contact` is a setup failure, and the copy says what to do about it.** It is the
 * one code on this surface a passenger can fix themselves, and the fix is SCR-PA-027b — which is
 * also where SCR-PA-029 sends them from the empty-contacts state, *before* an emergency rather than
 * during one.
 */
internal object SafetyErrors {

    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // AL-13. `POST /v1/sos` refuses outright when the account has nobody on file, so the alert
        // is not raised at all — which is why SCR-PA-029 warns about it while the disc is still
        // armed rather than waiting for the refusal.
        ErrorCode.NO_EMERGENCY_CONTACT -> R.string.error_no_emergency_contact

        // The ride ended between the tap and the request. A share link has nothing left to follow
        // (D-34's window is trip end + 1 h and this one never opened) and a call has nobody to reach.
        ErrorCode.RIDE_TERMINAL -> R.string.error_ride_ended

        ErrorCode.NOT_RIDE_PARTICIPANT, ErrorCode.FORBIDDEN -> R.string.error_not_your_ride

        ErrorCode.NOT_FOUND -> R.string.error_ride_not_found

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        // D-30. The handset could not attest, and `POST /v1/sos` is one of the twenty attested
        // operations — nothing the passenger can act on, so it reads as a plain failure.
        ErrorCode.ATTESTATION_FAILED -> R.string.error_generic

        else -> R.string.error_generic
    }
}
