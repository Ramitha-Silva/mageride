using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Support.Domain;

namespace MageRide.Support.Persistence;

/// <summary>A <c>support.tickets</c> row, as it actually stands.</summary>
/// <param name="ScreenshotUploadId">
/// The <c>docs.uploads</c> id (migration 1309), and the only evidence link this service ever writes.
/// </param>
/// <param name="LegacyScreenshotUrl">
/// §13's original <c>screenshot_url</c>. <b>Nothing here writes it</b> — fare-svc (C050) does, for
/// AL-47's driver-QR dispute, and dropping the column from this read would silently lose the
/// evidence on a Finance-queue ticket this service is responsible for showing an agent. It is
/// projected onto the agent's row and <b>never onto the user's detail</b>: it is an unsigned,
/// uncontrolled URL, which is precisely what the definition of done keeps away from a user-facing
/// response. Raised in the C053 handoff as a migration C050 should make.
/// </param>
public sealed record SupportTicket(
    Guid Id,
    Guid UserId,
    string Category,
    string Description,
    Guid? RideId,
    Guid? ScreenshotUploadId,
    string? LegacyScreenshotUrl,
    string Status,
    string? AdminResponse,
    Guid? AssignedTo,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Which back-office pile this belongs to — derived, never stored (see <see cref="TicketQueues"/>).</summary>
    public string Queue => TicketQueues.For(Category);
}

/// <summary>One entry in a ticket's thread (<c>support.ticket_events</c>, migration 1309).</summary>
public sealed record TicketEvent(
    Guid Id,
    Guid TicketId,
    string Kind,
    Guid? ActorId,
    string? ActorRole,
    string? FromStatus,
    string? ToStatus,
    string? Body,
    DateTimeOffset At);

/// <summary>The filters the agent queue is worked with.</summary>
/// <param name="Queue"><c>support</c> | <c>finance</c>, or null for both.</param>
public sealed record TicketQueueFilter(string? Queue, string? Status, string? Category, Guid? AssignedTo);

