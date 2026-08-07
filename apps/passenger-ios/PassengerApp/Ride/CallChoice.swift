import Foundation
import MageRideShared

/// SCR-PI-015a's two options, and the memory behind them.
///
/// **There is no masked option, and that is AL-48.** The masking requirement was *withdrawn* — no
/// proxy-DID / CPaaS product exists with +94 numbers — so *"Normal call"* is a **direct cellular dial
/// of the driver's real number**, revealed post-accept in `RideDetail.counterpartyPhone`. Every word
/// of "masked", "private number" and "we hide your number" is gone from this app's call copy, and the
/// only privacy claim that survives is a true one: a **VoIP** call genuinely does not reveal a number,
/// because no number is involved.
///
/// A class over ``AppPreferences`` rather than two reads at the call site, for the reason the Android
/// twin is one: what *"remembers last choice"* and *"shown once"* mean is a rule, and a rule split
/// across two screens is a rule that will be half-applied when C102's SCR-PI-028 offers the same
/// chooser after a failed VoIP call.
final class CallChoice {

    private let preferences: AppPreferences

    init(preferences: AppPreferences) {
        self.preferences = preferences
    }

    /// What to pre-select, or `nil` on the very first call.
    ///
    /// A stored value this build does not recognise answers `nil` — no pre-selection is better than
    /// highlighting a row that means something else now.
    var remembered: CallType? {
        guard let wire = preferences.lastCallType else { return nil }
        return CallChoice.all.first { $0.wire == wire }
    }

    /// Whether US-26.5's *"your number is visible to the other party"* notice is still owed.
    ///
    /// **Only for a direct dial.** A free VoIP call reveals nothing, so disclosing number visibility
    /// before one would be a warning about something that is not happening — and a disclosure shown
    /// where it does not apply is how people learn to dismiss disclosures.
    func owesNumberNotice(for type: CallType) -> Bool {
        type == CallType.directDial && !preferences.callNumberNoticeShown
    }

    /// Records the choice, and the fact that the notice has now been seen.
    func remember(_ type: CallType) {
        preferences.lastCallType = type.wire
        if type == CallType.directDial {
            preferences.callNumberNoticeShown = true
        }
    }

    /// The two call types, listed once.
    ///
    /// A Kotlin enum reaches Swift as a class with static members and **no `CaseIterable`**, so
    /// `CallType.entries` has no Swift counterpart. Both values are named here rather than at three
    /// call sites; the sheet draws them in this order, which is the cell's (free first).
    static let all: [CallType] = [CallType.freeVoip, CallType.directDial]
}
