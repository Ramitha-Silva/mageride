using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Support.Configuration;
using MageRide.Support.Persistence;
using MageRide.Support.Screenshots;
using MageRide.Support.Tickets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Support.Endpoints;

/// <summary>
/// <c>/v1/internal/support/**</c> — the agent queue admin-bff forwards. **Δ C053.**
/// </summary>
/// <remarks>
/// <para>
/// <b>The same split C052 uses for the vehicle-report queue.</b> <c>admin-bff.yaml</c> declares
/// <c>GET /v1/admin/support/tickets</c> and <c>POST .../{ticketId}/resolve</c>, and the C053 fence
/// says ticket resolution UI is the Admin Portal's and "this service exposes the queue API only".
/// Both hold at once if admin-bff is the RBAC-gated, audited front door — it checks the Support CSR
/// or Finance Officer role (URD §2.3) and writes the D-35 <c>audit.events</c> row — and the decision
/// itself is made here, against the rows this service owns.
/// </para>
/// <para>
/// <b>The deciding agent travels on the body, not on a bearer</b>, because the caller is a service.
/// Recording <i>who</i> answered is what makes a resolution appealable, and it is the half this
/// service contributes to the audit: <c>audit.events</c> is admin-bff's, <c>support.ticket_events</c>
/// is ours, and the two do not overlap — one is the operator's log of every admin action, the other
/// is the conversation the complainant reads.
/// </para>
/// <para>
/// <b>Three routes, because they are three decisions.</b> <c>assign</c> and <c>respond</c> are C053
/// deliverables that no contract had a route for; collapsing <c>respond</c> into <c>resolve</c>
/// would mean an agent asking a clarifying question had to close the ticket to be heard.
/// </para>
/// </remarks>
public static class InternalSupportEndpoints
{
    /// <summary>The guard header, matching every other internal plane on the platform (C008).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalSupportEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var queue = endpoints.MapGroup("/v1/internal/support")
            .WithTags("support-internal")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalKeyFilter(apiKey));

        queue.MapGet("/tickets", ListQueueAsync).WithName("listSupportQueueInternal");
        queue.MapGet("/tickets/{ticketId}", ReadAsync).WithName("getSupportTicketInternal");

        queue.MapPost("/tickets/{ticketId}/assign", AssignAsync).WithName("assignSupportTicketInternal");
        queue.MapPost("/tickets/{ticketId}/respond", RespondAsync).WithName("respondSupportTicketInternal");
        queue.MapPost("/tickets/{ticketId}/resolve", ResolveAsync).WithName("resolveSupportTicketInternal");

        return endpoints;
    }

    private static async Task<Ok<CursorPage<TicketRowResponse>>> ListQueueAsync(
        string? queue,
        string? status,
        string? category,
        string? assignedTo,
        string? cursor,
        int? limit,
        ITicketService tickets,
        IScreenshotLinks links,
        IOptions<SupportOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(options);

        var page = PageRequest.Create(cursor, limit);
        var size = Math.Min(page.Limit, options.Value.MaxPageSize);

        var rows = await tickets.ListQueueAsync(
            QueueFilters.From(queue, status, category, assignedTo),
            TicketCursors.Decode(page.Cursor),
            size + 1,
            cancellationToken);

        // The queue carries no thread: it is a list of what is waiting, and reading every ticket's
        // whole conversation to render one screen would be a query per row. The detail route is one
        // click away and returns it.
        return TypedResults.Ok(
            CursorPage<SupportTicket>.FromOverfetch(rows, size, TicketCursors.Encode)
                .Select(ticket => TicketProjections.Row(ticket, [], ScreenshotLinkFor(ticket, links))));
    }

    private static async Task<Ok<TicketRowResponse>> ReadAsync(
        string ticketId,
        ITicketService tickets,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);

        var detail = await tickets.ReadForAgentAsync(
            RequestIds.Require(ticketId, "ticketId"), cancellationToken);

        return Rendered(detail, links);
    }

    private static async Task<Ok<TicketRowResponse>> AssignAsync(
        string ticketId,
        AssignTicketBody? body,
        ITicketService tickets,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);

        var actor = RequestIds.Require(body?.ActorId, "actorId");

        // An absent `assignTo` means the actor is taking it themselves, which is what "claim the
        // next ticket" is. Naming somebody else is a hand-off and equally legitimate.
        var assignTo = RequestIds.Optional(body?.AssignTo) ?? actor;

        var detail = await tickets.AssignAsync(
            RequestIds.Require(ticketId, "ticketId"),
            assignTo,
            actor,
            NormaliseRole(body?.ActorRole),
            cancellationToken);

        return Rendered(detail, links);
    }

    private static async Task<Ok<TicketRowResponse>> RespondAsync(
        string ticketId,
        TicketResponseBody? body,
        ITicketService tickets,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);

        var detail = await tickets.RespondAsync(
            RequestIds.Require(ticketId, "ticketId"),
            body?.Response ?? string.Empty,
            RequestIds.Require(body?.ActorId, "actorId"),
            NormaliseRole(body?.ActorRole),
            cancellationToken);

        return Rendered(detail, links);
    }

    private static async Task<Ok<TicketRowResponse>> ResolveAsync(
        string ticketId,
        TicketResponseBody? body,
        ITicketService tickets,
        IScreenshotLinks links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(links);

        var detail = await tickets.ResolveAsync(
            RequestIds.Require(ticketId, "ticketId"),
            body?.Response ?? string.Empty,
            RequestIds.Require(body?.ActorId, "actorId"),
            NormaliseRole(body?.ActorRole),
            cancellationToken);

        return Rendered(detail, links);
    }

    private static Ok<TicketRowResponse> Rendered(TicketWithThread detail, IScreenshotLinks links) =>
        TypedResults.Ok(TicketProjections.Row(
            detail.Ticket, detail.Thread, ScreenshotLinkFor(detail.Ticket, links)));

    private static string? ScreenshotLinkFor(SupportTicket ticket, IScreenshotLinks links) =>
        ticket.ScreenshotUploadId is { } upload ? links.Create(upload) : null;

    /// <summary>
    /// The actor's role, as admin-bff resolved it.
    /// </summary>
    /// <remarks>
    /// Taken as free text and merely bounded, deliberately: <c>iam.roles</c> is a table, admin-bff is
    /// the service that knows which of the nine the caller holds, and a whitelist here would have to
    /// be migrated every time that table grew — refusing an audit trail entry because a new role name
    /// was not recognised would lose the record rather than protect it.
    /// </remarks>
    private static string? NormaliseRole(string? role)
    {
        var trimmed = role?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 40)];
    }
}

/// <summary>
/// Rejects a call that does not carry <c>Support:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c>
/// prefix (C008). The comparison is fixed-time — a length-varying compare leaks the key a character
/// at a time.
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(
        apiKey ?? throw new ArgumentNullException(nameof(apiKey)));

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalSupportEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
