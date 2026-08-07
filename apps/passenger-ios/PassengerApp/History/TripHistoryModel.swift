import Foundation
import MageRideShared

/// SCR-PI-022's three tabs (US-8.7, US-20.11).
enum HistoryTab: String, CaseIterable, Identifiable {
    case past
    case scheduled
    case packages

    var id: String { rawValue }

    var titleKey: String {
        switch self {
        case .past: return "history_tab_past"
        case .scheduled: return "history_tab_scheduled"
        case .packages: return "history_tab_packages"
        }
    }

    /// What the tab says when it has nothing in it — which kind of nothing matters (US-7.14's rule,
    /// applied to a list rather than to a map).
    var emptyKey: String {
        switch self {
        case .past: return "history_empty_past"
        case .scheduled: return "history_empty_scheduled"
        case .packages: return "history_empty_packages"
        }
    }
}

/// A driver, resolved and ready to be called.
///
/// Carries the **ride** as well as the number because SCR-PI-015a's *"Free call"* opens SCR-PI-028,
/// which is `PassengerRoute.voipCall(rideId:)` — a chooser opened from a list has to say which ride it
/// was opened for. `apps/passenger-android`'s `CallTarget` has no ride id and its history screen
/// therefore passes an empty `onFreeCall`; recorded in the C099 handoff (Δ C099).
struct CallTarget: Identifiable, Equatable {
    let rideId: String
    let driverName: String?
    let phone: String

    var id: String { rideId }
}

/// SCR-PI-022's state.
struct TripHistoryState {

    var tab: HistoryTab = .past

    /// Terminal rides that are not parcels.
    var rides: [RideHistoryRow] = []

    /// The parcels, split out of the same read — see ``RideHistoryRow/isPackage``.
    var packages: [RideHistoryRow] = []

    var scheduled: [ScheduledRideRow] = []

    var isLoading = true

    /// The ride whose **real** number has been fetched, ready for SCR-PI-015a. `nil` until a Call is
    /// tapped — see ``TripHistoryModel/call(_:)`` for why it is a second read.
    var callTarget: CallTarget?

    var errorKey: String?

    /// What the visible tab shows, so the screen has one list to draw.
    var visibleRides: [RideHistoryRow] {
        tab == .packages ? packages : rides
    }

    /// Whether the tab is genuinely empty rather than still loading — the illustration state.
    var isEmpty: Bool {
        guard !isLoading else { return false }
        return tab == .scheduled ? scheduled.isEmpty : visibleRides.isEmpty
    }
}

/// SCR-PI-022 — Past / Scheduled / Packages.
///
/// **A completed trip row shows the driver's number and offers a Call** (US-24.4, AL-36) so a
/// passenger can reach the driver about a lost item. **It is withheld for a ride cancelled before
/// assignment** — there was no driver, and inventing a contact for one would be worse than showing
/// none. That is this component's fence, and it is checked twice on purpose: the card hides the row
/// (what a passenger sees) and ``call(_:)`` refuses it (what a stale list or a mis-wired callback
/// cannot get around).
///
/// **The number on the card and the number that gets dialled are not the same value**, and that is a
/// contract gap rather than a design choice. `RideHistoryRow.driver.mobileMasked` is a `PhoneMasked`
/// — `+9477*****67` — and `:shared`'s own KDoc says *"never parse this back into a dialable
/// number"*. AL-48 made the clear number `RideDetail.counterpartyPhone`, which only
/// `GET /v1/rides/{rideId}` carries. So the card renders the masked form and **Call** costs one read.
/// `ride.yaml`'s own note still claims the row exists *"so the post-trip Call action can be rendered
/// without a second round-trip"*, which was true before AL-48 and is not now.
@MainActor
final class TripHistoryModel: ObservableObject {

    @Published private(set) var state = TripHistoryState()

    private let history: HistoryRepository
    private let sessions: PassengerSessions

    init(history: HistoryRepository, sessions: PassengerSessions) {
        self.history = history
        self.sessions = sessions
    }

    /// Reads the history. Also what `.refreshable` calls — the cell's own `Δ iOS` clause.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            let rows = try await history.rides()
            state.rides = rows.filter { !$0.isPackage }
            state.packages = rows.filter(\.isPackage)
            state.isLoading = false
        } catch is CancellationError {
            return
        } catch {
            state.isLoading = false
            state.errorKey = RideErrors.messageKey(for: error)
        }

        if state.tab == .scheduled { await loadScheduled() }
    }

    /// A tab was chosen.
    func select(_ tab: HistoryTab) {
        state.tab = tab
        state.errorKey = nil
    }

    /// The **Scheduled** tab's own read, which answers empty — see
    /// ``ApiHistoryRepository/scheduled(userId:)``.
    ///
    /// A failure is swallowed rather than shown: the tab has one outcome today either way, and an
    /// error message on a list that cannot exist yet would be reporting a gap as a fault.
    func loadScheduled() async {
        guard let userId = sessions.userId else { return }
        guard let rows = try? await history.scheduled(userId: userId) else { return }
        state.scheduled = rows
    }

    /// A Call was tapped — fetch the ride for its clear number, then open SCR-PI-015a.
    ///
    /// A **cancelled-before-assignment** ride has no `counterpartyPhone` and no driver, so this
    /// answers nothing. That is AL-48's own rule seen from the server's side, and a `nil` number is
    /// not an error to apologise for: there is nobody to call.
    func call(_ row: RideHistoryRow) async {
        guard row.hasReachableDriver else { return }
        do {
            let ride = try await history.ride(rideId: row.rideId)
            guard let phone = ride.counterpartyPhone else { return }
            state.callTarget = CallTarget(rideId: row.rideId, driverName: ride.driver?.name, phone: phone)
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = RideErrors.messageKey(for: error)
        }
    }

    /// The chooser has been dismissed.
    func onCallConsumed() {
        state.callTarget = nil
    }

    func clearError() {
        state.errorKey = nil
    }
}
