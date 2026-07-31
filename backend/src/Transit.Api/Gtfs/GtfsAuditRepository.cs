using System.Text.Json;
using MageRide.Shared.Http;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Transit.Gtfs;

/// <summary>
/// <c>audit.events</c> — the immutable admin log (D-35).
/// </summary>
/// <remarks>
/// <para>
/// Shared, append-only, and not this service's table: every service that takes an admin decision
/// writes here, and D3' §0 audits the whole <c>/v1/admin</c> family. It is written <b>inside the
/// same transaction as the decision</b>, which is the only thing that makes "audited" mean
/// anything — a row committed separately is lost by exactly the crash somebody would later want
/// explained.
/// </para>
/// <para>
/// C062's admin-bff interceptor will audit these routes again at the edge (D6' §2.1 gives
/// <c>audit.events</c> the producer "all (admin-bff interceptor)"), and SCR-AP-016 reaches this
/// service through that proxy. Two rows for one action is the right failure: the interceptor
/// records "an admin called this route", this records "feed A went live and feed B was archived",
/// and only the second survives the route being renamed.
/// </para>
/// <para>
/// <b>The second copy of this writer.</b> reputation-svc (C033) has the same twelve lines, and the
/// third caller should promote it into the kernel next to <c>AuditEvent</c> — raised in the C057
/// handoff for C062, which is the component that will need it on every mutating route it owns.
/// </para>
/// </remarks>
public interface IGtfsAuditRepository
{
    Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? actorId,
        string action,
        Guid feedVersionId,
        object? before,
        object? after,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGtfsAuditRepository"/>
internal sealed class GtfsAuditRepository : IGtfsAuditRepository
{
    /// <summary><c>entity_type</c> for every GTFS lifecycle fact.</summary>
    public const string FeedEntity = "gtfs_feed";

    /// <summary>An operator uploaded a zip (US-28.1).</summary>
    public const string FeedUploaded = "GTFS_FEED_UPLOADED";

    /// <summary>
    /// Validation reached a verdict (BR-32.1). <b>Actor-less by construction</b> — a queued job
    /// decided it, not a person, and <c>audit.events.actor_id</c> is nullable for exactly this.
    /// </summary>
    public const string FeedValidated = "GTFS_FEED_VALIDATED";

    /// <summary>A feed went live (US-28.2), including a rollback, which is the same act (BR-32.3).</summary>
    public const string FeedActivated = "GTFS_FEED_ACTIVATED";

    public async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? actorId,
        string action,
        Guid feedVersionId,
        object? before,
        object? after,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
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
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = FeedEntity });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = feedVersionId });
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
