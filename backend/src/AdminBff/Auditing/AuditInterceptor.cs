using MageRide.AdminBff.Configuration;
using MageRide.Shared.Auth;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Auditing;

/// <summary>
/// What a mutating route records into <c>audit.events</c> (D-35), declared on the route itself.
/// </summary>
/// <param name="Action">
/// The <c>action</c> column, e.g. <c>VEHICLE_SUSPENDED</c>. Screaming snake because that is the
/// spelling <c>server_db_schema.md</c> §23 gives the two read-access actions (<c>DOC_VIEW</c>,
/// <c>PII_READ</c>) and one vocabulary is worth more than a prettier one.
/// </param>
/// <param name="EntityType">The <c>entity_type</c> column, e.g. <c>vehicle</c>.</param>
public sealed record AuditActionMetadata(string Action, string? EntityType);

/// <summary>
/// The D-35 interceptor: every mutating route on this surface writes an <c>audit.events</c> row,
/// and one that does not is a bug rather than a quiet success.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three things happen here, and the middle one is the fence.</b> Before the handler, the
/// request's actor, address and shape are stamped onto the request-scoped
/// <see cref="AdminAuditContext"/>. After a <em>successful</em> handler, the context is required to
/// be non-empty: a mutating route that changed something and recorded nothing throws, which is a
/// 500 and a failing test, not a silent gap in the trail. Then anything the handler did not already
/// flush inside its own transaction is written here, and every row is mirrored onto
/// <c>audit.events</c> on Redpanda (D6' §2.1, D7' §4.2 <c>Audit__Topic</c>).
/// </para>
/// <para>
/// <b>Only successes are audited.</b> A 4xx is a request that was refused — nothing changed, and a
/// row saying an admin suspended a vehicle they were not allowed to suspend would be a false entry
/// in an immutable log. The refusal itself is in the access log and in telemetry. A 5xx is worse
/// than useless: nobody knows whether the write landed, so the honest record is the one the
/// handler's own transaction did or did not commit.
/// </para>
/// <para>
/// <b>The topic publish is best-effort and the row is not.</b> Postgres holds the immutable log
/// that <c>GET /v1/admin/audit-log</c> reads and that D-35 is about; the topic is the cold-storage
/// sink D6' §2.1 registers. A broker that is down must not roll back a suspension that has already
/// committed, so the failure is logged at ERROR and the request still succeeds — but it happens
/// after the row, never instead of it.
/// </para>
/// <para>
/// <b>There is no way to switch it off.</b> `AdminBffApplication` attaches this filter to the whole
/// <c>/v1/admin</c> group and refuses to start if any mutating endpoint lacks an
/// <see cref="AuditActionMetadata"/> — so "a mutation performed with the interceptor disabled" is
/// not a state this service can be deployed in. D7' §4.2's <c>Rbac__DenyByDefault</c> is listed as
/// a variable and is treated the same way: a fence with an off switch is a default.
/// </para>
/// </remarks>
internal sealed class AuditInterceptor(
    INpgsqlConnectionFactory connections,
    IEventPublisher publisher,
    IOptions<AdminBffOptions> options,
    ILogger<AuditInterceptor> logger) : IEndpointFilter
{
    /// <summary>The verbs that change something. A GET is audited only where AL-39/AL-40 say so.</summary>
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.OrdinalIgnoreCase) { HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete };

    private readonly AdminBffOptions.AuditOptions _audit =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Audit;

    private readonly string _topic = options.Value.Audit.Topic;

    /// <summary>
    /// Whether this endpoint <em>can</em> change state, and must therefore be able to record
    /// something. Used by the start-up guard, which has no request to look at.
    /// </summary>
    internal static bool IsMutating(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;

        return methods is not null && methods.Any(MutatingMethods.Contains);
    }

    /// <summary>
    /// Whether <em>this request</em> changes state.
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="IsMutating(Endpoint)"/> on a route mapped for several
    /// verbs — the GTFS pass-through carries all four, and asking the endpoint would call a status
    /// poll a mutation.
    /// </remarks>
    internal static bool IsMutating(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MutatingMethods.Contains(request.Method);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var endpoint = http.GetEndpoint();
        var mutating = endpoint is not null && IsMutating(endpoint) && IsMutating(http.Request);
        var audit = (AdminAuditContext)http.RequestServices.GetRequiredService<IAdminAuditContext>();

        audit.Begin(endpoint?.Metadata.GetMetadata<AuditActionMetadata>(), ActorOf(http), DetailOf(http));

        var result = await next(context).ConfigureAwait(false);

        // Read after the handler: a typed result sets the status when it is written, not when it is
        // returned, so `StatusCode` alone is 200 for a 201 that has not been executed yet.
        var status = StatusOf(http, result);

        if (status is < 200 or >= 300)
        {
            return result;
        }

        if (mutating && audit.RecordedCount == 0)
        {
            // D-35, stated as an invariant rather than a review comment. The alternative — writing a
            // bare "somebody called this route" row — would make the trail always look complete and
            // would hide exactly the routes that forgot to say what they changed.
            throw new InvalidOperationException(
                $"{http.Request.Method} {http.Request.Path} changed state and recorded no audit.events row. "
                + "Every mutation on this surface passes the D-35 interceptor: the handler must call "
                + "IAdminAuditContext.Record(...) for what it changed.");
        }

        if (audit.HasPending)
        {
            await using var connection = await connections.OpenAsync(http.RequestAborted).ConfigureAwait(false);
            await audit.FlushAsync(connection, transaction: null, http.RequestAborted).ConfigureAwait(false);
        }

        await PublishAsync(audit, http.RequestAborted).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// The status the response will carry, taking a not-yet-executed typed result into account.
    /// </summary>
    private static int StatusOf(HttpContext http, object? result) =>
        http.Response.HasStarted
            ? http.Response.StatusCode
            : result as IStatusCodeHttpResult is { StatusCode: { } declared } ? declared : http.Response.StatusCode;

    private AuditActor ActorOf(HttpContext http)
    {
        var roles = http.User.Roles();

        return new AuditActor(
            http.User.SubjectId(),
            // Ordinal-sorted rather than "the first claim": claim order is a property of how the
            // token was built, and an audit column that changes when iam-svc reorders a list is a
            // column an auditor cannot group by.
            roles.Count == 0 ? null : roles.Order(StringComparer.Ordinal).First(),
            AddressOf(http));
    }

    private string? AddressOf(HttpContext http)
    {
        if (_audit.TrustForwardedFor && http.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } value)
        {
            // The left-most entry is the original client; everything after it is a hop. Trimmed to
            // something a column can hold, because the header is caller-influenced and a very long
            // one is either a proxy chain nobody will read or an attempt to bloat the trail.
            var first = value.Split(',')[0].Trim();
            return first.Length is > 0 and <= 64 ? first : null;
        }

        return http.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// What the interceptor knows about the request, kept apart from what the handler knows about
    /// the entity (migration 1312).
    /// </summary>
    private static Dictionary<string, object?> DetailOf(HttpContext http) =>
        new(StringComparer.Ordinal)
        {
            ["method"] = http.Request.Method,
            ["path"] = http.Request.Path.Value,
            // The whole union, because `actor_role` records one of them and a multi-role account's
            // authority is the union (AL-06). Absent rather than empty for an unauthenticated call,
            // which this surface does not have.
            ["roles"] = http.User.Roles() is { Count: > 0 } roles ? roles : null,
            ["idempotencyKey"] = http.Request.Headers.TryGetValue(MageRideHeaders.IdempotencyKey, out var key)
                ? key.ToString()
                : null,
        };

    private async Task PublishAsync(AdminAuditContext audit, CancellationToken cancellationToken)
    {
        if (!_audit.PublishToTopic || audit.Flushed.Count == 0)
        {
            return;
        }

        foreach (var entry in audit.Flushed)
        {
            try
            {
                await publisher.PublishAsync(ToMessage(entry), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Deliberately swallowed. The row is committed and is the record; refusing the
                // response now would tell an operator their suspension failed when it did not.
                logger.LogError(
                    exception,
                    "audit.events row {EventId} ({Action}) was stored but could not be published to {Topic}. "
                    + "The immutable log in Postgres is intact; the D6' §2.1 sink is behind.",
                    entry.EventId,
                    entry.Action,
                    _topic);
            }
        }
    }

    /// <summary>
    /// The D6' §2.2 envelope, keyed by <c>entityId</c> per the §2.1 registry.
    /// </summary>
    /// <remarks>
    /// A row with no entity — there are none on this surface today, but C065's PDPA fulfilment and
    /// an operator action against nothing in particular are the shape that would have one — falls
    /// back to the event id as the key. Not to a constant: a single key would funnel the whole
    /// topic through one partition, and ordering "per entity" is vacuous when there is no entity.
    /// </remarks>
    private EventMessage ToMessage(AuditEntry entry) => new(
        _topic,
        (entry.EntityId ?? entry.EventId).ToString(),
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                eventId = entry.EventId,
                actorId = entry.ActorId,
                actorRole = entry.ActorRole,
                action = entry.Action,
                entityType = entry.EntityType,
                entityId = entry.EntityId,
                before = entry.Before,
                after = entry.After,
                ip = entry.Ip,
                detail = entry.Detail,
            },
            MageRideJson.Options),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventId"] = entry.EventId.ToString(),
            ["action"] = entry.Action,
        });
}

/// <summary>Declares what a route records, and is what the start-up guard looks for.</summary>
public static class AuditEndpointExtensions
{
    /// <summary>
    /// Names the <c>audit.events</c> action and entity type this route writes (D-35).
    /// </summary>
    /// <remarks>
    /// Required on every mutating route: <c>AdminBffApplication</c> enumerates the route table at
    /// start-up and refuses to build without it. Declaring it on a read route is legitimate and is
    /// how AL-39's <c>DOC_VIEW</c> and AL-40's <c>PII_READ</c> are recorded — the handler calls
    /// <c>Record</c> and the interceptor writes it, exactly as for a mutation.
    /// </remarks>
    public static TBuilder Audited<TBuilder>(this TBuilder builder, string action, string? entityType = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return builder.WithMetadata(new AuditActionMetadata(action, entityType));
    }
}
