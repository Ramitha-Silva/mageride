using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Persistence;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;

namespace MageRide.AdminBff.Moderation;

/// <summary>US-14.3's two suspensions.</summary>
public interface IModerationService
{
    Task<ModerationOutcome> SuspendVehicleAsync(
        Guid vehicleId, string reason, Guid actorId, CancellationToken cancellationToken);

    Task<ModerationOutcome> SuspendDriverAsync(
        Guid driverId, string reason, Guid actorId, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IModerationService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction, and the audit row is inside it.</b> The suspension and its
/// <c>audit.events</c> row commit together or not at all — the rule reputation-svc and transit-svc
/// are written under, and the reason <see cref="IAdminAuditContext.FlushAsync(IUnitOfWork,
/// CancellationToken)"/> exists. The interceptor then finds nothing left to write and only
/// publishes; a route that forgot to record would still be caught, because the interceptor checks
/// the count rather than the flush.
/// </para>
/// <para>
/// <b>Suspending is idempotent and still audited.</b> Suspending an already-suspended vehicle
/// answers 200 with the same body and writes a row whose before and after agree — which is the
/// honest record of what happened: an admin performed the action, and it changed nothing. Refusing
/// with a 409 instead would make the Admin Portal's "Suspend" button fail on a double click.
/// </para>
/// <para>
/// <b>An in-flight ride is left alone.</b> The contract says so in as many words, and it is the
/// right call: a passenger already in the car is not made safer by the ride vanishing from their
/// phone mid-journey. What stops is the *next* dispatch and the live tracking session.
/// </para>
/// </remarks>
internal sealed class ModerationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IModerationRepository moderation,
    IAdminAuditContext audit,
    TimeProvider clock,
    ILogger<ModerationService> logger) : IModerationService
{
    /// <summary><c>registry.vehicles.dispatch_state</c> (migration 0303).</summary>
    private const string Suspended = "DISPATCH_SUSPENDED";

    /// <summary>The contract's <c>ModerationResult.status</c> enum.</summary>
    private const string SuspendedStatus = "SUSPENDED";

    public async Task<ModerationOutcome> SuspendVehicleAsync(
        Guid vehicleId, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var vehicle = await moderation.LockVehicleAsync(unitOfWork, vehicleId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.VehicleNotFound, "No such vehicle.");

        await moderation.SetDispatchStateAsync(unitOfWork, vehicleId, Suspended, cancellationToken);

        var sessions = await moderation.EndSessionsAsync(
            unitOfWork, vehicleId, driverId: null, now, cancellationToken);

        var presence = await moderation.GoOfflineAsync(
            unitOfWork, vehicleId, driverId: null, cancellationToken);

        audit.Record(
            vehicleId,
            before: new { dispatchState = vehicle.DispatchState, status = vehicle.Status },
            after: new
            {
                dispatchState = Suspended,
                status = vehicle.Status,
                reason,
                sessionsEnded = sessions,
                presenceCleared = presence,
            });

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} ({RegistrationNumber}) suspended by {ActorId}: {Reason}. "
            + "{Sessions} live session(s) ended, presence cleared for {Presence} driver(s).",
            vehicleId, vehicle.RegistrationNumber, actorId, reason, sessions, presence);

        return new ModerationOutcome(vehicleId, SuspendedStatus, reason);
    }

    public async Task<ModerationOutcome> SuspendDriverAsync(
        Guid driverId, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (await moderation.LockDriverAsync(unitOfWork, driverId, cancellationToken) is not { } driver)
        {
            throw new MageRideException(MageRideErrors.DriverNotFound, "No such driver.");
        }

        await moderation.SetBlockedAsync(unitOfWork, driverId, blocked: true, cancellationToken);

        var sessions = await moderation.EndSessionsAsync(
            unitOfWork, vehicleId: null, driverId, now, cancellationToken);

        var presence = await moderation.GoOfflineAsync(
            unitOfWork, vehicleId: null, driverId, cancellationToken);

        var revoked = await moderation.RevokeDriverSessionsAsync(
            unitOfWork, driverId, now, cancellationToken);

        audit.Record(
            driverId,
            before: new { isBlocked = driver.IsBlocked },
            after: new
            {
                isBlocked = true,
                reason,
                sessionsEnded = sessions,
                presenceCleared = presence,
                appSessionsRevoked = revoked,
            });

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Driver {DriverId} suspended by {ActorId}: {Reason}. {Sessions} live session(s) ended, "
            + "{Revoked} app session(s) revoked. Any ride already in flight is left to complete.",
            driverId, actorId, reason, sessions, revoked);

        return new ModerationOutcome(driverId, SuspendedStatus, reason);
    }
}

/// <summary>
/// The vehicle-report queue, forwarded to safety-svc's <c>/v1/internal/safety/reports/**</c> (C052).
/// </summary>
public interface IReportQueue
{
    Task<CursorPage<ReportRowResponse>> QueueAsync(
        PageRequest page, HttpContext context, CancellationToken cancellationToken);

