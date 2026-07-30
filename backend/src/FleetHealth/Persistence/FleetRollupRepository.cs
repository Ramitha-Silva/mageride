using Dapper;
using MageRide.FleetHealth.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.FleetHealth.Persistence;

/// <summary>
/// One fleet's roster measured against one closed <c>telemetry.fleet_health_5m</c> bucket, plus the
/// bucket before it.
/// </summary>
/// <param name="PreviousReporting">Needed by
/// <see cref="Configuration.FleetHealthOptions.AlertOnCrossingOnly"/>: US-3.16 is written as
/// "N % … <b>goes</b> offline within a 5-minute window", which is a transition, and a transition
/// needs the window before it.</param>
public sealed record FleetWindowCandidate(Guid FleetId, int Expected, int Reporting, int PreviousReporting);

/// <summary>What TimescaleDB says about the aggregate this service depends on.</summary>
/// <param name="Exists">The continuous aggregate is registered.</param>
/// <param name="MaterializedOnly">A read would see only materialised buckets and not the live tail.</param>
/// <param name="HasRefreshPolicy">A refresh job is scheduled for it.</param>
public sealed record AggregateStatus(bool Exists, bool MaterializedOnly, bool HasRefreshPolicy);

/// <summary>
/// Reads <c>telemetry.fleet_health_5m</c>, maintains it, and claims the one alert a window may raise.
/// </summary>
public interface IFleetRollupRepository
{
    /// <summary>What TimescaleDB knows about <c>telemetry.fleet_health_5m</c>.</summary>
    Task<AggregateStatus> ReadAggregateStatusAsync(CancellationToken cancellationToken);

    /// <summary>Materialises <c>[from, to)</c> of the aggregate by calling its own refresh procedure.</summary>
    Task RefreshAggregateAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Every fleet with at least <paramref name="minFleetSize"/> active trackers, for one window.</summary>
    Task<IReadOnlyList<FleetWindowCandidate>> ReadWindowCandidatesAsync(
        DateTimeOffset bucket, DateTimeOffset previousBucket, int minFleetSize, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the window for <paramref name="window"/>'s fleet, or returns <see langword="null"/> when
    /// another replica already has it.
    /// </summary>
    Task<FleetHealthAlert?> TryClaimAlertAsync(
        IUnitOfWork unitOfWork,
        FleetWindowRollup window,
        int windowMinutes,
        double thresholdPct,
        CancellationToken cancellationToken);

    /// <summary>One fleet's window, read inside the caller's fleet-scoped transaction.</summary>
    Task<FleetWindowRollup> ReadFleetWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        DateTimeOffset bucket,
        DateTimeOffset bucketEnd,
        CancellationToken cancellationToken);