/// <summary>The <c>(createdAt, id)</c> position a ticket list pages on.</summary>
/// <remarks>
/// A pair rather than the timestamp alone: two tickets written in one transaction share
/// <c>created_at</c> to the microsecond — subscription-svc's refund intake and a user's own raise can
/// land in the same instant — and a timestamp-only cursor would drop whichever straddled a page
/// boundary, silently.
/// </remarks>
public sealed record TicketCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// <c>support.tickets</c> and <c>support.ticket_events</c>.
/// </summary>
/// <remarks>
/// Every state move is a <b>guarded</b> <c>UPDATE</c> bound to the status it was resolved from, so
/// two agents acting at the same instant produce one decision and the loser is told it had already
/// happened rather than overwriting who made it. The pattern safety-svc's report queue uses, for the
/// same reason.
/// </remarks>
public interface ITicketRepository
{
    Task<SupportTicket> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        string category,
        string description,
        Guid? rideId,
        Guid? screenshotUploadId,
        CancellationToken cancellationToken);

    Task<SupportTicket?> FindAsync(Guid ticketId, CancellationToken cancellationToken);

    /// <summary>
    /// The same read, inside a transaction and holding the row.
    /// </summary>
    /// <remarks>
    /// <c>FOR UPDATE</c> because the caller is about to record <c>from_status</c>: without the lock
    /// two agents acting at once could both read <c>OPEN</c>, and the one whose guarded update lost
    /// would have written a thread entry claiming a transition that never happened. The loser blocks
    /// here, wakes to the new status, and its own guard turns it into the <c>409</c> it should be.
    /// </remarks>
    Task<SupportTicket?> FindForUpdateAsync(
        IUnitOfWork unitOfWork, Guid ticketId, CancellationToken cancellationToken);

    /// <summary>One page of a user's own tickets, newest first (<c>ix_tickets_user</c>).</summary>
    Task<IReadOnlyList<SupportTicket>> ListForUserAsync(
        Guid userId, TicketCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the agent queue, <b>oldest first</b> — a queue is worked from its head, and the
    /// complaint that has waited longest is the one that should be answered next.
    /// </summary>
    Task<IReadOnlyList<SupportTicket>> ListQueueAsync(
        TicketQueueFilter filter, TicketCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Assigns a non-terminal ticket and moves <c>OPEN</c> to <c>IN_PROGRESS</c>.
    /// </summary>
    /// <returns>The moved row, or <see langword="null"/> when it is missing or already resolved.</returns>
    Task<SupportTicket?> AssignAsync(
        IUnitOfWork unitOfWork, Guid ticketId, Guid assignTo, CancellationToken cancellationToken);

    /// <summary>Replies to a non-terminal ticket and moves <c>OPEN</c> to <c>IN_PROGRESS</c>.</summary>
    Task<SupportTicket?> RespondAsync(
        IUnitOfWork unitOfWork, Guid ticketId, string response, CancellationToken cancellationToken);

    /// <summary>Resolves a non-terminal ticket.</summary>
    Task<SupportTicket?> ResolveAsync(
        IUnitOfWork unitOfWork, Guid ticketId, string response, Guid? resolvedBy, CancellationToken cancellationToken);

    /// <summary>Appends a thread entry. Always inside the transaction that made the move.</summary>
    Task AppendEventAsync(
        IUnitOfWork unitOfWork,
        Guid ticketId,
        string kind,
        Guid? actorId,
        string? actorRole,
        string? fromStatus,
        string? toStatus,
        string? body,
        CancellationToken cancellationToken);

    /// <summary>A ticket's thread, oldest first.</summary>
    Task<IReadOnlyList<TicketEvent>> ListEventsAsync(
        Guid ticketId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITicketRepository"/>
internal sealed class TicketRepository(INpgsqlConnectionFactory connections) : ITicketRepository
{
    private const string Columns =
        """
        id, user_id, category, description, ride_id, screenshot_upload_id,
        screenshot_url AS legacy_screenshot_url, status, admin_response,
        assigned_to, assigned_at, resolved_at, resolved_by, created_at, updated_at
        """;

    public Task<SupportTicket> CreateAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        string category,
        string description,
        Guid? rideId,
        Guid? screenshotUploadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // `status` is left to the column default (`OPEN`, migration 1303) rather than sent: the
        // default is the schema's statement of where a ticket starts, and naming it here would let
        // the two drift.
        return unitOfWork.Connection.QuerySingleAsync<SupportTicket>(new CommandDefinition(
            $"""
             INSERT INTO support.tickets (user_id, category, description, ride_id, screenshot_upload_id)
             VALUES (@UserId, @Category, @Description, @RideId, @ScreenshotUploadId)
             RETURNING {Columns};
             """,
            new
            {
                UserId = userId,
                Category = category,
                Description = description,
                RideId = rideId,
                ScreenshotUploadId = screenshotUploadId,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<SupportTicket?> FindAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<SupportTicket>(new CommandDefinition(
            $"SELECT {Columns} FROM support.tickets WHERE id = @TicketId;",
            new { TicketId = ticketId },
            cancellationToken: cancellationToken));
    }

    public Task<SupportTicket?> FindForUpdateAsync(
        IUnitOfWork unitOfWork, Guid ticketId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.Connection.QuerySingleOrDefaultAsync<SupportTicket>(new CommandDefinition(
            $"SELECT {Columns} FROM support.tickets WHERE id = @TicketId FOR UPDATE;",
            new { TicketId = ticketId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SupportTicket>> ListForUserAsync(
        Guid userId, TicketCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // The row-value comparison is what makes the composite cursor an index seek rather than a
        // filter over a scan: `(created_at, id) < (@At, @Id)` matches the DESC ordering of
        // `ix_tickets_user` directly.
        var rows = await connection.QueryAsync<SupportTicket>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM support.tickets
              WHERE user_id = @UserId
                AND (@AfterAt::timestamptz IS NULL OR (created_at, id) < (@AfterAt, @AfterId))
              ORDER BY created_at DESC, id DESC
              LIMIT @Limit;
             """,
            new
            {
                UserId = userId,
                AfterAt = after?.CreatedAt,
                AfterId = after?.Id ?? Guid.Empty,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<SupportTicket>> ListQueueAsync(
        TicketQueueFilter filter, TicketCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // The queue predicate is the *derived* routing, expressed in SQL: the Finance categories are
        // passed as an array and the Support queue is its complement, so a row written by
        // subscription-svc's refund intake — which knows nothing about queues — lands on Finance's
        // pile exactly like one raised through this service.
        var rows = await connection.QueryAsync<SupportTicket>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM support.tickets
              WHERE (@Status::text IS NULL OR status = @Status)
                AND (@Category::text IS NULL OR category = @Category)
                AND (@AssignedTo::uuid IS NULL OR assigned_to = @AssignedTo)
                AND (@Queue::text IS NULL
                     OR (@Queue = '{TicketQueues.Finance}' AND category = ANY(@FinanceCategories))
                     OR (@Queue = '{TicketQueues.Support}' AND NOT (category = ANY(@FinanceCategories))))
                AND (@AfterAt::timestamptz IS NULL OR (created_at, id) > (@AfterAt, @AfterId))
              ORDER BY created_at, id
              LIMIT @Limit;
             """,
            new
            {
                filter.Status,
                filter.Category,
                filter.AssignedTo,
                filter.Queue,
                FinanceCategories = TicketQueues.FinanceCategories.ToArray(),
                AfterAt = after?.CreatedAt,
                AfterId = after?.Id ?? Guid.Empty,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<SupportTicket?> AssignAsync(
        IUnitOfWork unitOfWork, Guid ticketId, Guid assignTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // Guarded on "not resolved" rather than on a particular status: taking an already-assigned
        // ticket is legitimate (a hand-off), and taking a resolved one is not — reopening is a
        // decision, not a side effect of picking something up.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<SupportTicket>(new CommandDefinition(
            $"""
             UPDATE support.tickets
                SET assigned_to = @AssignTo,
                    assigned_at = now(),
                    status = CASE WHEN status = '{TicketStatuses.Open}'
                                  THEN '{TicketStatuses.InProgress}' ELSE status END
              WHERE id = @TicketId AND status <> '{TicketStatuses.Resolved}'
             RETURNING {Columns};
             """,
            new { TicketId = ticketId, AssignTo = assignTo },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SupportTicket?> RespondAsync(
        IUnitOfWork unitOfWork, Guid ticketId, string response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        // `admin_response` is the *latest* reply — §13 has one column and admin-bff's `TicketRow`
        // returns it as the current value. Every reply is kept in `support.ticket_events`, which is
        // what the user's thread renders, so overwriting here loses nothing.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<SupportTicket>(new CommandDefinition(
            $"""
             UPDATE support.tickets
                SET admin_response = @Response,
                    status = CASE WHEN status = '{TicketStatuses.Open}'
                                  THEN '{TicketStatuses.InProgress}' ELSE status END
              WHERE id = @TicketId AND status <> '{TicketStatuses.Resolved}'
             RETURNING {Columns};
             """,
            new { TicketId = ticketId, Response = response },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task<SupportTicket?> ResolveAsync(
        IUnitOfWork unitOfWork, Guid ticketId, string response, Guid? resolvedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        // `resolved_at` moves with `status` in one statement, which is what `ck_tickets_resolution`
        // (1309) demands — the two cannot disagree even for the length of a transaction.
        return unitOfWork.Connection.QuerySingleOrDefaultAsync<SupportTicket>(new CommandDefinition(
            $"""
             UPDATE support.tickets
                SET status = '{TicketStatuses.Resolved}',
                    admin_response = @Response,
                    resolved_at = now(),
                    resolved_by = @ResolvedBy
              WHERE id = @TicketId AND status <> '{TicketStatuses.Resolved}'
             RETURNING {Columns};
             """,
            new { TicketId = ticketId, Response = response, ResolvedBy = resolvedBy },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public Task AppendEventAsync(
        IUnitOfWork unitOfWork,
        Guid ticketId,
        string kind,
        Guid? actorId,
        string? actorRole,
        string? fromStatus,
        string? toStatus,
        string? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // `at` is left to the column default — Postgres's transaction timestamp, the same clock
        // `created_at`, `updated_at`, `assigned_at` and `resolved_at` are written from. **Every
        // timestamp on this table comes from the database**, deliberately: a thread entry taken from
        // one replica's clock and a `resolved_at` taken from the database's is how a resolution comes
        // to be stamped before the reply that resolved it, and it is not a hypothetical — an earlier
        // revision of this file did exactly that and the thread sorted out of order.
        //
        // The id is a UUIDv7 rather than the column's `gen_random_uuid()` default, because the thread
        // is read `ORDER BY at, id`: two entries sharing an instant would otherwise be ordered by a
        // random value, and a reply could render above the transition that caused it.
        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO support.ticket_events
              (id, ticket_id, kind, actor_id, actor_role, from_status, to_status, body)
            VALUES (@Id, @TicketId, @Kind, @ActorId, @ActorRole, @FromStatus, @ToStatus, @Body);
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticketId,
                Kind = kind,
                ActorId = actorId,
                ActorRole = actorRole,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                Body = body,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TicketEvent>> ListEventsAsync(
        Guid ticketId, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<TicketEvent>(new CommandDefinition(
            """
            SELECT id, ticket_id, kind, actor_id, actor_role, from_status, to_status, body, at
              FROM support.ticket_events
             WHERE ticket_id = @TicketId
             ORDER BY at, id
             LIMIT @Limit;
            """,
            new { TicketId = ticketId, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
