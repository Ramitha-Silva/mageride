import Foundation
import MageRideShared

/// One row of SCR-DI-017.
///
/// - Parameters:
///   - ride: The board entry.
///   - verdict: Whether **Post intent** is live, and why not when it is not.
///   - isExpired: The T-30 window has passed. The card fades and then leaves the list; see
///     ``JobBoardModel/expiryFadeSeconds``.
///   - isPosting: This row's intent call is in flight.
struct JobBoardRow: Identifiable {

    let ride: ScheduledRide
    let verdict: JobBoardVerdict
    var isExpired: Bool
    var isPosting = false

    /// The `dispatch.scheduled_rides` id — the row's identity and what the intent call takes.
    var id: String { ride.scheduledRideId }

    /// Whether the card shows the *"Intent posted ✓"* pill rather than the **Post intent** link.
    var isPosted: Bool {
        (verdict as? JobBoardVerdictRejected)?.reason == JobBoardRejection.alreadyPosted
    }

    /// Whether the tap is live.
    var canPost: Bool { verdict.isAllowed && !isPosting }
}

/// SCR-DI-017's state.
///
/// - Parameters:
///   - isLoading: The first pass is in flight — the wireframe's shimmer.
///   - isGated: **US-6A.8.** Level 1 has no Job Board; the screen shows *"Reach Level 2"* and no
///     list. `nil` until the level read answers, so the shimmer holds rather than the gate flashing.
///   - minimumLevel: The level the board opens at, from the server's own config when it sent one
///     (US-14.12).
///   - rows: The board, soonest pickup first.
///   - errorKey: Resolved copy for the last failure.
struct JobBoardState {

    var isLoading = true
    var isGated: Bool?
    var minimumLevel = 0
    var rows: [JobBoardRow] = []
    var errorKey: String?

    /// The board could not be read **at all**, because the level read did not answer.
    ///
    /// Distinct from both the gate and the empty board on purpose: the gate is a fact about the
    /// driver and the empty board is a fact about the city, and neither is true when reputation is
    /// down. Showing *"Reach Level 2"* to a Level-3 driver whose level read timed out would be the
    /// one failure US-6A.8 must never produce.
    var isUnavailable: Bool { !isLoading && isGated == nil }

    /// The wireframe's *"No jobs within 30 km"* — an answered, ungated, empty board, nothing wrong.
    var isEmpty: Bool { !isLoading && isGated == false && rows.isEmpty && errorKey == nil }
}

/// **SCR-DI-017 · the Job Board** (US-6A.5, US-6A.8, D-06, D5' §3.7).
///
/// ### The board is post-intent only
///
/// There is no accept here and there is no route to make one with: dispatch-svc's board group has
/// `GET /job-board` and `POST /job-board/{id}/intent` and nothing else. At **T-30 min** the booking
/// is materialised into a ride and offered to the closest intent-poster, breaking ties on the higher
/// Level, and that offer is accepted on SCR-DI-014 through ride-svc like every other one. An accept
/// on the board would reserve a driver half an hour early and would have to be unwound for anyone
/// who was mid-ride when the window came.
///
/// ### What "ranked by Driver Level" is, and is not
///
/// D5' §3.7 ranks **drivers** at T-30 — *"the closest intent-submitting driver by Level"* — not the
/// rows on one driver's board. A device knows neither the other bidders nor their levels, which is
/// why `:shared`'s `JobBoard` deliberately carries no ranking function. This list is ordered by
/// **pickup time, soonest first**, which is the order the wireframe prints and the order
/// `GET /v1/rides/scheduled/{driverId}` already documents for the sibling list.
///
/// ### Intent posted, and where that fact lives
///
/// `ScheduledRide` carries `intentCount` — how many drivers have bid — and nothing that says whether
/// **this** driver is one of them, so the *"Intent posted ✓"* pill is backed by a set held here. It
/// does not survive the process, and a repeat tap after a restart is a harmless replay: the server
/// treats a second intent from the same driver as idempotent. dispatch-svc already computes the fact
/// (`ScheduledRideResponse.HasIntent`) and `dispatch.yaml` does not declare it — the C072 handoff's
/// spec gap 3, carried forward.
@MainActor
final class JobBoardModel: ObservableObject {