    /// <summary>The caller fleet's most recent alert, or <see langword="null"/>.</summary>
    Task<FleetHealthAlert?> ReadLatestAlertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Whether the organisation exists at all — the 404 half of the endpoint's scoping.</summary>
    Task<bool> FleetExistsAsync(Guid fleetId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetRollupRepository"/>
/// <remarks>
/// <para>
/// <b>The numerator is the continuous aggregate's and the denominator is the roster's, and neither
/// could supply the other.</b> <c>telemetry.fleet_health_5m</c> counts distinct vehicles that
/// reported inside a bucket — it has no way to know a fleet has forty trackers of which four never
/// reported, because a vehicle that publishes nothing writes no row for the aggregate to count.
/// <c>prov.tracker_bindings</c> knows the roster and nothing about liveness. US-3.16's percentage is
/// the ratio of the two.
/// </para>
/// <para>
/// <b><c>reporting</c> is capped at <c>expected</c>.</b> The aggregate counts every vehicle carrying
/// the fleet's id in <c>telemetry.positions</c>, which includes a fleet vehicle publishing from a
/// phone (US-3.6's other source) and one whose binding was revoked partway through the window.
/// Without the cap either would make the offline count negative and an outage read as a surplus.
/// </para>
/// </remarks>
public sealed class FleetRollupRepository(INpgsqlConnectionFactory connectionFactory) : IFleetRollupRepository
{
    /// <remarks>
    /// <c>timescaledb_information.jobs</c> keys a continuous-aggregate refresh policy by the
    /// <i>view</i> name, not by the materialisation hypertable's — which is what
    /// <c>migrate-verify.sh</c> already asserts against for the other three rollups.
    /// </remarks>
    private const string AggregateStatusSql =
        """
        SELECT EXISTS (SELECT 1 FROM timescaledb_information.continuous_aggregates
                        WHERE view_schema = 'telemetry' AND view_name = 'fleet_health_5m')     AS exists,
               COALESCE((SELECT materialized_only FROM timescaledb_information.continuous_aggregates
                          WHERE view_schema = 'telemetry' AND view_name = 'fleet_health_5m'),
                        false)                                                                 AS materialized_only,
               EXISTS (SELECT 1 FROM timescaledb_information.jobs
                        WHERE hypertable_schema = 'telemetry'
                          AND hypertable_name   = 'fleet_health_5m'
                          AND proc_name         = 'policy_refresh_continuous_aggregate')       AS has_refresh_policy;
        """;

    private const string WindowCandidatesSql =
        """
        WITH roster AS (
          SELECT fleet_id, count(*)::int AS expected
            FROM prov.tracker_bindings
           WHERE fleet_id IS NOT NULL
             AND state = 'ACTIVE'
           GROUP BY fleet_id)
        SELECT r.fleet_id                              AS fleet_id,
               r.expected                              AS expected,
               COALESCE(cur.active_vehicles, 0)::int   AS reporting,
               COALESCE(prev.active_vehicles, 0)::int  AS previous_reporting
          FROM roster r
          LEFT JOIN telemetry.fleet_health_5m cur
                 ON cur.fleet_id = r.fleet_id AND cur.bucket = @Bucket
          LEFT JOIN telemetry.fleet_health_5m prev
                 ON prev.fleet_id = r.fleet_id AND prev.bucket = @PreviousBucket
         WHERE r.expected >= @MinFleetSize
         ORDER BY r.fleet_id;
        """;

    /// <remarks>
    /// The INSERT is the claim, not a preceding read: every replica evaluates every window and
    /// <c>ux_fleet_health_alert_window</c> is what makes exactly one of them the winner. A replica
    /// whose insert returns no row writes no outbox event, which is how "exactly one alert per window"
    /// survives a deployment of any size.
    /// </remarks>
    private const string ClaimAlertSql =
        """
        INSERT INTO telemetry.fleet_health_alerts
              (fleet_id, bucket, window_minutes, expected_vehicles, reporting_vehicles,
               offline_vehicles, offline_pct, threshold_pct)
        VALUES (@FleetId, @Bucket, @WindowMinutes, @Expected, @Reporting,
                @Offline, @OfflinePct, @ThresholdPct)
        ON CONFLICT (fleet_id, bucket) DO NOTHING
        RETURNING id AS alert_id, fleet_id, bucket, window_minutes::int AS window_minutes,
                  expected_vehicles AS expected, reporting_vehicles AS reporting,
                  offline_vehicles AS offline, offline_pct::float8 AS offline_pct,
                  threshold_pct::float8 AS threshold_pct, raised_at;
        """;

    private const string FleetWindowSql =
        """
        SELECT (SELECT count(*)::int
                  FROM prov.tracker_bindings
                 WHERE fleet_id = @FleetId AND state = 'ACTIVE')      AS expected,
               COALESCE((SELECT active_vehicles
                           FROM telemetry.fleet_health_5m_fleet
                          WHERE bucket = @Bucket), 0)::int             AS reporting;
        """;

    private const string LatestAlertSql =
        """
        SELECT id AS alert_id, fleet_id, bucket, window_minutes::int AS window_minutes,
               expected_vehicles AS expected, reporting_vehicles AS reporting,
               offline_vehicles AS offline, offline_pct::float8 AS offline_pct,
               threshold_pct::float8 AS threshold_pct, raised_at
          FROM telemetry.fleet_health_alerts_fleet
         ORDER BY bucket DESC
         LIMIT 1;
        """;

    private readonly INpgsqlConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<AggregateStatus> ReadAggregateStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<AggregateStatus>(
            new CommandDefinition(AggregateStatusSql, cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>refresh_continuous_aggregate</c> is a procedure that manages its own transactions, so it
    /// cannot run inside one — the same constraint migration 1802's header records for
    /// <c>CREATE MATERIALIZED VIEW … WITH DATA</c>. Hence a bare connection and no unit of work.
    /// </remarks>
    public async Task RefreshAggregateAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "CALL refresh_continuous_aggregate('telemetry.fleet_health_5m', @From, @To);",
            new { From = from.ToUniversalTime(), To = to.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FleetWindowCandidate>> ReadWindowCandidatesAsync(
        DateTimeOffset bucket, DateTimeOffset previousBucket, int minFleetSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FleetWindowCandidate>(new CommandDefinition(
            WindowCandidatesSql,
            new
            {
                Bucket = bucket.ToUniversalTime(),
                PreviousBucket = previousBucket.ToUniversalTime(),
                MinFleetSize = minFleetSize,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FleetHealthAlert?> TryClaimAlertAsync(
        IUnitOfWork unitOfWork,
        FleetWindowRollup window,
        int windowMinutes,
        double thresholdPct,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(window);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<FleetHealthAlert>(new CommandDefinition(
            ClaimAlertSql,
            new
            {
                window.FleetId,
                Bucket = window.Start.ToUniversalTime(),
                WindowMinutes = (short)windowMinutes,
                window.Expected,
                window.Reporting,
                window.Offline,
                OfflinePct = (decimal)window.OfflinePct,
                ThresholdPct = (decimal)thresholdPct,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<FleetWindowRollup> ReadFleetWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        DateTimeOffset bucket,
        DateTimeOffset bucketEnd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleAsync<WindowRow>(new CommandDefinition(
            FleetWindowSql,
            new { FleetId = fleetId, Bucket = bucket.ToUniversalTime() },
            transaction,
            cancellationToken: cancellationToken));

        return new FleetWindowRollup(
            fleetId, bucket, bucketEnd, row.Expected, Math.Min(row.Reporting, row.Expected));
    }

    public async Task<FleetHealthAlert?> ReadLatestAlertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<FleetHealthAlert>(
            new CommandDefinition(LatestAlertSql, transaction: transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> FleetExistsAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM registry.fleets WHERE id = @FleetId);",
            new { FleetId = fleetId },
            cancellationToken: cancellationToken));
    }

    private sealed record WindowRow(int Expected, int Reporting);
}
