using System.Text.Json;
using MageRide.Shared.Http;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Messaging;

/// <summary>
/// One row of <c>audit.events</c> before it is written — the D-35 fact, with the four columns
/// migration 1312 added for the admin-bff interceptor.
/// </summary>
/// <remarks>
/// <b>Every field except <see cref="Action"/> is optional, and that is the point.</b> The three
/// producers this shape has to cover are not the same shape of fact: a service decision knows the
/// entity and both images and has no request behind it; the interceptor knows the request and the
/// actor and may not know the entity at all; a device-initiated observation (D-17's
/// <c>mqtt.rate_violation</c>) has no human actor. A record that demanded all of them would be
/// filled in with placeholders by two of the three, and a placeholder in an audit trail is worse
/// than an absence — it cannot be told apart from a fact.
/// </remarks>
/// <param name="Action">Dotted or screaming-snake verb, e.g. <c>VEHICLE_SUSPENDED</c>. Required.</param>
/// <param name="EntityType">Aggregate the action was against, e.g. <c>vehicle</c>.</param>
/// <param name="EntityId">Aggregate id. Also the partition key when this is published (D6' §2.1).</param>
/// <param name="ActorId">Who caused it. Null for a scheduled job or a rule (1305's comment).</param>
/// <param name="ActorRole">
/// The canonical role exercised, as it stood at the time. Recorded rather than joined —
/// <c>iam.user_roles</c> is mutable, and a grant revoked afterwards must not erase the record that
/// it was held.
/// </param>
/// <param name="Before">State before, or null for an observation rather than a change.</param>
/// <param name="After">State after, or the observation itself.</param>
/// <param name="Ip">Caller address as the gateway reported it.</param>
/// <param name="Detail">What the caller's <em>request</em> was, kept apart from what the entity did.</param>
public sealed record AuditEntry(
    string Action,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? ActorId = null,
    string? ActorRole = null,
    object? Before = null,
    object? After = null,
    string? Ip = null,
    object? Detail = null)
{
    /// <summary>
    /// Idempotency key for a consumer (D6' §2.3). Assigned when the entry is built rather than by
    /// the database default, so a caller can publish the same id it stored.
    /// </summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>
/// <c>audit.events</c> — the immutable admin log (D-35, AL-39…AL-42), written once and read by
/// <c>GET /v1/admin/audit-log</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared and append-only, and it belongs to no single bounded context.</b> D3' §0 audits the
/// whole <c>/v1/admin</c> family and D6' §2.1 gives the topic the producer "all (admin-bff
/// interceptor)" — so reputation-svc, transit-svc and admin-bff all write here, and the third
/// caller is what promoted this into the kernel (the C057 handoff asked for it by name). Two
/// copies of an INSERT into a shared, append-only table is how two services start disagreeing
/// about what an audit row contains.
/// </para>
/// <para>
/// <b>It takes the caller's connection and transaction on purpose.</b> A decision and its audit row
/// commit together or not at all — a row committed separately is lost by exactly the crash somebody
/// would later want explained. Passing <see langword="null"/> for the transaction is legitimate and
/// means "there is no state change to be atomic with": the admin-bff interceptor's route-level row
/// and a read-access <c>PII_READ</c> / <c>DOC_VIEW</c> event are both of that kind.
/// </para>
/// <para>
/// <b>There is no update and no delete.</b> Not by convention — there is no method for one. 1305's
/// <c>REVOKE</c> is a backstop; this is the code path, and a correction to an audit trail is a new
/// row saying so.
/// </para>
/// </remarks>
public interface IAuditEventWriter
{
    /// <summary>Appends one row and returns its <see cref="AuditEntry.EventId"/>.</summary>
    Task<Guid> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AuditEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAuditEventWriter"/>
public sealed class AuditEventWriter : IAuditEventWriter
{
    private const string InsertSql =
        """
        INSERT INTO audit.events
              (event_id, actor_id, actor_role, action, entity_type, entity_id, before, after, ip, detail, ts)
        VALUES ($1,       $2,       $3,         $4,     $5,          $6,        $7,     $8,    $9, $10,    $11);
        """;

    public async Task<Guid> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AuditEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);

        await using var command = new NpgsqlCommand(InsertSql, connection, transaction);

        command.Parameters.Add(Uuid(entry.EventId));
        command.Parameters.Add(Uuid(entry.ActorId));
        command.Parameters.Add(Text(entry.ActorRole));
        command.Parameters.Add(Text(entry.Action));
        command.Parameters.Add(Text(entry.EntityType));
        command.Parameters.Add(Uuid(entry.EntityId));
        command.Parameters.Add(Json(entry.Before));
        command.Parameters.Add(Json(entry.After));
        command.Parameters.Add(Text(entry.Ip));
        command.Parameters.Add(Json(entry.Detail));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = now });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return entry.EventId;
    }

    private static NpgsqlParameter Uuid(Guid? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Uuid,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter Text(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    /// <remarks>
    /// <c>StorageOptions</c>, not the wire options: a stored image is read back by an auditor years
    /// later and must not have been reshaped by whatever the HTTP layer's naming policy was on the
    /// day it was written.
    /// </remarks>
    private static NpgsqlParameter Json(object? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        Value = value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, MageRideJson.StorageOptions),
    };
}
