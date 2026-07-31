using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Support.Configuration;
using MageRide.Support.Domain;
using MageRide.Support.Persistence;
using MageRide.Support.Screenshots;
using Microsoft.Extensions.Options;

namespace MageRide.Support.Tickets;

/// <summary>A ticket and its thread — what both detail routes return.</summary>
public sealed record TicketWithThread(SupportTicket Ticket, IReadOnlyList<TicketEvent> Thread);

/// <summary>What a stored screenshot became.</summary>
public sealed record AttachedScreenshot(Guid FileId, byte[] Sha256, long Bytes, DateTimeOffset AutoDeleteAt);

/// <summary>Epic 16 — raising a ticket, reading your own, and the agent queue behind them.</summary>
public interface ITicketService
{
    Task<AttachedScreenshot> StoreScreenshotAsync(
        Guid ownerId, string? fileName, Stream content, CancellationToken cancellationToken);

    Task<SupportTicket> RaiseAsync(
        Guid userId,
        string category,
        string description,
        Guid? tripId,
        Guid? screenshotFileId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SupportTicket>> ListForUserAsync(
        Guid userId, TicketCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>One of the caller's own tickets, with the part of the thread they may see.</summary>
    Task<TicketWithThread> ReadForUserAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken);

    /// <summary>One ticket with its whole thread, for an agent.</summary>
    Task<TicketWithThread> ReadForAgentAsync(Guid ticketId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SupportTicket>> ListQueueAsync(
        TicketQueueFilter filter, TicketCursor? after, int limit, CancellationToken cancellationToken);

    Task<TicketWithThread> AssignAsync(
        Guid ticketId, Guid assignTo, Guid actorId, string? actorRole, CancellationToken cancellationToken);

    Task<TicketWithThread> RespondAsync(
        Guid ticketId, string response, Guid actorId, string? actorRole, CancellationToken cancellationToken);

    Task<TicketWithThread> ResolveAsync(
        Guid ticketId, string response, Guid actorId, string? actorRole, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="ITicketService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>Every move and the thread entry that records it commit together.</b> A status that changed
/// with no event behind it is a resolution the user cannot see — which is the definition of done
/// this component is measured against — and an event with no status behind it is a thread that
/// describes something that did not happen. One transaction, both rows.
/// </para>
/// <para>
/// <b>A move that changed nothing is told apart from a ticket that does not exist by reading the
/// row.</b> `404` and `409` are different answers: one is a typo, the other is another agent having
/// got there first, and collapsing them would make a race look like a mistake.
/// </para>
/// </remarks>
internal sealed class TicketService(
    IUnitOfWorkFactory unitOfWorkFactory,
    ITicketRepository tickets,
    IUploadRepository uploads,
    IScreenshotStore screenshots,
    IOptions<SupportOptions> options,
    ILogger<TicketService> logger) : ITicketService
{
    private readonly SupportOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    // -------------------------------------------------------------------------------------------
    // Intake
    // -------------------------------------------------------------------------------------------

    public async Task<AttachedScreenshot> StoreScreenshotAsync(
        Guid ownerId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The id is minted here rather than by the INSERT, because it names the file: the bytes are
        // written first, so a crash between the two leaves an orphan file rather than a row pointing
        // at nothing. An orphan is swept by NFR-28's deadline; a dangling pointer is a broken image
        // on a complaint nobody can explain.
        var uploadId = Guid.CreateVersion7();

        var stored = await screenshots.SaveAsync(uploadId, fileName, content, cancellationToken);

        var upload = await uploads.CreateAsync(
            ownerId,
            stored.StorageUrl,
            stored.Sha256,
            SupportUploadKinds.Screenshot,
            _options.ScreenshotRetention,
            cancellationToken);

        logger.LogInformation(
            "Stored a {Bytes}-byte support screenshot for {OwnerId} as upload {UploadId}.",
            stored.Bytes, ownerId, upload.Id);

        return new AttachedScreenshot(upload.Id, stored.Sha256, stored.Bytes, upload.AutoDeleteAt!.Value);
    }

    public async Task<SupportTicket> RaiseAsync(
        Guid userId,
        string category,
        string description,
        Guid? tripId,
        Guid? screenshotFileId,
        CancellationToken cancellationToken)
    {
        if (screenshotFileId is { } fileId)
        {
            await RequireAttachableAsync(userId, fileId, cancellationToken);
        }

        // `tripId` is stored unvalidated, deliberately. Migration 1303 leaves `ride_id` bare of a
        // foreign key because the referent is polymorphic — a Mode C `rides.rides` id or a Mode A/B
        // `trips.sessions` id — and resolving which would mean reading two other bounded contexts on
        // the intake path. A wrong id costs a CSR one lookup; refusing a ticket because a trip could
        // not be resolved costs the platform the complaint.
        SupportTicket ticket;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            ticket = await tickets.CreateAsync(
                unitOfWork, userId, category, description, tripId, screenshotFileId, cancellationToken);

            // The thread starts full rather than empty: without this the user's own complaint is the
            // one entry missing from the record of it, and a ticket nobody has answered yet would
            // render as a blank thread rather than as "you raised this, on this date".
            await tickets.AppendEventAsync(
                unitOfWork,
                ticket.Id,
                TicketEventKinds.Opened,
                userId,
                actorRole: null,
                fromStatus: null,
                toStatus: ticket.Status,
                body: null,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Ticket {TicketId} raised by {UserId} in category {Category}; queue {Queue}.",
            ticket.Id, userId, category, ticket.Queue);

        return ticket;
    }

    /// <summary>
    /// Refuses a screenshot id that is not this user's to attach.
    /// </summary>
    /// <remarks>
    /// Three separate refusals, and all three are <c>validation-failed</c> with the same message on
    /// purpose: telling a caller which of "no such upload", "not yours" and "already on another
    /// ticket" applies turns the route into an oracle over other people's uploads. Silently dropping
    /// the id — the other obvious option — would leave the complainant believing their evidence was
    /// attached when it was not.
    /// </remarks>
    private async Task RequireAttachableAsync(Guid userId, Guid fileId, CancellationToken cancellationToken)
    {
        var upload = await uploads.FindAsync(fileId, cancellationToken);

        var usable = upload is not null
                     && upload.OwnerId == userId
                     && string.Equals(upload.Kind, SupportUploadKinds.Screenshot, StringComparison.Ordinal)
                     && !await uploads.IsAttachedAsync(fileId, cancellationToken);

        if (!usable)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["screenshotFileId"] = ["That is not a screenshot you can attach to a ticket."],
                },
                "The screenshot could not be attached.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Reads
    // -------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<SupportTicket>> ListForUserAsync(
        Guid userId, TicketCursor? after, int limit, CancellationToken cancellationToken) =>
        tickets.ListForUserAsync(userId, after, Math.Clamp(limit, 1, _options.MaxPageSize), cancellationToken);

    public async Task<TicketWithThread> ReadForUserAsync(
        Guid userId, Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken);

        // Scoped to the caller inside the same answer: a ticket that exists but is somebody else's
        // is `404`, not `403`. A `403` would confirm the id names a real complaint, and a ticket id
        // is guessable in exactly the way a complaint should not be.
        if (ticket is null || ticket.UserId != userId)
        {
            throw new MageRideException(MageRideErrors.NotFound, $"No ticket {ticketId}.");
        }

        var thread = await ReadThreadAsync(ticketId, cancellationToken);

        return new TicketWithThread(
            ticket,
            [.. thread.Where(entry => TicketEventKinds.UserVisible.Contains(entry.Kind))]);
    }

    public async Task<TicketWithThread> ReadForAgentAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await tickets.FindAsync(ticketId, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, $"No ticket {ticketId}.");

        return new TicketWithThread(ticket, await ReadThreadAsync(ticketId, cancellationToken));
    }

    public Task<IReadOnlyList<SupportTicket>> ListQueueAsync(
        TicketQueueFilter filter, TicketCursor? after, int limit, CancellationToken cancellationToken) =>
        tickets.ListQueueAsync(filter, after, Math.Clamp(limit, 1, _options.MaxPageSize), cancellationToken);

    private async Task<IReadOnlyList<TicketEvent>> ReadThreadAsync(
        Guid ticketId, CancellationToken cancellationToken)
    {
        var thread = await tickets.ListEventsAsync(ticketId, _options.MaxThreadEvents + 1, cancellationToken);

        if (thread.Count <= _options.MaxThreadEvents)
        {
            return thread;
        }

        logger.LogWarning(
            "Thread for ticket {TicketId} hit Support:MaxThreadEvents ({Max}); the answer is truncated.",
            ticketId, _options.MaxThreadEvents);

        return [.. thread.Take(_options.MaxThreadEvents)];
    }

    // -------------------------------------------------------------------------------------------
    // The agent's three decisions
    // -------------------------------------------------------------------------------------------

    public Task<TicketWithThread> AssignAsync(
        Guid ticketId, Guid assignTo, Guid actorId, string? actorRole, CancellationToken cancellationToken) =>
        MoveAsync(
            ticketId,
            actorId,
            actorRole,
            TicketEventKinds.Assigned,
            body: null,
            (unitOfWork, token) => tickets.AssignAsync(unitOfWork, ticketId, assignTo, token),
            cancellationToken);

    public Task<TicketWithThread> RespondAsync(
        Guid ticketId, string response, Guid actorId, string? actorRole, CancellationToken cancellationToken)
    {
        var body = RequireResponse(response);

        return MoveAsync(
            ticketId,
            actorId,
            actorRole,
            TicketEventKinds.Responded,
            body,
            (unitOfWork, token) => tickets.RespondAsync(unitOfWork, ticketId, body, token),
            cancellationToken);
    }

    public Task<TicketWithThread> ResolveAsync(
        Guid ticketId, string response, Guid actorId, string? actorRole, CancellationToken cancellationToken)
    {
        var body = RequireResponse(response);

        return MoveAsync(
            ticketId,
            actorId,
            actorRole,
            TicketEventKinds.Resolved,
            body,
            (unitOfWork, token) => tickets.ResolveAsync(unitOfWork, ticketId, body, actorId, token),
            cancellationToken);
    }

    /// <summary>
    /// The shape all three decisions share: read the current status, apply a guarded update, record
    /// the transition in the same transaction, and tell a loser apart from a typo.
    /// </summary>
    private async Task<TicketWithThread> MoveAsync(
        Guid ticketId,
        Guid actorId,
        string? actorRole,
        string kind,
        string? body,
        Func<IUnitOfWork, CancellationToken, Task<SupportTicket?>> move,
        CancellationToken cancellationToken)
    {
        SupportTicket moved;
        string? fromStatus;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            // Read inside the transaction the update runs in, holding the row, so `from_status` is
            // the status the update was actually applied to rather than one that was true a moment
            // earlier — see ITicketRepository.FindForUpdateAsync.
            var before = await tickets.FindForUpdateAsync(unitOfWork, ticketId, cancellationToken);
            fromStatus = before?.Status;

            var result = await move(unitOfWork, cancellationToken);

            if (result is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken);

                throw before is null
                    ? new MageRideException(MageRideErrors.NotFound, $"No ticket {ticketId}.")
                    : new MageRideException(
                        MageRideErrors.Conflict,
                        $"Ticket {ticketId} is already {before.Status} and cannot be changed.");
            }

            moved = result;

            await tickets.AppendEventAsync(
                unitOfWork,
                ticketId,
                kind,
                actorId,
                actorRole,
                fromStatus,
                moved.Status,
                body,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Ticket {TicketId} {Kind} by {ActorId}; status {From} to {To}.",
            ticketId, kind, actorId, fromStatus, moved.Status);

        return await ReadForAgentAsync(ticketId, cancellationToken);
    }

    private static string RequireResponse(string? response)
    {
        var trimmed = response?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["response"] = ["A response is required."],
                },
                "An answer the user will read cannot be empty.");
        }

        if (trimmed.Length > 4_000)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["response"] = ["A response is at most 4000 characters."],
                },
                "The response is too long.");
        }

        return trimmed;
    }
}
