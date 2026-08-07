import Foundation
import MageRideShared

/// SCR-DI-019's state.
///
/// - Parameters:
///   - isLoading: The read is in flight.
///   - standing: The level, the points and the counters.
///
/// **There is no error field, and that is Δ C090.** `DriverLevelState` on Android carries one and it
/// is unreachable: `JobsRepository.standing` swallows both failures by design (a dead stats read must
/// not close the Job Board), so nothing it is called from can throw and the banner never draws. What
/// a failed read actually looks like on this screen is already specified — the badge is an em dash
/// and the points line says *"Reading your level"* — which is the honest rendering of "reputation did
/// not answer" and the same three-valued rule US-6A.8 turns on. A banner that could never appear is
/// a control nobody can test.
struct DriverLevelState {

    var isLoading = true
    var standing = JobStanding()

    /// The level itself, `1`–`3`; `nil` until reputation has answered.
    var level: Int? { standing.standing.map { Int($0.level) } }

    /// Points banked toward the next level.
    var points: Int { standing.detailed.map { Int($0.points) } ?? 0 }

    /// What a level costs, from the server's config when it sent one (US-14.12).
    var threshold: Int { Int(standing.rules.levelUpThreshold) }

    /// Whether the driver is already at `DriverLevelRules.MAX_LEVEL` — see ``DriverLevelModel``.
    var isAtTopLevel: Bool { (level ?? 0) >= Int(DriverLevelRules.companion.MAX_LEVEL) }

    /// The next level a driver can reach, or `nil` at the top.
    var nextLevel: Int? {
        guard let level, level + 1 <= Int(DriverLevelRules.companion.MAX_LEVEL) else { return nil }
        return level + 1
    }

    /// How full the bar is, `0`–`1`.
    ///
    /// Full at the top level: the points are still banked and still spent on a crossing (D5' §4.2's
    /// `points -= 500` applies at the cap too), but there is no rung above to progress toward, and a
    /// bar frozen at 20% would read as a driver who had stopped earning.
    var progress: Double {
        guard level != nil else { return 0 }
        if isAtTopLevel { return 1 }
        guard threshold > 0 else { return 0 }
        return min(max(Double(points) / Double(threshold), 0), 1)
    }

    /// US-6A.14's acceptance rate as whole percent, or `nil` when the stats read did not answer.
    var acceptancePercent: Int? {
        standing.acceptanceRate.map { min(max(Int($0 * 100), 0), 100) }
    }

    /// Scheduled-ride no-shows (US-6A.7).
    var noShows: Int? { standing.noShows }
}

/// **SCR-DI-019 · Driver Level & stats** (US-6A.6, US-6A.14).
///
/// Two reads, `GET /v1/drivers/{driverId}/level` and `…/stats`, both best-effort — see
/// ``ApiJobsRepository/standing(driverId:)``.
///
/// ### Levels run 1–3, and the wireframe draws a fourth
///
/// `driver_ios.html` prints *"510 / 500 pts → Level 4"* above a bar at 88%. **D5' §4.2 gives three
/// levels** (`dispatch.driver_levels`, `level = min(level + 1, 3)`) and `:shared`'s
/// `DriverLevelRules.MAX_LEVEL` is 3, so there is no Level 4 to progress toward. The layout is the
/// wireframe's — badge, points line, bar, the two stat cards, the warning — and the *copy* on the
/// points line becomes "this is the top level" once a driver is at 3. Recorded as a wireframe/D5'
/// conflict in the C072 handoff and carried forward unchanged; D5' wins, because a screen promising
/// a rung the dispatcher cannot award is worse than a screen that says the ladder ends here.
///
/// ### The reports counter is not on the wire
///
/// `GET …/stats` answers `acceptanceRate`, `noShows` and `points`. **Nothing app-facing carries the
/// passenger-report count** — `reputation.block_state` and its counters are C033's gRPC and portal
/// surface (C012 models no reputation DTO on purpose) — so the wireframe's *"⚠ 3 reports → level drop
/// + delisting"* is rendered as the **rule**, which is exactly the sentence it prints, and never as a
/// live tally. A screen cannot warn a driver that they are on their second report, and inventing a
/// count would be worse than saying the rule.
@MainActor
final class DriverLevelModel: ObservableObject {

    @Published private(set) var state = DriverLevelState()

    private let identity: DriverIdentity
    private let jobs: JobsRepository

    init(identity: DriverIdentity, jobs: JobsRepository) {
        self.identity = identity
        self.jobs = jobs
    }

    /// Re-reads the level and the counters.
    ///
    /// Nothing to catch: both reads are best-effort inside the repository, so a service that is down
    /// leaves its own field `nil` and the screen draws an em dash. See ``DriverLevelState``.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.standing = await jobs.standing(driverId: driverId)
        state.isLoading = false
    }
}
