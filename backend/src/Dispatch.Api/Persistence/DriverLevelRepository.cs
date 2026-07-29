using Dapper;
using MageRide.Dispatch.Domain;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.driver_levels</c>, <c>dispatch.no_show_events</c> and <c>dispatch.level_config</c>
/// (migrations 0705, 0713) — the Driver Level System's tables (D5' §4, US-6A.6/6A.7/6A.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two services write this table, and the split is the specs' own.</b> reputation-svc (C033)
/// owns every rule that <em>takes</em> a level from its own counters — three confirmed passenger
/// reports and the temporary delisting that comes with them (D5' §4.2), plus the admin appeal
/// restore D3' puts on <c>POST /v1/admin/drivers/{id}/level/restore</c>. Its own CLAUDE.md draws
/// the other half of the line: "rating collection and the level-*up* points (D5' §4.1,
/// <c>trips.ratings</c>) belong to whoever writes ratings". That is here, together with the two
/// surfaces D3' files under dispatch-svc — <c>POST /v1/internal/drivers/{id}/no-show</c> and
/// <c>PUT /v1/admin/drivers/level-config</c>.
/// </para>
/// <para>
/// <b>What keeps two writers safe is one lock, not a convention.</b> Every path on both sides takes
/// the row with <c>SELECT … FOR UPDATE</c> before it changes anything, and C033's documented lock
/// order is block state → counters → level. This side takes the level row and nothing else, so it
/// holds a suffix of that order and the two can never form a cycle. Recorded as a micro-change-set
/// in the C035 handoff: D3' should say which service owns which rule, because right now it is
/// derivable but not stated.
/// </para>
/// </remarks>
public interface IDriverLevelRepository
{
    /// <summary>The driver's row, or <see langword="null"/> when they have never had one.</summary>
    Task<DriverLevelRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the row for update, creating it at D5' §4.2's starting level if absent. The insert is
    /// <c>ON CONFLICT DO NOTHING</c> and the lock follows it, because a <c>SELECT … FOR UPDATE</c>
    /// that matches nothing takes no lock at all — the same trap C033's <c>LockAsync</c> documents.
    /// </summary>
    Task<DriverLevelRow> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        int levelUpThreshold,
        CancellationToken cancellationToken);

    /// <summary>Writes a level, its point remainder and the engine's watermark in one statement.</summary>
    Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DriverLevelRow row,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every rating point this driver has ever earned, recomputed from <c>trips.ratings</c>.
    /// </summary>
    /// <remarks>
    /// D5' §4.2 counts <b>only 4★ and 5★</b> and gives each the value of its own star count
    /// ("5★=5pts, 4★=4pts"); 3★ and below contribute nothing. Scoped to
    /// <c>subject_kind = 'ride'</c> and <c>direction = 'passenger_to_driver'</c>: this level gates
    /// Mode C dispatch, and a Mode A/B session rating belongs to trip-state-svc's plane, which the
    /// project's own boundary rule says never to cross.
    /// </remarks>
    Task<int> TotalRatingPointsAsync(NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// Drivers whose rating total has moved past what the engine last counted — the sweep's work
    /// list, so a level rises without waiting for someone to read it.
    /// </summary>
    Task<IReadOnlyList<Guid>> DriversWithUncountedRatingsAsync(
        NpgsqlConnection connection, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the no-show. Returns <see langword="false"/> when this (driver, ride) pair already has
    /// a row, which is what makes the level decrement happen once however many times the report is
    /// delivered (<c>ux_no_show_driver_ride</c>, migration 0713).
    /// </summary>
    Task<bool> RecordNoShowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        Guid? rideId,
        CancellationToken cancellationToken);

    Task<int> NoShowCountAsync(NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// US-6A.14's acceptance rate: accepted offers over offers made, from this service's own
    /// <c>dispatch.offers</c> log.
    /// </summary>
    Task<(int Offered, int Accepted)> OfferTallyAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken);

    Task<LevelConfigRow> GetConfigAsync(NpgsqlConnection connection, CancellationToken cancellationToken);

    Task<LevelConfigRow> UpdateConfigAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        LevelConfigRow config,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverLevelRepository"/>
public sealed class DriverLevelRepository : IDriverLevelRepository
{
    // ::int on `level` for the reason C033's repository gives: the column is SMALLINT and Dapper
    // matches a record constructor on exact field types.
    private const string Columns =
        """
        driver_id AS DriverId,
        level::int AS Level,
        rating_points AS RatingPoints,
        level_up_threshold AS LevelUpThreshold,
        points_awarded_total AS PointsAwardedTotal
        """;

    private const string ConfigColumns =
        """
        level_up_threshold AS LevelUpThreshold,
        no_show_penalty_points AS NoShowPenaltyPoints,
        cancellation_penalty_points AS CancellationPenaltyPoints,
        job_board_min_level::int AS JobBoardMinLevel
        """;

    /// <summary>D5' §4.2's "only 4★ and 5★", as one predicate both queries below share.</summary>
    private const string CountedRatings =
        """
        FROM trips.ratings r
         WHERE r.subject_kind = 'ride'
           AND r.direction = 'passenger_to_driver'
           AND r.stars >= 4
        """;

    public Task<DriverLevelRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<DriverLevelRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.driver_levels WHERE driver_id = @DriverId;",
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<DriverLevelRow> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        int levelUpThreshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dispatch.driver_levels (driver_id, level_up_threshold)
            VALUES (@DriverId, @Threshold)
                ON CONFLICT (driver_id) DO NOTHING;
            """,
            new { DriverId = driverId, Threshold = levelUpThreshold },
            transaction,
            cancellationToken: cancellationToken));

        return await connection.QuerySingleAsync<DriverLevelRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.driver_levels WHERE driver_id = @DriverId FOR UPDATE;",
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DriverLevelRow row,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(row);

        // `level_up_threshold` is written on every apply, so the per-driver column tracks
        // dispatch.level_config rather than whatever value happened to be configured on the day
        // some other service first created the row. The config table is the authority; this column
        // is the mirror GET /v1/drivers/{id}/level reports.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dispatch.driver_levels
               SET level = @Level,
                   rating_points = @RatingPoints,
                   level_up_threshold = @LevelUpThreshold,
                   points_awarded_total = @PointsAwardedTotal
             WHERE driver_id = @DriverId;
            """,
            new
            {
                row.DriverId,
                Level = (short)row.Level,
                row.RatingPoints,
                row.LevelUpThreshold,
                row.PointsAwardedTotal,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> TotalRatingPointsAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
             SELECT COALESCE(SUM(r.stars), 0)::int
             {CountedRatings}
               AND r.ratee_id = @DriverId;
             """,
            new { DriverId = driverId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> DriversWithUncountedRatingsAsync(
        NpgsqlConnection connection, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // A driver with ratings and no level row yet is included: `points_awarded_total` is 0 for
        // them by COALESCE, so the first 4★ they receive is a difference and puts them on the list.
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
             SELECT r.ratee_id
             {CountedRatings}
             GROUP BY r.ratee_id
             HAVING SUM(r.stars)::int <> COALESCE(
                      (SELECT d.points_awarded_total FROM dispatch.driver_levels d
                        WHERE d.driver_id = r.ratee_id), 0)
              LIMIT @BatchSize;
             """,
            new { BatchSize = batchSize },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<bool> RecordNoShowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        Guid? rideId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // The insert IS the claim (ux_no_show_driver_ride, 0713): no row written, no level taken.
        // A report that carries no ride id cannot be deduplicated and is therefore always counted —
        // the index is partial for that reason, and the endpoint's contract types `rideId` as
        // optional, so this is the honest behaviour rather than a hole.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dispatch.no_show_events (driver_id, ride_id) VALUES (@DriverId, @RideId)
                ON CONFLICT (driver_id, ride_id) WHERE ride_id IS NOT NULL DO NOTHING;
            """,
            new { DriverId = driverId, RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public async Task<int> NoShowCountAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM dispatch.no_show_events WHERE driver_id = @DriverId;",
            new { DriverId = driverId },
            cancellationToken: cancellationToken));
    }

    public async Task<(int Offered, int Accepted)> OfferTallyAsync(
        NpgsqlConnection connection, Guid driverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var tally = await connection.QuerySingleAsync<OfferTally>(new CommandDefinition(
            $"""
             SELECT count(*)::int AS Offered,
                    count(*) FILTER (WHERE status = '{OfferStatuses.Accepted}')::int AS Accepted
               FROM dispatch.offers WHERE driver_id = @DriverId;
             """,
            new { DriverId = driverId },
            cancellationToken: cancellationToken));

        return (tally.Offered, tally.Accepted);
    }

    public async Task<LevelConfigRow> GetConfigAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The seed row is migration 0713's and migrate-verify.sh asserts it, so the fallback is a
        // belt on a brace — but a missing config row must not stop the platform dispatching.
        return await connection.QuerySingleOrDefaultAsync<LevelConfigRow>(new CommandDefinition(
            $"SELECT {ConfigColumns} FROM dispatch.level_config WHERE id = 1;",
            cancellationToken: cancellationToken))
            ?? LevelConfigRow.Defaults;
    }

    public Task<LevelConfigRow> UpdateConfigAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        LevelConfigRow config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(config);

        // Upsert on the singleton id, so a deployment whose seed row was lost is repaired by the
        // first admin write rather than answering 404 forever.
        return connection.QuerySingleAsync<LevelConfigRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.level_config
               (id, level_up_threshold, no_show_penalty_points, cancellation_penalty_points, job_board_min_level)
             VALUES
               (1, @LevelUpThreshold, @NoShowPenaltyPoints, @CancellationPenaltyPoints, @JobBoardMinLevel)
                 ON CONFLICT (id) DO UPDATE
                SET level_up_threshold = EXCLUDED.level_up_threshold,
                    no_show_penalty_points = EXCLUDED.no_show_penalty_points,
                    cancellation_penalty_points = EXCLUDED.cancellation_penalty_points,
                    job_board_min_level = EXCLUDED.job_board_min_level
             RETURNING {ConfigColumns};
             """,
            new
            {
                config.LevelUpThreshold,
                config.NoShowPenaltyPoints,
                config.CancellationPenaltyPoints,
                JobBoardMinLevel = (short)config.JobBoardMinLevel,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Dapper's landing shape for <see cref="OfferTallyAsync"/>'s two aggregates.</summary>
    private sealed record OfferTally(int Offered, int Accepted);
}
