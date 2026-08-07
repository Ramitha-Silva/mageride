import Foundation
import MageRideShared

/// One destination the driver can pick — a geocoder hit, a saved address, or ★ Home.
///
/// - Parameters:
///   - label: What the row reads. Driver-owned text or a place name, never platform copy.
///   - point: Where it is.
///   - isHome: Whether this is the ★ Home shortcut the wireframe pins to the top.
struct DirectionalDestination: Identifiable {

    let label: String
    let point: GeoPoint
    var isHome = false

    var id: String { "\(label)|\(point.lat),\(point.lng)" }
}

/// SCR-DI-013's state.
///
/// - Parameters:
///   - filter: The server's own view of the activation (DT-08).
///   - destination: What the driver has chosen but not yet set.
///   - suggestions: Geocoder hits and saved addresses for the destination field.
///   - maxDurationSec: The configured ceiling **once this app has been told one**. See the type KDoc
///     on ``DirectionalModel`` — it is not readable before the first activation.
///   - query: What is typed in the destination field.
///   - tickAt: The clock the `1:42 left` countdown renders against.
///   - position: The driver's own last fix, for the ➤ bearing and the search bias.
///   - isBusy: A set or a clear is in flight.
///   - errorKey: Resolved copy for the last failure.
struct DirectionalState {

    var filter: DirectionalFilterState?
    var destination: DirectionalDestination?
    var suggestions: [DirectionalDestination] = []
    var maxDurationSec: Int64?
    var query = ""
    var tickAt = Date()
    var position: Fix?
    var isBusy = false
    var errorKey: String?

    /// Whether a filter is live — the wireframe's *"when active"* half.
    var isActive: Bool { filter?.active == true }

    /// Activations left today, in Asia/Colombo (DT-03, D-38).
    var usesRemaining: Int { Int(filter?.usesRemaining ?? 0) }

    /// Whether **Set Direction** is live.
    ///
    /// Three conditions the client can actually evaluate: a destination has been chosen, no filter is
    /// already running, and the day's budget is not spent — the wireframe's *"uses exhausted → Set
    /// disabled"*.
    ///
    /// **Being online is the fourth condition and is the server's to enforce.** `dispatch.yaml` carries
    /// no presence read — `POST /v1/standby/online` answers a `PresenceState` and nothing reads one
    /// back — so a screen reached by deep link or after a process death cannot know. The honest
    /// behaviour is to let the driver tap and to render `403 not-online` as copy, rather than to grey
    /// out a control on a guess. The same spec gap the C070 handoff records.
    var canSet: Bool { !isActive && usesRemaining > 0 && destination != nil }

    /// What is left of the activation, in seconds, floored at zero (DT-04).
    var timeRemainingSeconds: Int64 {
        guard let filter else { return 0 }
        guard let expiresAt = filter.expiresAt else { return max(Int64(filter.timeRemainingSec), 0) }
        let deadline = TimeInterval(IosInstantKt.timestampEpochMillis(instant: expiresAt)) / 1000
        return max(Int64(deadline - tickAt.timeIntervalSince1970), 0)
    }

    /// Whether the ten-minute warning is due (DT-08, US-10.14).
    ///
    /// The same threshold notify-svc's `directional.expiring` push uses, so the banner and the
    /// notification agree about when "expiring soon" starts.
    var isExpiringSoon: Bool {
        isActive && timeRemainingSeconds <= DirectionalState.preExpiryReminderSeconds && timeRemainingSeconds > 0
    }

    /// MAP-06's ➤ rotation — the bearing from the driver to where they are heading.
    var headingDeg: Double? {
        guard let from = position?.point else { return nil }
        guard let to = destination?.point ?? filter?.destination else { return nil }
        return GeoDistanceKt.bearingDegrees(from: from, to: to)
    }

    /// `DirectionalStanding.PRE_EXPIRY_REMINDER` — ten minutes (DT-08, US-10.14).
    static let preExpiryReminderSeconds: Int64 = 600
}

/// **SCR-DI-013 · Directional Travel** (US-6A.17–23, DT-01..DT-08).
///
/// **A use is spent on activation and turning the filter off does not give it back** (DT-03, US-6A.19).
/// `DELETE /v1/standby/directional` answers with the same `usesRemaining` it had before, and this model
/// shows exactly that number: without the rule a driver could flick the filter on for the one offer
/// they wanted and off again, all day, on two uses.
///
/// **Directional Travel is not in the ride state machine** (ADD Appendix B.2 invariant 7). It is a
/// dispatch-svc candidate filter applied before an offer exists, which is why nothing here touches
/// ride-svc and why the only visible effect on SCR-DI-014 is a badge.
///
/// ### The one number this screen cannot read
///
/// The wireframe prints *"Uses left · 1 of 2"* and *"Max · 2h"*. `dispatch.yaml` carries
/// `DirectionalConfig` on a **`PUT /v1/admin/dispatch/directional-config`** and on no read at all, so
/// an app can learn `maxDurationSec` only from a `DirectionalFilterCreated` it has just received, and
/// `maxUsesPerDay` never. Baking D5' §12.1's defaults would be the exact failure
/// `DirectionalPredicate`'s KDoc warns about — *"a build that hardcoded them would silently disagree
/// with dispatch the first time an operator tuned one"* — so this screen renders the remaining count
/// the server sent and shows the ceiling only once it has been told one. The same spec gap the C070
/// handoff records.
@MainActor
final class DirectionalModel: ObservableObject {

