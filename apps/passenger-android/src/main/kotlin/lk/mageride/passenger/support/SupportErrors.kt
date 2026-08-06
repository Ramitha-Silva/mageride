package lk.mageride.passenger.support

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 8's support half — the codes `support.yaml` declares.
 *
 * Four operations and a small surface: the FAQ pair answers `unauthorized` / `not-found`, the
 * ticket read adds `forbidden`, `POST /v1/support/tickets` adds `validation-failed` (which is what
 * a `screenshotFileId` belonging to somebody else comes back as), and the upload adds
 * `payload-too-large`.
 *
 * Separate from [lk.mageride.passenger.safety.SafetyErrors] because they are separate contracts —
 * one `when` over the whole platform is a table nobody can check against anything.
 */
internal object SupportErrors {

    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // The screenshot upload's own ceiling. The ticket is unaffected — see `SupportViewModel`,
        // which submits without the attachment rather than losing what the passenger wrote.
        ErrorCode.PAYLOAD_TOO_LARGE -> R.string.error_screenshot_too_large

        // A ticket, an article or an attachment that is not there. `getSupportScreenshot` answers
        // `403` for an unknown id on purpose, so a forged link tells its author nothing.
        ErrorCode.NOT_FOUND, ErrorCode.FORBIDDEN -> R.string.error_ticket_not_found

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        else -> R.string.error_generic
    }
}
