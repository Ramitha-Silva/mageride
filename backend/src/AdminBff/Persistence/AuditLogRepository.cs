using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Persistence;

/// <summary>What <c>GET /v1/admin/audit-log</c> filters on (US-19.3).</summary>
/// <param name="ActorId">Who acted. The Auditor's most common question.</param>
/// <param name="Action">Exact match on the action verb.</param>
/// <param name="SubjectId">The entity acted on — <c>audit.events.entity_id</c>.</param>
/// <param name="From">Inclusive lower bound. Defaults to <c>AdminBff:AuditLogDefaultWindow</c> ago.</param>
/// <param name="To">Exclusive upper bound.</param>
public sealed record AuditLogQuery(
    Guid? ActorId, string? Action, Guid? SubjectId, DateTimeOffset From, DateTimeOffset? To);

/// <summary>
/// <c>audit.events</c>, read-only (D-35, US-19.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no write method here, and that is the design.</b> The only thing on this platform
/// that appends to <c>audit.events</c> is the kernel's <c>IAuditEventWriter</c>; this repository
/// can only <c>SELECT</c>. An append-only table whose reader could also write it is append-only by
/// convention, and the contract calls the log "append-only — there is no write route here".
/// </para>
/// <para>
/// <b>The page is ordered newest-first and the cursor is the identity column.</b> <c>ts</c> is not
/// unique — a mutation writes its row and the interceptor's request row within the same
/// millisecond, and a suspension writes three — so paging on the timestamp would drop or repeat
/// rows at a page boundary. <c>id</c> is a <c>BIGINT GENERATED ALWAYS AS IDENTITY</c>, which is
/// monotonic per insert and is exactly what a stable descending page needs. It is not exposed on
/// the wire: the cursor is opaque and <c>eventId</c> is what the contract shows.
/// </para>
/// </remarks>
public interface IAuditLogRepository
{
    /// <summary>One page, newest first. <paramref name="before"/> is the previous page's last id.</summary>
    Task<IReadOnlyList<(long Id, AuditLogRow Row)>> PageAsync(
        AuditLogQuery query, long? before, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAuditLogRepository"/>
internal sealed class AuditLogRepository(INpgsqlConnectionFactory connections) : IAuditLogRepository
{
    private const string Sql =
        """
        SELECT id                     AS Id,
               event_id               AS EventId,
               actor_id               AS ActorId,
               actor_role             AS ActorRole,
               action                 AS Action,
               entity_type            AS EntityType,
               entity_id              AS EntityId,
               before::text           AS Before,
               after::text            AS After,
               ip                     AS Ip,
               detail::text           AS Detail,
               ts                     AS Ts
          FROM audit.events
         WHERE ts >= @From
           AND (@To::timestamptz IS NULL OR ts < @To)
           AND (@ActorId::uuid   IS NULL OR actor_id  = @ActorId)
           AND (@Action::text    IS NULL OR action    = @Action)
           AND (@SubjectId::uuid IS NULL OR entity_id = @SubjectId)
           AND (@Before::bigint  IS NULL OR id        < @Before)
         ORDER BY id DESC
         LIMIT @Limit;
        """;

    public async Task<IReadOnlyList<(long Id, AuditLogRow Row)>> PageAsync(
        AuditLogQuery query, long? before, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            Sql,
            new
            {
                query.From,
                query.To,
                query.ActorId,
                query.Action,
                query.SubjectId,
                Before = before,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(static row => (
                row.Id,
                new AuditLogRow(
                    row.EventId,
                    row.ActorId,
                    row.ActorRole,
                    row.Action,
                    row.EntityType,
                    row.EntityId,
                    row.Before,
                    row.After,
                    row.Ip,
                    row.Detail,
                    row.Ts))),
        ];
    }

    /// <summary>The row as Dapper materialises it, carrying the paging id alongside the wire shape.</summary>
    private sealed record AuditRow(
        long Id,
        Guid EventId,
        Guid? ActorId,
        string? ActorRole,
        string Action,
        string? EntityType,
        Guid? EntityId,
        string? Before,
        string? After,
        string? Ip,
        string? Detail,
        DateTimeOffset Ts);
}