    @Published private(set) var state = DirectionalState()

    private let standby: StandbyRepository
    private let location: DriverLocationSource

    private var ticker: Task<Void, Never>?
    private var search: Task<Void, Never>?

    init(standby: StandbyRepository, location: DriverLocationSource) {
        self.standby = standby
        self.location = location
    }

    deinit {
        ticker?.cancel()
        search?.cancel()
    }

    func start() {
        guard ticker == nil else { return }
        location.start { [weak self] fix in
            self?.state.position = fix
        }
        // `guard let self` inside the loop, for the reason ``HomeModel/start()`` gives: a loop that
        // only *checks* a weak reference outlives the model it was ticking for.
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: DirectionalModel.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.state.tickAt = Date()
            }
        }
    }

    func stop() {
        ticker?.cancel()
        ticker = nil
        search?.cancel()
        search = nil
        location.stop()
    }

    /// Re-reads the filter and the ★ Home shortcut.
    func refresh() async {
        do {
            state.filter = try await standby.directional()
            state.suggestions = await shortcuts()
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// The destination field. Geocoded through query-svc, biased toward where the driver is.
    ///
    /// The search is debounced by cancellation rather than by a timer: a driver types at a junction and
    /// every keystroke would otherwise be a round trip that outlives the one before it and lands out of
    /// order.
    func typed(_ text: String) {
        state.query = text
        search?.cancel()
        guard text.count >= DirectionalModel.minimumQuery else { return }

        search = Task { [weak self] in
            guard let self else { return }
            try? await Task.sleep(nanoseconds: DirectionalModel.debounceNanoseconds)
            guard !Task.isCancelled else { return }

            let hits = (try? await self.standby.searchPlaces(query: text, near: self.state.position)) ?? []
            guard !Task.isCancelled else { return }
            self.state.suggestions = await self.shortcuts()
                + hits.map { DirectionalDestination(label: $0.displayName, point: $0.point) }
        }
    }

    /// Picks a destination — a search hit or a saved address.
    func choose(_ destination: DirectionalDestination) {
        state.destination = destination
        state.query = destination.label
    }

    /// **Set Direction** — `POST /v1/standby/directional` (DT-01). Consumes one of the day's uses.
    ///
    /// `403 not-online` off standby and `409 directional-limit-reached` when the budget is gone; both
    /// become copy rather than a thrown screen.
    func setDirection() async {
        guard let destination = state.destination else { return }
        state.isBusy = true
        state.errorKey = nil
        do {
            let created = try await standby.setDirectional(
                destination: destination.point,
                // The driver's own shorthand, not platform copy — `SetDirectionalFilterRequest` caps
                // it at 60 characters and the server echoes it back on the card.
                label: String(destination.label.prefix(DirectionalModel.labelMaximum))
            )
            let remaining = TimeInterval(IosInstantKt.timestampEpochMillis(instant: created.expiresAt)) / 1000
                - Date().timeIntervalSince1970

            state.maxDurationSec = Int64(created.maxDurationSec)
            state.filter = DirectionalFilterState(
                active: true,
                destination: destination.point,
                label: destination.label,
                expiresAt: created.expiresAt,
                timeRemainingSec: Int32(max(remaining, 0)),
                usesRemaining: created.usesRemaining
            )
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// **Turn Off** — and the use is still spent (US-6A.19).
    func turnOff() async {
        state.isBusy = true
        state.errorKey = nil
        do {
            let cleared = try await standby.clearDirectional()
            state.destination = nil
            state.query = ""
            state.filter = DirectionalFilterState(
                active: cleared.active,
                destination: nil,
                label: nil,
                expiresAt: nil,
                timeRemainingSec: 0,
                usesRemaining: cleared.usesRemaining
            )
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// ★ Home and ★ Work, from the driver's own saved addresses.
    ///
    /// They come first and stay first: the wireframe pins ★ Home to the top because a driver setting a
    /// direction at the end of a shift is almost always heading there, and typing an address one-handed
    /// at a junction is the thing this shortcut exists to avoid.
    private func shortcuts() async -> [DirectionalDestination] {
        await standby.savedShortcuts().map {
            DirectionalDestination(
                label: $0.label,
                point: GeoPoint(lat: $0.lat, lng: $0.lng),
                isHome: $0.isHome?.boolValue == true
            )
        }
    }

    /// One second — fine enough for the `1:42 left` countdown on the active card.
    private static let tickNanoseconds: UInt64 = 1_000_000_000

    /// Three characters — below that a geocoder answers with the whole country.
    private static let minimumQuery = 3

    /// A third of a second: long enough that a fast typist sends one request, short enough that a
    /// driver who has stopped typing is not left looking at an empty list.
    private static let debounceNanoseconds: UInt64 = 300_000_000

    /// `SetDirectionalFilterRequest.label` — *"at most 60 characters"*.
    private static let labelMaximum = 60
}
