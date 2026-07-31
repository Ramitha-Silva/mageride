using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Support.Domain;
using MageRide.Support.Persistence;

namespace MageRide.Support.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/support.yaml. The contract wins over this file: it is what
// C012/C013 generate the KMP client from and what C118 asserts the running service against.
// =============================================================================================

/// <summary>One row of `GET /v1/support/faq`.</summary>
/// <param name="Language">
/// The language actually served, which is not necessarily the one asked for. Present on every item
/// rather than once on the envelope because `FaqSummary` in the contract carries it, and a client
/// rendering a mixed answer needs to know per article which script to lay out.
/// </param>
public sealed record FaqSummaryResponse(Guid ArticleId, string Title, string Category, string Language);

/// <summary>The 200 of `GET /v1/support/faq`.</summary>
public sealed record FaqListResponse(IReadOnlyList<FaqSummaryResponse> Items);

/// <summary>`GET /v1/support/faq/{articleId}` — the summary plus the body.</summary>
public sealed record FaqArticleResponse(
    Guid ArticleId, string Title, string Category, string Language, string Body);

/// <summary>`POST /v1/support/tickets`.</summary>
public sealed record RaiseTicketBody(
    string? Category, string? Description, string? TripId, string? ScreenshotFileId);

/// <summary>The 201 of `POST /v1/support/screenshots`.</summary>
public sealed record UploadedScreenshotResponse(
    Guid FileId, long SizeBytes, string Sha256, DateTimeOffset AutoDeleteAt);

/// <summary>D3' `Ticket` — the summary a list returns.</summary>
public sealed record TicketResponse(
    Guid TicketId,
    string Category,
    string Status,
    string Queue,
    Guid? TripId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt)
{
    public static TicketResponse From(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return new TicketResponse(
            ticket.Id,
            ticket.Category,
            ticket.Status,
            ticket.Queue,
            ticket.RideId,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ResolvedAt);
    }
}

/// <summary>One entry in a ticket's thread.</summary>
/// <remarks>
/// <b>There is no actor id on this record.</b> `actorRole` says a Support CSR answered; naming the
/// individual would put a MageRide employee's identity on a complaint that may be about a person
/// they know. The agent's queue reads the same thread through the same shape, and the id it needs —
/// who a ticket is assigned to — is on <see cref="TicketRowResponse"/> instead.
/// </remarks>
public sealed record TicketEventResponse(
    string Kind,
    DateTimeOffset At,
    string? FromStatus,
    string? ToStatus,
    string? Body,
    string? ActorRole)
{
    public static TicketEventResponse From(TicketEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new TicketEventResponse(
            entry.Kind, entry.At, entry.FromStatus, entry.ToStatus, entry.Body, entry.ActorRole);
    }
}

/// <summary>D3' `TicketDetail` — the ticket, its thread and a signed link to its screenshot.</summary>
/// <param name="ScreenshotUrl">
/// Minted per read and short-lived, so it cannot be cached alongside the ticket and cannot outlive
/// the session that asked for it.
/// </param>
public sealed record TicketDetailResponse(
    Guid TicketId,
    string Category,
    string Status,
    string Queue,
    Guid? TripId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    string Description,
    string? ScreenshotUrl,
    string? AdminResponse,
    IReadOnlyList<TicketEventResponse> Thread);

/// <summary>The agent's view — `TicketDetail` plus who raised it and who is working it.</summary>
public sealed record TicketRowResponse(
    Guid TicketId,
    Guid UserId,
    string Category,
    string Status,
    string Queue,
    Guid? TripId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    string Description,
    string? ScreenshotUrl,
    string? AdminResponse,
    Guid? AssignedTo,
    DateTimeOffset? AssignedAt,
    Guid? ResolvedBy,
    string? LegacyScreenshotUrl,
    IReadOnlyList<TicketEventResponse> Thread);

/// <summary>`POST /v1/internal/support/tickets/{ticketId}/assign`.</summary>
public sealed record AssignTicketBody(string? ActorId, string? ActorRole, string? AssignTo);

