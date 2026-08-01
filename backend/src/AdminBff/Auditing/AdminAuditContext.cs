using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.AdminBff.Auditing;

/// <summary>Who did it and from where — stamped once per request by the interceptor.</summary>
/// <param name="ActorId">The <c>sub</c> claim. Never null on this surface: every route is authenticated.</param>
/// <param name="ActorRole">
/// The canonical role the caller exercised. When several are held the union is sorted and the
/// <em>first</em> is recorded, so the value is deterministic rather than dependent on claim order —
/// the full set is in <c>detail.roles</c>, which is what an auditor reading a multi-role account
/// actually needs.
/// </param>
/// <param name="Ip">Address as the gateway reported it, or the socket's.</param>
public sealed record AuditActor(Guid? ActorId, string? ActorRole, string? Ip);

/// <summary>
/// The request-scoped collector the D-35 interceptor drains.
/// </summary>
/// <remarks>
/// <para>
/// <b>A handler records the fact; the interceptor decides it was recorded.</b> Splitting it that
/// way is what makes "every mutation is audited" checkable rather than aspirational: a handler
/// cannot know whether it is on a mutating route, and an interceptor cannot know what changed. So
/// the handler supplies the entity and the before/after images, the interceptor supplies the actor,
/// the address and the request, and a mutating route that finishes with nothing recorded is a
/// <b>500</b> — see <see cref="AuditInterceptor"/>.
/// </para>
/// <para>
/// <b>Flush inside the handler's own transaction wherever there is one.</b>
/// <see cref="FlushAsync(IUnitOfWork, CancellationToken)"/> is the call to make just before
/// <c>CommitAsync</c>: the decision and its audit row then commit together or not at all, which is
/// the only thing that makes "audited" mean anything — a row committed separately is lost by
/// exactly the crash somebody would later want explained. What the interceptor writes afterwards is
/// whatever was left: a route with no transaction (a proxied call, a read-access event) and nothing
/// else.
/// </para>
/// </remarks>
public interface IAdminAuditContext
{
    /// <summary>What the route declared through <c>.Audited(...)</c>. Null outside a mutating route.</summary>
    AuditActionMetadata? Route { get; }

    /// <summary>How many facts this request has recorded, flushed or not.</summary>
    int RecordedCount { get; }

    /// <summary>Records one fact, defaulting the action and entity type to the route's.</summary>
    /// <param name="entityId">The aggregate the action was against.</param>
    /// <param name="before">State before, or null when nothing existed.</param>
    /// <param name="after">State after, or null for a deletion.</param>
    /// <param name="action">
    /// Overrides the route's action. Used where one route reaches two facts — resolving a report is
    /// <c>REPORT_CONFIRMED</c> or <c>REPORT_DISMISSED</c>, and an auditor filtering by action must
    /// be able to tell them apart.
    /// </param>
    /// <param name="entityType">Overrides the route's entity type.</param>
    void Record(
        Guid? entityId,
        object? before = null,
        object? after = null,
        string? action = null,
        string? entityType = null);

    /// <summary>Writes everything not yet written, inside <paramref name="unitOfWork"/>.</summary>
    Task FlushAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken);

    /// <summary>Writes everything not yet written on <paramref name="connection"/>.</summary>
    Task FlushAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAdminAuditContext"/>
internal sealed class AdminAuditContext(IAuditEventWriter writer, TimeProvider clock) : IAdminAuditContext
{
    private readonly List<AuditEntry> _entries = [];

    private int _flushed;

    public AuditActionMetadata? Route { get; private set; }

    public AuditActor Actor { get; private set; } = new(null, null, null);

    /// <summary>Request facts the interceptor stamps into <c>audit.events.detail</c>.</summary>
    public IReadOnlyDictionary<string, object?>? RequestDetail { get; private set; }

    public int RecordedCount => _entries.Count;

    /// <summary>Everything recorded this request, in order. Read by the interceptor and by tests.</summary>
    public IReadOnlyList<AuditEntry> Entries => _entries;

    /// <summary>Called once by the interceptor, before the handler runs.</summary>
    internal void Begin(AuditActionMetadata? route, AuditActor actor, IReadOnlyDictionary<string, object?> detail)
    {
        Route = route;
        Actor = actor;
        RequestDetail = detail;
    }

    public void Record(
        Guid? entityId,
        object? before = null,
        object? after = null,
        string? action = null,
        string? entityType = null)
    {
        var resolved = action ?? Route?.Action
            ?? throw new InvalidOperationException(
                "There is no audit action to record under. A route that records outside the interceptor "
                + "must pass `action` explicitly; a mutating route should declare one with .Audited(...).");

        _entries.Add(new AuditEntry(
            resolved,
            EntityType: entityType ?? Route?.EntityType,
            EntityId: entityId,
            ActorId: Actor.ActorId,
            ActorRole: Actor.ActorRole,
            Before: before,
            After: after,
            Ip: Actor.Ip,
            Detail: RequestDetail));
    }

    public Task FlushAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return FlushAsync(unitOfWork.Connection, unitOfWork.Transaction, cancellationToken);
    }

    public async Task FlushAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var now = clock.GetUtcNow();

        // Indexed rather than enumerated: a handler may record another fact while this runs (a
        // cascade that audits its own consequence), and the loop has to reach it rather than throw
        // a collection-modified exception halfway through writing an audit trail.
        while (_flushed < _entries.Count)
        {
            var entry = _entries[_flushed];
            await writer.WriteAsync(connection, transaction, entry, now, cancellationToken).ConfigureAwait(false);
            _flushed++;
        }
    }

    /// <summary>Entries written so far. The interceptor publishes exactly these onto the topic.</summary>
    internal IReadOnlyList<AuditEntry> Flushed => _entries[.._flushed];

    /// <summary>Whether anything is still unwritten.</summary>
    internal bool HasPending => _flushed < _entries.Count;
}
