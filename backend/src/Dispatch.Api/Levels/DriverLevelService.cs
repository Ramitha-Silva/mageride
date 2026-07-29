using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Eligibility;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Levels;

/// <summary>
/// The Driver Level System's engine and its four D3' surfaces (D5' §4, US-6A.6/6A.7/6A.8/6A.14,
/// US-14.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this service owns and what it does not.</b> D5' §4.2 has four rules. Level-<em>up</em>
/// from ratings is here, because it is a pure function of <c>trips.ratings</c> and nothing else has
/// a claim on it — reputation-svc's own CLAUDE.md hands it over explicitly. The no-show decrement
/// is here because D3' files <c>POST /v1/internal/drivers/{id}/no-show</c> under dispatch-svc and
/// <c>dispatch.no_show_events</c> is this schema's table. The <b>three-reports</b> decrement, the
/// temporary delisting that rides with it and the admin appeal restore are <b>reputation-svc's</b>
/// (C033) and are not reimplemented here: they are decisions about counters, and counters live
/// there and nowhere else.
/// </para>
/// <para>
/// <b><c>level_config.cancellation_penalty_points</c> is stored and never read</b>, and that is the
/// honest state rather than an omission. <c>dispatch.yaml</c>'s <c>LevelConfig</c> names the knob,
/// so the admin surface has to round-trip it; §11.12 gives a driver cancellation a reputation hit
/// and a brief delist — both reputation-svc's, both already applied there — and no spec gives it a
/// level or a point cost. Applying one off <c>reputation.driver_cancelled</c> would also be the one
/// write in this service that a redelivery could double, which D6' §2.3 guarantees will happen.
/// Raised in the C035 handoff.
/// </para>
/// <para>
/// <b>The engine is a recompute, not a queue.</b> Points are summed from <c>trips.ratings</c> and
/// compared against <c>points_awarded_total</c> (migration 0713); only the difference is applied.
/// That makes every entry point — a read, the sweep, two replicas at once, a redelivered anything —
/// idempotent without a consumed-ratings table, and it makes a repair as simple as running it
/// again. The alternative, incrementing on each rating event, is only correct if the event is
/// delivered exactly once, which D6' §2.3 explicitly does not promise.
/// </para>
/// </remarks>
public interface IDriverLevelService
{
    /// <summary>The driver's level, brought up to date with their ratings first.</summary>
    Task<DriverLevelRow> GetLevelAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>US-6A.14's three numbers behind the level badge.</summary>
    Task<DriverStats> GetStatsAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes one driver's points and applies any level-ups they have earned. Returns the row
    /// as it now stands.
    /// </summary>
    Task<DriverLevelRow> RefreshAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>One sweep of the drivers whose ratings have moved since the engine last looked.</summary>
    /// <returns>How many drivers were recounted.</returns>
    Task<int> SweepAsync(CancellationToken cancellationToken);

    /// <summary>
    /// US-6A.7: a driver did not turn up for a ride they had accepted. Takes one level and writes
    /// the audit row. Idempotent per (driver, ride).
    /// </summary>
    /// <returns>The level after the decrement — unchanged when this no-show was already counted.</returns>
    Task<DriverLevelRow> RecordNoShowAsync(Guid driverId, Guid? rideId, CancellationToken cancellationToken);

    /// <summary>
    /// US-6A.8's gate. Throws <c>403 forbidden</c> naming the level when the driver is below
    /// <c>level_config.job_board_min_level</c>.
    /// </summary>
    Task RequireJobBoardAccessAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// AL-16's gate on the booking side: a passenger reputation-svc has marked
    /// <c>BOOKING_DISABLED</c> may not put a ride on the Job Board either.
    /// </summary>
    Task RequirePassengerMayBookAsync(Guid passengerId, CancellationToken cancellationToken);

    Task<LevelConfigRow> GetConfigAsync(CancellationToken cancellationToken);

    Task<LevelConfigRow> UpdateConfigAsync(LevelConfigRow config, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverLevelService"/>
public sealed class DriverLevelService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IDriverLevelRepository levels,
    IReputationGate reputationGate,
    IOptions<DispatchOptions> options,
    ILogger<DriverLevelService> logger) : IDriverLevelService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<DriverLevelRow> GetLevelAsync(Guid driverId, CancellationToken cancellationToken) =>
        // Refresh-then-read, not read-then-hope. The dispatch hot path reads the level through
        // reputation-svc's gRPC and never through this route, so if the sweep is off this is the
        // only thing that would ever move it — and a driver reading their own badge is exactly when
        // being out of date is most visible.
        RefreshAsync(driverId, cancellationToken);

