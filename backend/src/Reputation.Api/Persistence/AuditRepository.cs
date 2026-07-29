using System.Text.Json;
using MageRide.Shared.Http;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>audit.events</c> — the immutable admin log (D-35, AL-39…AL-42).
/// </summary>
/// <remarks>
/// <para>
/// Shared, append-only and not this service's table: every service that takes an admin decision
/// writes here, and D3' §0 audits the whole <c>/v1/admin</c> family. It is written <b>inside the
/// same transaction as the decision</b>, which is the only thing that makes "with audit" mean
/// anything — an audit row committed separately can be lost by exactly the crash somebody would
/// later want to explain.
/// </para>
/// <para>
/// C052's admin-bff interceptor will audit these routes again at the edge (D6' §2.1 gives
/// <c>audit.events</c> the producer "all (admin-bff interceptor)"). Two rows for one action is the
/// right failure: the interceptor records "an admin called this route", this records "the state
/// went from X to Y", and only the second survives the route being renamed.
/// </para>
/// </remarks>
public interface IAuditRepository
{
    Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? actorId,
        string action,
        string entityType,
        Guid entityId,
        object? before,
        object? after,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAuditRepository"/>
public sealed class AuditRepository : IAuditRepository
{
    /// <summary>The block state was set by hand.</summary>
    public const string BlockStateOverride = "REPUTATION_BLOCK_STATE_OVERRIDE";

    /// <summary>A driver level was restored on appeal (US-6A.8).</summary>
    public const string LevelRestore = "REPUTATION_LEVEL_RESTORE";

    /// <summary>An E-07 flag was dismissed or actioned.</summary>
    public const string FlagResolved = "REPUTATION_FLAG_RESOLVED";

    /// <summary>A level was taken automatically. No actor — the rule decided (D5' §4.2).</summary>
    public const string LevelDecrement = "REPUTATION_LEVEL_DECREMENT";

    public async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? actorId,
        string action,
        string entityType,
        Guid entityId,
        object? before,
        object? after,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.events (actor_id, action, entity_type, entity_id, before, after, ts)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)actorId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = action });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = entityType });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = entityId });
        command.Parameters.Add(Json(before));
        command.Parameters.Add(Json(after));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Json(object? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        Value = value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, MageRideJson.StorageOptions),
    };
}