    Task<ResolveReportResponse> ResolveAsync(
        Guid reportId,
        string decision,
        string? note,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IReportQueue"/>
internal sealed class ReportQueue(IAdminUpstream upstream) : IReportQueue
{
    public async Task<CursorPage<ReportRowResponse>> QueueAsync(
        PageRequest page, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = $"/v1/internal/safety/reports/queue?limit={page.Limit}"
                    + (page.Cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(page.Cursor)}");

        using var request = upstream.Request(AdminUpstreams.Safety, HttpMethod.Get, query);

        var answer = await upstream.SendAsync<SafetyQueuePage>(
            AdminUpstreams.Safety, request, context, cancellationToken);

        var items = answer.Items ?? [];

        // safety-svc's internal page carries `{items, cursor}`; the admin contract's envelope also
        // carries `hasMore`, which is derivable and is never inferred from a full page — a page that
        // happens to end on the boundary would claim another one exists.
        return new CursorPage<ReportRowResponse>(
            [.. items.Select(static row => new ReportRowResponse(
                row.ReportId, row.VehicleId, null, row.Reason, row.Status, null, row.CreatedAt))],
            answer.Cursor,
            answer.Cursor is not null);
    }

    public async Task<ResolveReportResponse> ResolveAsync(
        Guid reportId,
        string decision,
        string? note,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var request = upstream.Request(
            AdminUpstreams.Safety, HttpMethod.Post, $"/v1/internal/safety/reports/{reportId:D}/resolve");

        // The deciding admin travels on the body: the callee has no bearer to read, and recording
        // who decided is what makes a delisting appealable (safety-svc's own note).
        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { decision, note, resolvedBy = actorId.ToString() }, options: Shared.Http.MageRideJson.Options);

        var answer = await upstream.SendAsync<SafetyResolveResult>(
            AdminUpstreams.Safety, request, context, cancellationToken);

        return new ResolveReportResponse(answer.ReportId, answer.Status, answer.ConfirmedTotal, answer.Delisted);
    }

    private sealed record SafetyQueuePage(IReadOnlyList<SafetyReportRow>? Items, string? Cursor);

    private sealed record SafetyReportRow(
        Guid ReportId, Guid VehicleId, string? Reason, Guid? TripId, string Status, DateTimeOffset CreatedAt);

    private sealed record SafetyResolveResult(Guid ReportId, string Status, int ConfirmedTotal, bool Delisted);
}

/// <summary>
/// The agent ticket queue, forwarded to support-svc's <c>/v1/internal/support/**</c> (C053).
/// </summary>
public interface ISupportTicketQueue
{
    Task<CursorPage<TicketRowResponse>> QueueAsync(
        string? status, string? category, PageRequest page, HttpContext context, CancellationToken cancellationToken);

    Task<TicketRowResponse> ResolveAsync(
        Guid ticketId, string response, Guid actorId, HttpContext context, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISupportTicketQueue"/>
internal sealed class SupportTicketQueue(IAdminUpstream upstream) : ISupportTicketQueue
{
    public async Task<CursorPage<TicketRowResponse>> QueueAsync(
        string? status, string? category, PageRequest page, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = new List<string> { $"limit={page.Limit}" };

        if (page.Cursor is not null)
        {
            query.Add($"cursor={Uri.EscapeDataString(page.Cursor)}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Uri.EscapeDataString(category)}");
        }

        using var request = upstream.Request(
            AdminUpstreams.Support, HttpMethod.Get, $"/v1/internal/support/tickets?{string.Join('&', query)}");

        var answer = await upstream.SendAsync<CursorPage<SupportTicketRow>>(
            AdminUpstreams.Support, request, context, cancellationToken);

        return new CursorPage<TicketRowResponse>(
            [.. answer.Items.Select(Map)], answer.Cursor, answer.HasMore);
    }

    public async Task<TicketRowResponse> ResolveAsync(
        Guid ticketId, string response, Guid actorId, HttpContext context, CancellationToken cancellationToken)
    {
        using var request = upstream.Request(
            AdminUpstreams.Support, HttpMethod.Post, $"/v1/internal/support/tickets/{ticketId:D}/resolve");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new { response, resolvedBy = actorId.ToString() }, options: Shared.Http.MageRideJson.Options);

        return Map(await upstream.SendAsync<SupportTicketRow>(
            AdminUpstreams.Support, request, context, cancellationToken));
    }

    private static TicketRowResponse Map(SupportTicketRow row) => new(
        row.TicketId,
        row.UserId,
        row.Category ?? string.Empty,
        row.Status ?? string.Empty,
        row.Description,
        row.Response ?? row.AdminResponse,
        row.CreatedAt,
        row.ResolvedAt);

    /// <remarks>
    /// <c>response</c> and <c>adminResponse</c> are both accepted because support-svc's row spells
    /// the column <c>admin_response</c> and the admin contract's <c>TicketRow</c> spells the field
    /// <c>response</c>; reading either keeps this mapping working whichever name the upstream
    /// happens to serialise.
    /// </remarks>
    private sealed record SupportTicketRow(
        Guid TicketId,
        Guid UserId,
        string? Category,
        string? Status,
        string? Description,
        string? Response,
        string? AdminResponse,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt);
}