    public async Task<DriverStats> GetStatsAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var level = await RefreshAsync(driverId, cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var noShows = await levels.NoShowCountAsync(connection, driverId, cancellationToken);
        var (offered, accepted) = await levels.OfferTallyAsync(connection, driverId, cancellationToken);

        // A driver who has never been offered anything has accepted everything they were offered.
        // 0 would be the other reading and it is the wrong one: the number is shown to the driver
        // and read by support, and "0%" on a new account describes a refusal that never happened.
        var acceptanceRate = offered == 0 ? 1d : (double)accepted / offered;

        return new DriverStats(acceptanceRate, noShows, level.RatingPoints);
    }

    public async Task<DriverLevelRow> RefreshAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var earned = await levels.TotalRatingPointsAsync(connection, driverId, cancellationToken);
        var current = await levels.FindAsync(connection, null, driverId, cancellationToken);

        // Nothing new and a row that already agrees with the live threshold: no write at all. The
        // sweep and every level read go through here, so the common case has to be a read.
        if (current is not null && current.PointsAwardedTotal == earned &&
            current.LevelUpThreshold == config.LevelUpThreshold)
        {
            return current;
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // Under the row lock from here. reputation-svc writes the same row for its own rules
        // (three reports, the appeal restore) and takes the same lock; this side takes only this
        // row, so the two orders cannot form a cycle.
        var locked = await levels.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, config.LevelUpThreshold, cancellationToken);

        // Re-read inside the lock: another replica may have applied the same delta while this one
        // was deciding to.
        var total = await levels.TotalRatingPointsAsync(unitOfWork.Connection, driverId, cancellationToken);

        var applied = Apply(locked, total, config.LevelUpThreshold);