    @Published private(set) var state = JobBoardState()

    private let identity: DriverIdentity
    private let jobs: JobsRepository
    private let location: DriverLocationSource
    private let board: JobBoard
    private let now: () -> Date
    private let positionWait: TimeInterval

    private var posted: Set<String> = []
    private var ticker: Task<Void, Never>?

    /// - Parameters:
    ///   - board: `:shared`'s client-side board rules. Injected rather than constructed here so a
    ///     test can hand in one built on an admin-tuned `LevelConfig`. The default is Kotlin's own
    ///     `JobBoard()`, spelled through ``driverLevelRulesFor(level:)`` so that even the fallback
    ///     does not name D5' §4.2's numbers in Swift.
    ///   - now: The clock. Injected because this model's **whole** behaviour is a comparison against
    ///     T-30, and a test that could only wait for wall-clock time would have to sleep for half an
    ///     hour to assert the rule the DoD names.
    ///   - positionWait: How long to wait for the first GNSS fix. Injected separately from ``now``
    ///     because it is a **timeout** rather than a rule — see ``awaitPosition()``.
    init(
        identity: DriverIdentity,
        jobs: JobsRepository,
        location: DriverLocationSource,
        board: JobBoard = JobBoard(levels: IosJobBoardKt.driverLevelRulesFor(level: nil)),
        now: @escaping () -> Date = Date.init,
        positionWait: TimeInterval = JobBoardModel.positionWaitSeconds
    ) {
        self.identity = identity
        self.jobs = jobs
        self.location = location
        self.board = board
        self.now = now
        self.positionWait = positionWait
    }

    deinit {
        ticker?.cancel()
    }

