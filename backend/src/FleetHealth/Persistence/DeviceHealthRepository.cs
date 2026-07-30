using Dapper;
using MageRide.FleetHealth.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.FleetHealth.Persistence;

/// <summary>Reads and writes <c>telemetry.device_health</c> (migration 1805).</summary>
public interface IDeviceHealthRepository
{
    /// <summary>Applies a flush of per-device pings as one statement.</summary>
    Task<int> UpsertPingsAsync(IReadOnlyCollection<DeviceHealthPing> pings, CancellationToken cancellationToken);

    /// <summary>Applies a flush of retained <c>veh/+/status</c> payloads (R-15, T-04).</summary>
    Task<int> UpsertStatusAsync(IReadOnlyCollection<DeviceStatusReport> reports, CancellationToken cancellationToken);

    /// <summary>Applies a flush of <c>sys/diag/+</c> reports (US-3.12).</summary>
    Task<int> UpsertDiagnosticsAsync(
        IReadOnlyCollection<DeviceDiagnosticsReport> reports, CancellationToken cancellationToken);

    /// <summary>Applies one binding lifecycle change from <c>provisioning.events</c>.</summary>
    Task<int> ApplyBindingChangeAsync(TrackerBindingChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Moves every device whose derived state no longer matches the recorded one and returns what
    /// moved.
    /// </summary>
    Task<IReadOnlyList<HealthTransition>> SweepTransitionsAsync(
        HealthThresholds thresholds, DateTimeOffset at, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes <c>last_seen_at</c> and the three diagnostics columns onto <c>prov.tracker_bindings</c>
    /// (US-3.12; C030 hands this service those columns).
    /// </summary>
    Task<int> SyncBindingDiagnosticsAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>Per-state device counts for one fleet, derived at <paramref name="at"/>.</summary>
    Task<TrackerStateCounts> ReadFleetCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HealthThresholds thresholds,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// The fleet's devices, worst state first, capped at <paramref name="limit"/> + 1 so the caller can
    /// tell a full page from a truncated one.
    /// </summary>
    Task<IReadOnlyList<DeviceHealthRow>> ReadFleetDevicesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HealthThresholds thresholds,
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>The two silence windows a classification is made against (US-3.13).</summary>
public sealed record HealthThresholds(TimeSpan StaleAfter, TimeSpan OfflineAfter);

/// <inheritdoc cref="IDeviceHealthRepository"/>
/// <remarks>
/// <para>
/// <b>Every write is one set-based statement over arrays.</b> The ingest plane peaks at 20k msg/s
/// (T-10) and this service sees all of it; a statement per device would be a round trip per position
/// on a fact with a five-minute grain. The accumulators collapse to one row per vehicle before
/// getting here, so a flush is bounded by the size of the fleet and not by the sample rate.
/// </para>
/// <para>
/// <b>Every conflicting column takes <c>GREATEST</c> or <c>COALESCE</c>, never the incoming value.</b>
/// Delivery is at-least-once (D6' §2.3) and per-vehicle ordering lapses for seconds during a
/// consumer-group rebalance, so a redelivered or overtaken flush must not be able to make a device
/// look staler than it is, and a report carrying no battery must not erase the battery the last one
/// gave. That makes every write here idempotent and order-insensitive, which is what lets the
/// consumers commit their offsets after the flush and nothing else.
/// </para>
/// <para>
/// <b>No state is written by any of these paths.</b> <c>observed_state</c> is set once when a row is
/// created and moved only by <see cref="SweepTransitionsAsync"/>; the answer a caller reads is derived
/// by <c>telemetry.device_health_state()</c> in the query itself. A ping that set the state to
/// <c>ONLINE</c> on the way in would leave a device that has gone quiet claiming to be online for ever,
/// because a device going quiet sends nothing.
/// </para>
/// </remarks>
public sealed class DeviceHealthRepository(INpgsqlConnectionFactory connectionFactory) : IDeviceHealthRepository
{
    /// <summary>
    /// The classification, spelled once per query. <c>telemetry.device_health_state()</c> is the only
    /// implementation of US-3.13's ladder (migration 1805) — this constant exists so the argument
    /// order cannot drift between the four call sites in this file.
    /// </summary>
    private const string StateOf =
        """
        telemetry.device_health_state(
          binding_state, decommissioned_at, last_ping_at, last_status, last_status_at,
          @StaleAfter, @OfflineAfter, @At)
        """;

    private const string UpsertPingsSql =
        """
        INSERT INTO telemetry.device_health AS d
              (vehicle_id, fleet_id, last_ping_at, last_sample_ts, ping_source, sat_count,
               observed_state, state_changed_at)
        SELECT t.vehicle_id, t.fleet_id, t.ping_at, t.sample_ts, t.source, t.sat_count,
               'ONLINE', t.ping_at
          FROM unnest(@VehicleIds::uuid[], @FleetIds::uuid[], @PingAts::timestamptz[],
                      @SampleTss::timestamptz[], @Sources::smallint[], @SatCounts::smallint[])
               AS t(vehicle_id, fleet_id, ping_at, sample_ts, source, sat_count)
        ON CONFLICT (vehicle_id) DO UPDATE
           SET last_ping_at   = GREATEST(d.last_ping_at, EXCLUDED.last_ping_at),
               last_sample_ts = GREATEST(d.last_sample_ts, EXCLUDED.last_sample_ts),
               ping_source    = COALESCE(EXCLUDED.ping_source, d.ping_source),
               sat_count      = COALESCE(EXCLUDED.sat_count, d.sat_count),
               fleet_id       = COALESCE(EXCLUDED.fleet_id, d.fleet_id);
        """;

    private const string UpsertStatusSql =
        """
        INSERT INTO telemetry.device_health AS d (vehicle_id, last_status, last_status_at, state_changed_at)
        SELECT t.vehicle_id, t.status, t.at, t.at
          FROM unnest(@VehicleIds::uuid[], @Statuses::text[], @Ats::timestamptz[])
               AS t(vehicle_id, status, at)
        ON CONFLICT (vehicle_id) DO UPDATE
           SET last_status    = CASE
                                  WHEN d.last_status_at IS NULL
                                    OR EXCLUDED.last_status_at >= d.last_status_at
                                  THEN EXCLUDED.last_status
                                  ELSE d.last_status
                                END,
               last_status_at = GREATEST(d.last_status_at, EXCLUDED.last_status_at);
        """;

    private const string UpsertDiagnosticsSql =
        """
        INSERT INTO telemetry.device_health AS d
              (vehicle_id, signal_strength, battery_mv, battery_pct, sat_count, last_diag_at)
        SELECT t.vehicle_id, t.signal_strength, t.battery_mv, t.battery_pct, t.sat_count, t.at
          FROM unnest(@VehicleIds::uuid[], @SignalStrengths::smallint[], @BatteryMvs::integer[],
                      @BatteryPcts::smallint[], @SatCounts::smallint[], @Ats::timestamptz[])
               AS t(vehicle_id, signal_strength, battery_mv, battery_pct, sat_count, at)
        ON CONFLICT (vehicle_id) DO UPDATE
           SET signal_strength = COALESCE(EXCLUDED.signal_strength, d.signal_strength),
               battery_mv      = COALESCE(EXCLUDED.battery_mv, d.battery_mv),
               battery_pct     = COALESCE(EXCLUDED.battery_pct, d.battery_pct),
               sat_count       = COALESCE(EXCLUDED.sat_count, d.sat_count),
               last_diag_at    = GREATEST(d.last_diag_at, EXCLUDED.last_diag_at)
         WHERE d.last_diag_at IS NULL
            OR EXCLUDED.last_diag_at >= d.last_diag_at;
        """;

    /// <remarks>
    /// <para>
    /// <c>binding_state</c> and <c>decommissioned_at</c> are <b>assigned</b>: the credential plane is
    /// the only authority on either, and a revoke that failed to overwrite an <c>ACTIVE</c> would leave
    /// a decommissioned tracker reading as merely offline. <c>decommissioned_at</c> is cleared in the
    /// same statement by a fresh bind, because a re-provisioned tracker is not decommissioned (US-3.1
    /// against US-3.8).
    /// </para>
    /// <para>
    /// <c>imei</c> and <c>fleet_id</c> are <b>coalesced</b>, and only a <c>tracker.bound</c> carries
    /// either (C030's envelopes). So an unbind cannot blank the fleet — which is the C006 decision 8
    /// rule seen from the other side: a vehicle that leaves a fleet keeps its history under the old
    /// one, and the fleet whose tracker was just decommissioned is precisely the fleet that has to see
    /// it in that state.
    /// </para>
    /// </remarks>
    private const string ApplyBindingSql =
        """
        INSERT INTO telemetry.device_health AS d
              (vehicle_id, imei, fleet_id, binding_state, decommissioned_at, observed_state, state_changed_at)
        VALUES (@VehicleId, @Imei, @FleetId, @BindingState, @DecommissionedAt, 'OFFLINE', now())
        ON CONFLICT (vehicle_id) DO UPDATE
           SET imei              = COALESCE(EXCLUDED.imei, d.imei),
               fleet_id          = COALESCE(EXCLUDED.fleet_id, d.fleet_id),
               binding_state     = EXCLUDED.binding_state,
               decommissioned_at = EXCLUDED.decommissioned_at;
        """;

    private static readonly string SweepSql =
        $"""
         WITH due AS (
           SELECT vehicle_id,
                  observed_state AS from_state,
                  {StateOf}      AS to_state
             FROM telemetry.device_health
            WHERE observed_state <> {StateOf}
            ORDER BY vehicle_id
            LIMIT @BatchSize
              FOR UPDATE SKIP LOCKED),
         moved AS (
           UPDATE telemetry.device_health d
              SET observed_state   = due.to_state,
                  state_changed_at = @At
             FROM due
            WHERE d.vehicle_id = due.vehicle_id
           RETURNING d.vehicle_id, d.fleet_id)
         SELECT m.vehicle_id   AS vehicle_id,
                m.fleet_id     AS fleet_id,
                due.from_state AS from_state,
                due.to_state   AS to_state
           FROM moved m
           JOIN due ON due.vehicle_id = m.vehicle_id;
         """;

    /// <remarks>
    /// Bounded by a claim CTE and skipped entirely when nothing moved, because the <c>updated_at</c>
    /// trigger on <c>prov.tracker_bindings</c> makes every row touched here a real write. The predicate
    /// compares each column so a device reporting the same battery for a week produces no writes at
    /// all; only a fresher ping or a changed reading does.
    /// </remarks>
    private const string SyncBindingsSql =
        """
        WITH due AS (
          SELECT b.id, d.last_ping_at, d.signal_strength, d.battery_mv, d.sat_count
            FROM prov.tracker_bindings b
            JOIN telemetry.device_health d ON d.vehicle_id = b.vehicle_id
           WHERE b.state = 'ACTIVE'
             AND ((d.last_ping_at IS NOT NULL AND (b.last_seen_at IS NULL OR d.last_ping_at > b.last_seen_at))
                  OR (d.signal_strength IS NOT NULL AND d.signal_strength IS DISTINCT FROM b.signal_strength)
                  OR (d.battery_mv     IS NOT NULL AND d.battery_mv     IS DISTINCT FROM b.battery_mv)
                  OR (d.sat_count      IS NOT NULL AND d.sat_count      IS DISTINCT FROM b.sat_count))
           ORDER BY b.id
           LIMIT @BatchSize
             FOR UPDATE OF b SKIP LOCKED)
        UPDATE prov.tracker_bindings b
           SET last_seen_at    = GREATEST(b.last_seen_at, due.last_ping_at),
               signal_strength = COALESCE(due.signal_strength, b.signal_strength),
               battery_mv      = COALESCE(due.battery_mv, b.battery_mv),
               sat_count       = COALESCE(due.sat_count, b.sat_count)
          FROM due
         WHERE b.id = due.id;
        """;

    private static readonly string CountsSql =
        $"""
         SELECT {StateOf} AS state, count(*)::int AS count
           FROM telemetry.device_health_fleet
          GROUP BY state;
         """;

    /// <remarks>
    /// <b>Worst first.</b> An operator opens this screen to find the devices that need attention, so
    /// the ordering is Offline, Stale, Decommissioned, Online — and within a state the longest silence
    /// first, with a device that has never reported at the very top. The order is fixed rather than a
    /// parameter because <c>items</c> may be truncated at <c>Health:MaxItems</c>, and a truncated list
    /// whose ordering a client chose could hide exactly the rows it was opened to see.
    /// </remarks>
    private static readonly string DevicesSql =
        $"""
         SELECT vehicle_id AS VehicleId, imei AS Imei, state AS State,
                last_ping_at AS LastPingAt, state_changed_at AS StateChangedAt,
                signal_strength AS SignalStrength, battery_mv AS BatteryMv,
                battery_pct AS BatteryPct, sat_count AS SatCount
           FROM (SELECT vehicle_id, imei, last_ping_at, state_changed_at, signal_strength,
                        battery_mv, battery_pct, sat_count,
                        {StateOf} AS state
                   FROM telemetry.device_health_fleet) AS classified
          ORDER BY CASE state
                     WHEN 'OFFLINE'        THEN 0
                     WHEN 'STALE'          THEN 1
                     WHEN 'DECOMMISSIONED' THEN 2
                     ELSE 3
                   END,
                   last_ping_at ASC NULLS FIRST,
                   vehicle_id
          LIMIT @Limit;
         """;

    private readonly INpgsqlConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<int> UpsertPingsAsync(
        IReadOnlyCollection<DeviceHealthPing> pings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pings);

        if (pings.Count == 0)
        {
            return 0;
        }

        var rows = pings as IList<DeviceHealthPing> ?? [.. pings];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            UpsertPingsSql,
            new
            {
                VehicleIds = Map(rows, static p => p.VehicleId),
                FleetIds = Map(rows, static p => p.FleetId),
                PingAts = Map(rows, static p => p.PingAt.ToUniversalTime()),
                SampleTss = Map(rows, static p => p.SampleTs.ToUniversalTime()),
                Sources = Map(rows, static p => p.Source),
                SatCounts = Map(rows, static p => p.SatCount),
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> UpsertStatusAsync(
        IReadOnlyCollection<DeviceStatusReport> reports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return 0;
        }

        var rows = reports as IList<DeviceStatusReport> ?? [.. reports];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            UpsertStatusSql,
            new
            {
                VehicleIds = Map(rows, static r => r.VehicleId),
                Statuses = Map(rows, static r => r.Status),
                Ats = Map(rows, static r => r.At.ToUniversalTime()),
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> UpsertDiagnosticsAsync(
        IReadOnlyCollection<DeviceDiagnosticsReport> reports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return 0;
        }

        var rows = reports as IList<DeviceDiagnosticsReport> ?? [.. reports];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            UpsertDiagnosticsSql,
            new
            {
                VehicleIds = Map(rows, static r => r.VehicleId),
                SignalStrengths = Map(rows, static r => r.SignalStrength),
                BatteryMvs = Map(rows, static r => r.BatteryMv),
                BatteryPcts = Map(rows, static r => r.BatteryPct),
                SatCounts = Map(rows, static r => r.SatCount),
                Ats = Map(rows, static r => r.At.ToUniversalTime()),
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> ApplyBindingChangeAsync(TrackerBindingChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            ApplyBindingSql,
            new
            {
                change.VehicleId,
                change.Imei,
                change.FleetId,
                change.BindingState,
                DecommissionedAt = change.DecommissionedAt?.ToUniversalTime(),
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<HealthTransition>> SweepTransitionsAsync(
        HealthThresholds thresholds, DateTimeOffset at, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        var moved = await connection.QueryAsync<HealthTransition>(new CommandDefinition(
            SweepSql,
            new
            {
                thresholds.StaleAfter,
                thresholds.OfflineAfter,
                At = at.ToUniversalTime(),
                BatchSize = batchSize,
            },
            cancellationToken: cancellationToken));

        return [.. moved];
    }

    public async Task<int> SyncBindingDiagnosticsAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteAsync(new CommandDefinition(
            SyncBindingsSql, new { BatchSize = batchSize }, cancellationToken: cancellationToken));
    }

    public async Task<TrackerStateCounts> ReadFleetCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HealthThresholds thresholds,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(thresholds);

        var rows = await connection.QueryAsync<StateCount>(new CommandDefinition(
            CountsSql,
            new { thresholds.StaleAfter, thresholds.OfflineAfter, At = at.ToUniversalTime() },
            transaction,
            cancellationToken: cancellationToken));

        var counts = TrackerStateCounts.Empty;

        foreach (var row in rows)
        {
            counts = row.State switch
            {
                TrackerHealthStates.Online => counts with { Online = row.Count },
                TrackerHealthStates.Stale => counts with { Stale = row.Count },
                TrackerHealthStates.Offline => counts with { Offline = row.Count },
                TrackerHealthStates.Decommissioned => counts with { Decommissioned = row.Count },

                // Unreachable while ck_device_health_state holds; folded into Offline rather than
                // dropped, so a widened domain shows up as a total that still adds up.
                _ => counts with { Offline = counts.Offline + row.Count },
            };
        }

        return counts;
    }

    public async Task<IReadOnlyList<DeviceHealthRow>> ReadFleetDevicesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HealthThresholds thresholds,
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(thresholds);

        var rows = await connection.QueryAsync<DeviceHealthRow>(new CommandDefinition(
            DevicesSql,
            new { thresholds.StaleAfter, thresholds.OfflineAfter, At = at.ToUniversalTime(), Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <summary>
    /// Projects one column of a batch into the array <c>unnest</c> takes.
    /// </summary>
    /// <remarks>
    /// <see cref="List{T}"/> rather than an array because Npgsql maps both and the list avoids a
    /// second copy; the explicit <c>::type[]</c> casts in the SQL are what pin the element type, so a
    /// column of all-nulls cannot arrive as <c>text[]</c> and fail the insert.
    /// </remarks>
    private static List<TValue> Map<TRow, TValue>(IList<TRow> rows, Func<TRow, TValue> select)
    {
        var values = new List<TValue>(rows.Count);

        foreach (var row in rows)
        {
            values.Add(select(row));
        }

        return values;
    }
}
