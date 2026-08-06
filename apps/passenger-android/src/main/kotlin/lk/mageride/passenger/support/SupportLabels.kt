package lk.mageride.passenger.support

import androidx.annotation.StringRes
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import lk.mageride.passenger.R
import lk.mageride.shared.data.models.support.TicketEventKind
import lk.mageride.shared.data.models.support.TicketStatus

/**
 * What SCR-PA-030 calls the values support-svc sends back.
 *
 * **`category` is a free-text server key, so it cannot be an exhaustive table.**
 * `support.tickets.category` carries no CHECK and the FAQ surface publishes the list — *"an enum
 * here would have to be revised every time Support adds a topic"* (`SupportModels`). The one key
 * this app raises is its own ([SupportCategories.GENERAL]) and has proper trilingual copy; anything
 * else — a ticket fare-svc opened for an AL-47 driver-QR dispute, say — is rendered from the key
 * itself rather than collapsed into *"Support request"*, because a passenger looking at their own
 * ticket list needs to tell two of them apart.
 */
internal object SupportLabels {

    /** The resource for a category this app knows, or `null` for one another service raised. */
    @StringRes
    fun category(key: String): Int? = when (key) {
        SupportCategories.GENERAL -> R.string.support_category_general
        else -> null
    }

    /** `TicketStatus` as the wireframe's chip. */
    @StringRes
    fun status(status: TicketStatus): Int = when (status) {
        TicketStatus.OPEN -> R.string.support_status_open
        TicketStatus.IN_PROGRESS -> R.string.support_status_in_progress
        TicketStatus.RESOLVED -> R.string.support_status_resolved
    }

    /**
     * What one thread entry is (Δ C053's `TicketEvent`).
     *
     * `assigned` is in the enum and is **never returned to a user** — who inside MageRide is
     * handling a complaint is not the complainant's business — so it has no copy here and the thread
     * skips it rather than printing an empty row.
     */
    @StringRes
    fun event(kind: TicketEventKind): Int? = when (kind) {
        TicketEventKind.OPENED -> R.string.support_event_opened
        TicketEventKind.RESPONDED -> R.string.support_event_responded
        TicketEventKind.RESOLVED -> R.string.support_event_resolved
        TicketEventKind.REOPENED -> R.string.support_event_reopened
        TicketEventKind.ASSIGNED -> null
    }
}

/**
 * A category as a passenger reads it.
 *
 * A key this build does not know becomes `driver_qr_dispute` → *"Driver qr dispute"*: the
 * underscores go and the first letter is capitalised. Not a translation, and it is not pretending to
 * be one — it is the server's own topic name made legible, which is better than a row that says
 * nothing.
 */
@Composable
internal fun categoryLabel(key: String): String = SupportLabels.category(key)?.let { stringResource(it) }
    ?: key.replace('_', ' ').replaceFirstChar(Char::uppercaseChar)