        if (applied == locked)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return locked;
        }

        await levels.ApplyAsync(unitOfWork.Connection, unitOfWork.Transaction, applied, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        if (applied.Level != locked.Level)
        {
            logger.LogInformation(
                "Driver {DriverId} moved from level {From} to level {To} on {Points} rating points (D5' §4.2)",
                driverId, locked.Level, applied.Level, total);
        }

        return applied;
    }

    /// <summary>
    /// D5' §4.2's arithmetic, as a pure function so it is testable without a database and cannot
    /// drift from the SQL that stores its result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the <em>delta</em> since <c>points_awarded_total</c> is added, which is what makes
    /// calling this twice the same as calling it once. A total that has gone <em>down</em> — a
    /// rating deleted under a PDPA erasure — moves the watermark without taking a level back:
    /// §4.2's level-down list is three reports and a no-show, and a level once earned is not
    /// un-earned by the removal of the evidence.
    /// </para>
    /// <para>
    /// "On crossing threshold: <c>level = min(level+1, 3)</c>, points -= 500" is applied literally,
    /// so a driver already at 3 consumes the points and stays at 3. The alternative — banking them
    /// against a future demotion — would let a driver knocked to Level 2 by a no-show bounce back
    /// the instant the engine next ran, which is not what "3 reports → level -= 1" is for.
    /// </para>
    /// </remarks>
    internal static DriverLevelRow Apply(DriverLevelRow row, int earnedTotal, int threshold)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The column CHECK admits nothing below 1 and the contract types it `minimum: 1`; this is
        // the last line of defence against a division that never terminates.
        var step = Math.Max(1, threshold);
        var delta = Math.Max(0, earnedTotal - row.PointsAwardedTotal);

        var level = row.Level;
        var points = row.RatingPoints + delta;

        while (points >= step)
        {
            points -= step;
            level = Math.Min(DriverLevelRow.MaxLevel, level + 1);
        }

        return row with
        {
            Level = level,
            RatingPoints = points,
            LevelUpThreshold = step,
            PointsAwardedTotal = earnedTotal,
        };
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        List<Guid> drivers;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            drivers = [.. await levels.DriversWithUncountedRatingsAsync(
                connection, _options.LevelSweepBatchSize, cancellationToken)];
        }

        foreach (var driverId in drivers)
        {
            await RefreshAsync(driverId, cancellationToken);
        }

        return drivers.Count;
    }

    public async Task<DriverLevelRow> RecordNoShowAsync(
        Guid driverId, Guid? rideId, CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // The level row is locked BEFORE the audit insert, so the claim and the decrement are one
        // atomic act: two deliveries of the same report cannot both find the row unclaimed.
        var locked = await levels.LockAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, config.LevelUpThreshold, cancellationToken);

        var claimed = await levels.RecordNoShowAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, rideId, cancellationToken);

        if (!claimed)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            logger.LogInformation(
                "No-show for driver {DriverId} on ride {RideId} was already counted; level stays {Level}",
                driverId, rideId, locked.Level);

            return locked;
        }

        var applied = locked with
        {
            // D5' §4.2: "No-show on accepted scheduled ride → level -= 1". Floored at 1, which
            // ck_driver_levels_level enforces anyway — Level 1 is a loss of privileges, not a ban,
            // so there is nothing below it to fall to (US-6A.8).
            Level = Math.Max(DriverLevelRow.MinLevel, locked.Level - 1),
            RatingPoints = Math.Max(0, locked.RatingPoints - config.NoShowPenaltyPoints),
        };

        await levels.ApplyAsync(unitOfWork.Connection, unitOfWork.Transaction, applied, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Driver {DriverId} no-showed on ride {RideId}; level {From} → {To} (US-6A.7)",
            driverId, rideId, locked.Level, applied.Level);

        return applied;
    }

    public async Task RequireJobBoardAccessAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken);
        var level = await RefreshAsync(driverId, cancellationToken);

        if (level.Level < config.JobBoardMinLevel)
        {
            // The message says what happened, why, and what is still available — US-6A.8 is
            // explicit that Level 1 "is NOT a permanent ban; still operates immediate Mode C".
            throw new MageRideException(
                MageRideErrors.Forbidden,
                $"Driver Level {level.Level} has no Job Board or scheduled-ride access (US-6A.8). This is not a " +
                "ban: immediate Mode C dispatch is unaffected, the level rises again with 4- and 5-star ratings, " +
                "and an admin can restore it on appeal.");
        }
    }

    public async Task RequirePassengerMayBookAsync(Guid passengerId, CancellationToken cancellationToken)
    {
        // The same gate ride-svc applies to POST /v1/rides/request, asked the same way dispatch
        // asks about a driver — reputation-svc's precomputed verdict rather than a second reading
        // of its counters, which its own fence reserves.
        var verdicts = await reputationGate.EvaluateAsync([passengerId], cancellationToken);

        if (verdicts.TryGetValue(passengerId, out var verdict) && verdict.Known && !verdict.DispatchEligible)
        {
            throw new MageRideException(
                MageRideErrors.BookingDisabled,
                $"Booking is disabled for this account ({verdict.State}). Clear the outstanding cancellation " +
                "balance to have it restored (US-6A.10b, AL-16).");
        }
    }

    public async Task<LevelConfigRow> GetConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        return await levels.GetConfigAsync(connection, cancellationToken);
    }

    public async Task<LevelConfigRow> UpdateConfigAsync(LevelConfigRow config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (config.LevelUpThreshold < 1)
        {
            errors["levelUpThreshold"] = ["levelUpThreshold must be at least 1."];
        }

        if (config.JobBoardMinLevel is < DriverLevelRow.MinLevel or > DriverLevelRow.MaxLevel)
        {
            errors["jobBoardMinLevel"] = ["jobBoardMinLevel must be between 1 and 3."];
        }

        if (config.NoShowPenaltyPoints < 0)
        {
            errors["noShowPenaltyPoints"] = ["noShowPenaltyPoints cannot be negative."];
        }

        if (config.CancellationPenaltyPoints < 0)
        {
            errors["cancellationPenaltyPoints"] = ["cancellationPenaltyPoints cannot be negative."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var updated = await levels.UpdateConfigAsync(
            unitOfWork.Connection, unitOfWork.Transaction, config, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // Existing `driver_levels.level_up_threshold` rows are NOT rewritten here. Each converges
        // the next time its driver is refreshed, which is what keeps a threshold change from being
        // a fleet-wide UPDATE inside an admin request — and the config row is the authority in the
        // meantime, because that is what the engine reads.
        logger.LogInformation(
            "Driver Level configuration updated: {Threshold} points per level, Job Board from level {MinLevel} " +
            "(US-14.12)",
            updated.LevelUpThreshold, updated.JobBoardMinLevel);

        return updated;
    }
}
