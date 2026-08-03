package lk.mageride.driver.support

import androidx.annotation.StringRes
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.StatusTone
import lk.mageride.shared.data.models.support.TicketEventKind
import lk.mageride.shared.data.models.support.TicketStatus

/**
 * What SCR-DA-033 calls the values support-svc sends back.
 *
 * **`category` is a free-text server key, so it cannot be an exhaustive table.** `support.tickets
 * .category` carries no CHECK and the FAQ surface publishes the list — *"an enum here would have to
 * be revised every time Support adds a topic"* (`SupportModels`). Two keys are the app's own
 * (`SupportCategories`) and have proper trilingual copy; anything else is rendered from the key
 * itself rather than collapsed into *"Support request"*, because a driver looking at their own
 * ticket list needs to tell two of them apart.
 */
internal object SupportLabels {

    /** The resource for a category this app knows, or `null` for one the server added. */
    @StringRes
    fun category(key: String): Int? = when (key) {
        SupportCategories.DAILY_FEE_REFUND -> R.string.support_category_refund
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
     * The tone the status chip wears — `Open` amber, `Being looked at` blue, `Resolved` green.
     *
     * One table for the list row and the thread header alike: the same ticket wearing two colours
     * on two surfaces is the kind of thing a driver reads as two different tickets.
     */
    fun tone(status: TicketStatus): StatusTone = when (status) {
        TicketStatus.OPEN -> StatusTone.PENDING
        TicketStatus.IN_PROGRESS -> StatusTone.INFO
        TicketStatus.RESOLVED -> StatusTone.DONE
    }

    /**
     * What one thread entry is (Δ C053's `TicketEvent`).
     *
     * `assigned` is in the enum and is **never returned to a user** — who is handling a complaint is
     * not theirs — so it has no copy and the thread skips it rather than printing an empty row.
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
 * A category as a driver reads it.
 *
 * A key this build does not know becomes `daily_fee_refund` → *"Daily fee refund"*: the underscores
 * go and the first letter is capitalised. Not a translation, and it is not pretending to be one —
 * it is the server's own topic name made legible, which is better than a row that says nothing.
 */
@Composable
internal fun categoryLabel(key: String): String = SupportLabels.category(key)?.let { stringResource(it) }
    ?: key.replace('_', ' ').replaceFirstChar(Char::uppercaseChar)