/// <summary>`POST /v1/internal/support/tickets/{ticketId}/respond` and `/resolve`.</summary>
public sealed record TicketResponseBody(string? ActorId, string? ActorRole, string? Response);

/// <summary>
/// Parses the ULID/UUID identifiers <c>_shared.yaml</c> types, the way every other service does.
/// </summary>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    /// <summary>
    /// A path <c>{userId}</c>, checked against the token.
    /// </summary>
    /// <remarks>
    /// A malformed id is <c>403</c> rather than <c>400</c>, the call subscription-svc's
    /// <c>SubjectScope</c> makes: whatever it was, it was not the caller's, and answering "that is
    /// not a ULID" for somebody else's identifier is a shape oracle.
    /// </remarks>
    public static Guid RequireSelf(Guid subject, string? requestedUserId)
    {
        var requested = Ulids.TryParse(requestedUserId, out var parsed) ? parsed : Guid.Empty;

        return requested == subject && subject != Guid.Empty
            ? subject
            : throw new MageRideException(MageRideErrors.Forbidden, "These tickets are not yours.");
    }
}

/// <summary>
/// Turns rows into the wire shapes, with the screenshot link minted per read.
/// </summary>
internal static class TicketProjections
{
    public static TicketDetailResponse Detail(
        SupportTicket ticket, IReadOnlyList<TicketEvent> thread, string? screenshotUrl)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(thread);

        return new TicketDetailResponse(
            ticket.Id,
            ticket.Category,
            ticket.Status,
            ticket.Queue,
            ticket.RideId,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ResolvedAt,
            ticket.Description,
            screenshotUrl,
            ticket.AdminResponse,
            [.. thread.Select(TicketEventResponse.From)]);
    }

    public static TicketRowResponse Row(
        SupportTicket ticket, IReadOnlyList<TicketEvent> thread, string? screenshotUrl)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(thread);

        return new TicketRowResponse(
            ticket.Id,
            ticket.UserId,
            ticket.Category,
            ticket.Status,
            ticket.Queue,
            ticket.RideId,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ResolvedAt,
            ticket.Description,
            screenshotUrl,
            ticket.AdminResponse,
            ticket.AssignedTo,
            ticket.AssignedAt,
            ticket.ResolvedBy,
            // Agent-only. fare-svc (C050) writes AL-47's QR-dispute evidence into §13's original
            // `screenshot_url`, and dropping it here would lose the evidence on a Finance-queue
            // ticket. It is absent from `Detail` on purpose: an unsigned URL in front of the user is
            // exactly what this component's definition of done rules out.
            ticket.LegacyScreenshotUrl,
            [.. thread.Select(TicketEventResponse.From)]);
    }
}

/// <summary>
/// Encodes and decodes the opaque <c>cursor</c> both ticket lists page on.
/// </summary>
/// <remarks>
/// Unsigned: it carries only an ordering position, and both queries are scoped by something the
/// caller cannot influence through it — the token's subject on the user list, the queue filter on
/// the agent one. A tampered cursor moves the reader to a different page of rows they could already
/// see.
/// </remarks>
internal static class TicketCursors
{
    private sealed record Position(DateTimeOffset At, Guid Id);

    public static string Encode(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return CursorCodec.Unsigned.Encode(new Position(ticket.CreatedAt, ticket.Id));
    }

    public static TicketCursor? Decode(string? cursor) =>
        CursorCodec.Unsigned.TryDecode<Position>(cursor, out var position) && position is not null
            ? new TicketCursor(position.At, position.Id)
            : null;
}

/// <summary>The queue filters, parsed. An unrecognised value names nothing and is ignored.</summary>
internal static class QueueFilters
{
    public static TicketQueueFilter From(string? queue, string? status, string? category, string? assignedTo) =>
        new(TicketQueues.TryNormalise(queue),
            TicketStatuses.TryNormalise(status),
            string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            RequestIds.Optional(assignedTo));
}
