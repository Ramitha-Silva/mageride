using System.Text.Json.Nodes;
using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// <c>GET /v1/admin/audit-log</c> — the immutable admin-action trail (US-19.3, D-35).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and there is no route that is not.</b> The contract says "append-only — there is no
/// write route here", and the repository behind this has no method that could be one. The only
/// writer on the platform is the kernel's <c>IAuditEventWriter</c>.
/// </para>
/// <para>
/// <b>The Auditor is the role this exists for, and the Verification Officer and CSR cannot read
/// it.</b> URD §2.3's audit row: Auditor ✅ read, Admin 👁, Super Admin 👁, Finance 👁, and ➖ for
/// the other two. That is a deliberately narrow list — the trail records what colleagues did, and
/// a queue-facing role has no business browsing it.
/// </para>
/// <para>
/// <b>The stored JSON is re-emitted, not re-serialised.</b> <c>before</c>, <c>after</c> and
/// <c>detail</c> come back as the document that was written. Parsing them into a CLR shape and
/// writing them out again would mean an image recorded by a component that has since changed comes
/// back reshaped — an audit trail that edits its own history on read is not one.
/// </para>
/// </remarks>
internal static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/audit-log", ListAsync)
            .WithName("listAuditLog")
            .WithSummary("Every admin mutation, plus the PII_READ and DOC_VIEW events (US-19.3).")
            .RequireFeature(FeatureAreas.AuditTrail, PermissionGrant.Read);

        return admin;
    }

    private static async Task<Ok<CursorPage<AuditEventResponse>>> ListAsync(
        Guid? actorId,
        string? action,
        Guid? subjectId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        IAuditLogRepository repository,
        IOptions<AdminBffOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var page = PageRequest.Create(cursor, limit);

        // A default window rather than "everything ever": the table is append-only and unbounded,
        // and an operator looking for yesterday should not page through years to reach it.
        var since = from ?? clock.GetUtcNow() - options.Value.AuditLogDefaultWindow;

        var before = CursorCodec.Unsigned.TryDecodeString(page.Cursor, out var decoded)
                     && long.TryParse(decoded, out var parsed)
            ? parsed
            : (long?)null;

        var rows = await repository.PageAsync(
            new AuditLogQuery(actorId, action?.Trim(), subjectId, since, to),
            before,
            page.OverfetchLimit,
            cancellationToken);

        var slab = rows.Select(static row => (row.Id, Response: ToResponse(row.Row))).ToArray();

        var envelope = CursorPage<(long Id, AuditEventResponse Response)>.FromOverfetch(
            slab, page.Limit, static last => CursorCodec.Unsigned.EncodeString(last.Id.ToString()));

        return TypedResults.Ok(envelope.Select(static entry => entry.Response));
    }

    private static AuditEventResponse ToResponse(Domain.AuditLogRow row) => new(
        row.EventId,
        row.ActorId,
        row.ActorRole,
        row.Action,
        row.EntityId,
        row.EntityType,
        Parse(row.Before),
        Parse(row.After),
        Parse(row.Detail),
        row.Ip,
        row.Ts);

    /// <summary>
    /// The stored document, verbatim.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonNode"/> because the column is <c>jsonb</c> and its shape is whatever the
    /// component that wrote it recorded — there is no CLR type that covers a before-image of a
    /// vehicle, a tariff card and a feature flag, and inventing one would mean discarding whatever
    /// did not fit.
    /// </remarks>
    private static JsonNode? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
}