    /// Subscribes to the device's own position and starts the one-second clock.
    ///
    /// Separate from ``refresh()`` because the two have different lifetimes: the reads happen on
    /// every appearance, the subscriptions exactly once per model.
    func start() {
        guard ticker == nil else { return }

        // The handler is empty on purpose: this screen draws no map and no marker, so a fix is not
        // state it renders — it is the anchor `GET /v1/rides/job-board` is read around, and
        // ``awaitPosition()`` reads it off the source when the read needs it. Starting is still
        // required, because the source has no last-known fix to answer with until it has.
        location.start { _ in }
        // `guard let self` inside the loop rather than only `[weak self]` on it, for the reason
        // ``HomeModel/start()`` gives: a weak reference that is merely *checked* each tick leaves the
        // loop running for the life of the process once the model has gone.
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: JobBoardModel.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.reappraise()
            }
        }
    }

    /// Drops both subscriptions. A board that is not on screen must not hold a GNSS one open.
    func stop() {
        ticker?.cancel()
        ticker = nil
        location.stop()
    }

    /// Re-reads the level gate and, when it opens, the board.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.errorKey = nil

        let standing = await jobs.standing(driverId: driverId)
        let access = standing.hasJobBoardAccess

        guard access == true else {
            // US-6A.8 is a gate, not an error. A Level-1 driver still takes immediate Mode C rides;
            // what they lose is this screen, and the copy says which level opens it. The gated path
            // never spends the board read either: a `GET /v1/rides/job-board` a driver may not act on
            // is a round trip to draw a list of disabled buttons.
            state.isLoading = false
            state.isGated = access.map { !$0 }
            state.minimumLevel = standing.jobBoardMinLevel
            state.rows = []
            return
        }

        // The board is anchored on where the driver is. `DriverLocationSource` answers nothing until
        // GNSS does, so the read waits for the first fix rather than sending a (0, 0) the server
        // would answer honestly and uselessly. It is bounded, because a revoked permission or a
        // switched-off receiver never emits at all — and a board that spun for ever would look like
        // an outage rather than like a setting.
        guard let here = await awaitPosition() else {
            state.isLoading = false
            state.isGated = false
            state.rows = []
            state.errorKey = "job_board_no_position"
            return
        }

        // Answered here, before the read, and not inside it. `hasJobBoardAccess` settled the gate
        // above; a board read that then fails is an error to retry, not an unanswered gate — and
        // leaving `isGated` nil makes ``JobBoardState/isUnavailable`` true, so the screen draws
        // "no board here" over copy the driver could have acted on.
        state.isGated = false
        state.minimumLevel = standing.jobBoardMinLevel

        do {
            let rides = try await jobs.board(
                lat: here.lat,
                lng: here.lng,
                radiusMetres: Int(JobBoard.companion.CATCHMENT_METRES)
            )
            state.rows = rows(from: rides, standing: standing)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// **Post intent** — the only action on this board (US-6A.5).
    ///
    /// The verdict is re-checked against the clock at the moment of the tap, not at the moment the
    /// row was drawn: a card that sat on screen through T-30 must not send an intent no dispatch
    /// round will read. `canPost` is that check, and ``JobBoard/canPostIntent(driver:ride:now:postedRideIds:)``
    /// is where it is written.
    func postIntent(_ row: JobBoardRow) async {
        guard row.canPost, let driverId = identity.driverId else { return }
        mark(row.id, isPosting: true)

        do {
            try await jobs.postIntent(scheduledRideId: row.id)
            posted.insert(row.id)
            let standing = await jobs.standing(driverId: driverId)
            state.rows = rows(from: state.rows.map(\.ride), standing: standing)
        } catch {
            mark(row.id, isPosting: false)
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// The device's own position, waiting up to ``positionWait`` for the first fix.
    ///
    /// A poll rather than a suspension resumed by the fix handler: CoreLocation delivers through a
    /// delegate, ``DriverLocationSource`` is the callback seam over it, and a continuation resumed
    /// from that callback has to defend against the *last known* fix arriving synchronously inside
    /// `start(onFix:)` — a double resume, which traps. In practice this returns on its first look:
    /// `CLLocationManager.location` is already populated on any handset that has had a fix today.
    ///
    /// **The budget is counted in polls, not read off ``now``.** That clock is the *rule's* — the
    /// T-30 comparison — and a test that freezes it to assert the rule would otherwise freeze this
    /// loop with it and spin for the life of the process. A timeout is elapsed time, and elapsed
    /// time here is however many two-hundred-millisecond sleeps have actually happened.
    private func awaitPosition() async -> Fix? {
        if let fix = location.fix { return fix }

        var remaining = positionWait
        while remaining > 0 {
            try? await Task.sleep(nanoseconds: JobBoardModel.positionPollNanoseconds)
            if Task.isCancelled { return nil }
            if let fix = location.fix { return fix }
            remaining -= JobBoardModel.positionPollSeconds
        }
        return nil
    }

    /// The board as rows, soonest first and **already past the fade filter**.
    ///
    /// A read that arrives holding rides whose window shut ten minutes ago must not print them and
    /// then take them away a second later; the fade is for a row that expires while the driver is
    /// looking at it, not for one that was dead when it landed.
    private func rows(from rides: [ScheduledRide], standing: JobStanding) -> [JobBoardRow] {
        guard let driver = standing.detailed else { return [] }
        let at = now()
        let timestamp = IosInstantKt.timestampFromEpochMillis(millis: Int64(at.timeIntervalSince1970 * 1000))

        return rides
            .sorted { pickupMillis($0) < pickupMillis($1) }
            .map { ride in
                JobBoardRow(
                    ride: ride,
                    verdict: board.canPostIntent(
                        driver: driver,
                        ride: ride,
                        now: timestamp,
                        postedRideIds: posted
                    ),
                    isExpired: at >= goesLiveAt(ride)
                )
            }
            .filter { isVisible($0, at: at) }
    }

    /// Re-evaluates every row against the clock, once a second.
    ///
    /// This is what makes the wireframe's *"expired job → overlay fade"* and the DoD's *"rows
    /// disappear once their T-30 window passes"* one behaviour rather than two: a row goes
    /// ``JobBoardRow/isExpired`` the second the window closes, which is what the card animates on,
    /// and leaves the list ``expiryFadeSeconds`` later, once that animation has been seen.
    private func reappraise() {
        guard !state.rows.isEmpty else { return }
        let at = now()

        state.rows = state.rows
            .filter { isVisible($0, at: at) }
            .map { row in
                var updated = row
                updated.isExpired = at >= goesLiveAt(row.ride)
                return updated
            }
    }

    /// Whether [at] is still inside the row's own life — its window, plus ``expiryFadeSeconds``.
    private func isVisible(_ row: JobBoardRow, at: Date) -> Bool {
        at < goesLiveAt(row.ride).addingTimeInterval(JobBoardModel.expiryFadeSeconds)
    }

    /// When [ride] leaves the board and dispatch starts offering it (D5' §3.7).
    ///
    /// Through `:shared` rather than `pickupTime - 30 min` here: `JobBoard.GO_LIVE_LEAD` is the one
    /// place the thirty minutes is written, and SCR-DI-018's *"reminder sent"* reads the same
    /// function. `timeToGoLive` is deliberately not used — it answers a `Duration`, which the export
    /// flattens into an opaque `Long`. See `IosJobBoard.kt`.
    private func goesLiveAt(_ ride: ScheduledRide) -> Date {
        Date(timeIntervalSince1970: TimeInterval(IosJobBoardKt.jobBoardGoesLiveAtMillis(ride: ride)) / 1000)
    }

    /// The pickup instant as epoch millis — what the list is ordered on.
    ///
    /// Milliseconds rather than comparing two `Timestamp`s: `Instant` is `Comparable` in Kotlin and
    /// reaches Swift as a `compareTo` whose spelling is the compiler's, not this codebase's.
    private func pickupMillis(_ ride: ScheduledRide) -> Int64 {
        IosInstantKt.timestampEpochMillis(instant: ride.pickupTime)
    }

    private func mark(_ id: String, isPosting: Bool) {
        state.rows = state.rows.map { row in
            guard row.id == id else { return row }
            var updated = row
            updated.isPosting = isPosting
            return updated
        }
    }

    /// How often the board re-reads the clock. The T-30 edge is a second, not a refresh.
    private static let tickNanoseconds: UInt64 = 1_000_000_000

    /// How long an expired card stays up before it is dropped.
    ///
    /// D2' §SCR-DI-017's animation is *"card expire fade"* — a row that vanished the instant it
    /// expired would look like a tap that lost the driver a job. Long enough to read, short enough
    /// that the board is never a list of rides nobody can bid on. **The only local number on this
    /// screen, and it is the animation rather than the rule.**
    static let expiryFadeSeconds: TimeInterval = 3

    /// How long the board waits for the device's first fix before saying it has none.
    ///
    /// CoreLocation answers its *last known* position immediately when it has one, so this only
    /// bites on a handset that has never had a fix, has the receiver switched off, or has had the
    /// permission revoked since SCR-DI-007 — all of which are settings, not outages.
    static let positionWaitSeconds: TimeInterval = 8

    /// A fifth of a second between looks. Short enough that the wait is invisible when a fix is one
    /// tick away, long enough that eight seconds of it is forty wake-ups and not eight thousand.
    private static let positionPollSeconds: TimeInterval = 0.2
    private static let positionPollNanoseconds: UInt64 = 200_000_000
}
