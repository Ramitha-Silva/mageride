package lk.mageride.passenger.ride

import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.CallType

/**
 * SCR-PA-015a's two options, and the memory behind them.
 *
 * **There is no masked option, and that is AL-48.** The masking requirement was *withdrawn* — no
 * proxy-DID/CPaaS product exists with +94 numbers — so "Normal call" is a **direct cellular dial of
 * the driver's real number**, revealed post-accept in `RideDetail.counterpartyPhone`. Every word of
 * "masked", "private number" and "we hide your number" is gone from this app's call copy, and the
 * only privacy claim that survives is a true one: a **VoIP** call genuinely does not reveal a
 * number, because no number is involved.
 *
 * @property preferences Where the last choice lives. The wireframe says the sheet *"remembers last
 *   choice"*, and it has to outlive the ride — a passenger who always calls normally should not be
 *   asked again on their next trip.
 */
internal class CallChoice(private val preferences: AppPreferences) {

    /** What to pre-select, or `null` on the very first call. */
    val remembered: CallType?
        get() = preferences.lastCallType?.let { wire -> CallType.entries.firstOrNull { it.wire == wire } }

    /**
     * Whether US-26.5's *"your number is visible to the other party"* tooltip is still owed.
     *
     * **Only for a direct dial.** A free VoIP call reveals nothing, so disclosing number visibility
     * before one would be a warning about something that is not happening — and a disclosure shown
     * where it does not apply is how people learn to dismiss disclosures.
     */
    fun owesNumberNotice(type: CallType): Boolean = type == CallType.DIRECT_DIAL && !preferences.callNumberNoticeShown

    /** Records the choice, and the fact that the notice has now been seen. */
    fun remember(type: CallType) {
        preferences.lastCallType = type.wire
        if (type == CallType.DIRECT_DIAL) preferences.callNumberNoticeShown = true
    }
}
