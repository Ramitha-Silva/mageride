using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.directional_filters</c> and <c>dispatch.directional_config</c> (migration 0707) —
/// Directional Travel's durable half (DT-01, DT-02, DT-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per activation, and the row is the limit.</b> DT-03 is enforced as
/// <c>COUNT(*) per (driver_id, used_date) &lt;= max_uses_per_day</c> in Asia/Colombo (D-38), which
/// is what makes US-6A.19's anti-gaming rule fall out of the schema rather than out of a decrement
/// somebody has to remember: turning a filter off early marks the row cleared and leaves it
/// counted. ADD §1.15's DT-03 cell mentions a <c>use_count</c> column; ADD §9.1 and both DDL
/// sources use this form, and migration 0707 already chose it.
/// </para>
/// <para>
/// <b>The activation and the count are one statement.</b> A <c>SELECT count(*)</c> followed by an
/// <c>INSERT</c> has a window exactly as wide as the double-tap it is trying to refuse, so the
/// count is a subquery in the insert's <c>WHERE</c> and "no row came back" <em>is</em> the limit
/// being reached. <c>ux_directional_active</c> closes the other half — two filters cannot be live
/// for one driver whatever races.
/// </para>
/// </remarks>
public interface IDirectionalRepository
{
    /// <summary>The driver's live filter, or <see langword="null"/>. Expiry is a predicate here, not
    /// a TTL: the Redis key is a hint and the durable timer can be late.</summary>
    Task<DirectionalFilterRow?> FindActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every live, unexpired filter among <paramref name="driverIds"/> — one round's worth, in one
    /// round trip. The DT-02 predicate's input.
    /// </summary>
    Task<IReadOnlyList<DirectionalFilterRow>> FindActiveForDriversAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> driverIds,
        CancellationToken cancellationToken);

    /// <summary>How many activations this driver has spent on <paramref name="usedDate"/> (DT-03).</summary>
    Task<int> CountUsesAsync(
        NpgsqlConnection connection, Guid driverId, DateOnly usedDate, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes one daily use and writes the filter, or returns <see langword="null"/> when the
    /// driver has none left.
    /// </summary>
    Task<DirectionalFilterRow?> TryActivateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        GeoPoint destination,
        string? label,
        DateTimeOffset expiresAt,
        DateOnly usedDate,
        DateTimeOffset usedDateTzAt,
        int maxUsesPerDay,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears whichever filter is live for a driver. Returns the row that was cleared, or
    /// <see langword="null"/> when there was nothing to clear — which every caller but the manual
    /// <c>DELETE</c> treats as ordinary.
    /// </summary>
    Task<DirectionalFilterRow?> ClearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string reason,
        CancellationToken cancellationToken);

    Task<DirectionalConfigRow> GetConfigAsync(NpgsqlConnection connection, CancellationToken cancellationToken);

    Task<DirectionalConfigRow> UpdateConfigAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DirectionalConfigRow config,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectionalRepository"/>
public sealed class DirectionalRepository : IDirectionalRepository
{
    private const string Columns =
        """
        id AS Id,
        driver_id AS DriverId,
        destination_geo AS Destination,
        label AS Label,
        set_at AS SetAt,
        expires_at AS ExpiresAt,
        used_date AS UsedDate,
        cleared_at AS ClearedAt,
        cleared_reason AS ClearedReason
        """;

    private const string ConfigColumns =
        """
        theta_max_deg::int AS ThetaMaxDeg,
        detour_max_m AS DetourMaxM,
        progress_min_m AS ProgressMinM,
        max_uses_per_day::int AS MaxUsesPerDay,
        max_duration_sec AS MaxDurationSec,
        clear_on_first_trip AS ClearOnFirstTrip
        """;

    public Task<DirectionalFilterRow?> FindActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<DirectionalFilterRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.directional_filters
              WHERE driver_id = @DriverId AND cleared_at IS NULL AND expires_at > now();
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DirectionalFilterRow>> FindActiveForDriversAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> driverIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(driverIds);

        if (driverIds.Count == 0)
        {
            return [];
        }

