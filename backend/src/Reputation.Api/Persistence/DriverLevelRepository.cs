using Dapper;
using MageRide.Reputation.Domain;
using Npgsql;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>dispatch.driver_levels</c> — the Driver Level System's row (D5' §4.2, US-6A.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is another schema's table and reputation-svc is nonetheless its writer.</b> D4' §6 and
/// server_db_schema.md §6 print the DDL under <c>dispatch</c>, but every rule that *changes* a
/// level is this service's by D5' §4.2 — three passenger reports take one, a no-show on an accepted
/// scheduled ride takes one (US-6A.7), and D3' puts the appeal restore on reputation-svc's admin
/// surface. D3' also gives this service <c>GetDriverLevel</c> over gRPC, so the level has to be
/// readable here whatever happens.
/// </para>
/// <para>
/// The alternative — a second <c>reputation.driver_levels</c> — would be two tables for one fact,
/// which is the thing this component's fence exists to prevent. So: <b>reputation-svc is the sole
/// writer of <c>dispatch.driver_levels</c></b>, dispatch-svc reads it for scoring and for
/// <c>GET /v1/drivers/{id}/level</c>, and the table's placement is raised as a micro-change-set in
/// the C033 handoff — it belongs in the <c>reputation</c> schema.
/// </para>
/// </remarks>
public interface IDriverLevelRepository
{
    Task<DriverLevelRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>Takes the row for update, creating it at the D5' §4.2 starting level if absent.</summary>
    Task<DriverLevelRow> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid driverId, int levelUpThreshold,
        CancellationToken cancellationToken);

    /// <summary>Writes a level, clamped to 1..3 by the caller and by <c>ck_driver_levels_level</c>.</summary>
    Task SetLevelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid driverId, int level,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverLevelRepository"/>
public sealed class DriverLevelRepository : IDriverLevelRepository
{
    // ::int for the same reason CounterRepository casts — `level` is SMALLINT and Dapper matches
    // a record constructor on exact field types.
    private const string Columns =
        """
        driver_id AS DriverId,
        level::int AS Level,
        rating_points AS RatingPoints,
        level_up_threshold AS LevelUpThreshold
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid driverId, int levelUpThreshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // "Start Level 3" (D5' §4.2) is the column default; the threshold is written because it is
        // admin-configurable per driver (PUT /v1/admin/drivers/level-config, US-14.12) and a row
        // created by this service should carry the configured value rather than the DDL's.
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

    public Task SetLevelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid driverId, int level,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dispatch.driver_levels SET level = @Level WHERE driver_id = @DriverId;",
            new { DriverId = driverId, Level = level },
            transaction,
            cancellationToken: cancellationToken));
    }
}