        // `expires_at > now()` and not just `cleared_at IS NULL`: DT-04 makes the durable timer the
        // source of truth for expiry, and a timer fires *at or after* its deadline. Between the two
        // the row is stale, and a stale filter that kept excluding rides would be the one failure
        // mode a driver could not see or undo.
        var rows = await connection.QueryAsync<DirectionalFilterRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.directional_filters
              WHERE driver_id = ANY(@DriverIds) AND cleared_at IS NULL AND expires_at > now();
             """,
            new { DriverIds = driverIds.ToArray() },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<int> CountUsesAsync(
        NpgsqlConnection connection, Guid driverId, DateOnly usedDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM dispatch.directional_filters
             WHERE driver_id = @DriverId AND used_date = @UsedDate;
            """,
            new { DriverId = driverId, UsedDate = usedDate },
            cancellationToken: cancellationToken));
    }

    public Task<DirectionalFilterRow?> TryActivateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        GeoPoint destination,
        string? label,
        DateTimeOffset expiresAt,
        DateOnly usedDate,
        DateTimeOffset usedDateTzAt,
        int maxUsesPerDay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // INSERT … SELECT … WHERE, so the daily count is evaluated by the same statement that
        // consumes it. Zero rows back means the budget is spent — the 409 the contract names — and
        // never means "something went wrong", because every other failure mode raises.
        return connection.QuerySingleOrDefaultAsync<DirectionalFilterRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.directional_filters
               (driver_id, destination_geo, label, expires_at, used_date, used_date_tz_at)
             SELECT @DriverId, @Destination, @Label, @ExpiresAt, @UsedDate, @UsedDateTzAt
              WHERE (SELECT count(*) FROM dispatch.directional_filters f
                      WHERE f.driver_id = @DriverId AND f.used_date = @UsedDate) < @MaxUsesPerDay
             RETURNING {Columns};
             """,
            new
            {
                DriverId = driverId,
                Destination = destination,
                Label = label,
                ExpiresAt = expiresAt,
                UsedDate = usedDate,
                UsedDateTzAt = usedDateTzAt,
                MaxUsesPerDay = maxUsesPerDay,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<DirectionalFilterRow?> ClearAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // `cleared_at IS NULL` in the predicate is what makes this idempotent: a filter cleared by
        // going offline and then reached by its own expiry timer a second later updates nothing and
        // returns nothing, so the caller emits one `directional.cleared` and not two.
        //
        // Deliberately NOT bounded by `expires_at > now()`: an expiry timer that fired late is
        // clearing a row that is already past its deadline, and that row still has to be marked.
        return connection.QuerySingleOrDefaultAsync<DirectionalFilterRow>(new CommandDefinition(
            $"""
             UPDATE dispatch.directional_filters
                SET cleared_at = now(), cleared_reason = @Reason
              WHERE driver_id = @DriverId AND cleared_at IS NULL
             RETURNING {Columns};
             """,
            new { DriverId = driverId, Reason = reason },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<DirectionalConfigRow> GetConfigAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<DirectionalConfigRow>(new CommandDefinition(
            $"SELECT {ConfigColumns} FROM dispatch.directional_config WHERE id = 1;",
            cancellationToken: cancellationToken));

        // 0707 seeds the singleton, so a missing row means the migration has not been applied.
        // Falling back to the D5' §12.1 values keeps the predicate defined rather than throwing on
        // a dispatch round; nothing else in this service would notice the difference.
        return row ?? DirectionalConfigRow.Defaults;
    }

    public Task<DirectionalConfigRow> UpdateConfigAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DirectionalConfigRow config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(config);

        // Upsert rather than UPDATE: `ck_directional_config_singleton` pins the id at 1, so this
        // can only ever touch the one row, and a database whose seed never ran still ends up with
        // exactly what the admin asked for.
        return connection.QuerySingleAsync<DirectionalConfigRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.directional_config
               (id, theta_max_deg, detour_max_m, progress_min_m, max_uses_per_day, max_duration_sec,
                clear_on_first_trip)
             VALUES (1, @ThetaMaxDeg, @DetourMaxM, @ProgressMinM, @MaxUsesPerDay, @MaxDurationSec,
                     @ClearOnFirstTrip)
                 ON CONFLICT (id) DO UPDATE
                SET theta_max_deg = EXCLUDED.theta_max_deg,
                    detour_max_m = EXCLUDED.detour_max_m,
                    progress_min_m = EXCLUDED.progress_min_m,
                    max_uses_per_day = EXCLUDED.max_uses_per_day,
                    max_duration_sec = EXCLUDED.max_duration_sec,
                    clear_on_first_trip = EXCLUDED.clear_on_first_trip
             RETURNING {ConfigColumns};
             """,
            new
            {
                ThetaMaxDeg = (short)config.ThetaMaxDeg,
                config.DetourMaxM,
                config.ProgressMinM,
                MaxUsesPerDay = (short)config.MaxUsesPerDay,
                config.MaxDurationSec,
                config.ClearOnFirstTrip,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
