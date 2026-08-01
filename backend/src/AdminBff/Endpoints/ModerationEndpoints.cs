using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Moderation;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-005 — suspensions, the vehicle-report queue and the support-ticket queue
/// (US-14.3, US-12.6, US-16.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two rows of URD §2.3, not one.</b> Suspensions and reports are the <b>Moderation</b> row —
/// Admin ✅, Super Admin ✅, Verification Officer ◐ at onboarding, CSR ◐ temp on reports, Finance ➖,
/// Auditor 👁. Tickets are the <b>Support</b> row, where CSR is ✅ and the Verification Officer is
/// ➖. Gating both families on one area would either hand a CSR the power to ban a driver outright
/// or take the ticket queue away from the role whose whole job it is.
/// </para>
/// <para>
/// <b>The two queues are forwarded, and the two suspensions are not.</b> safety-svc and support-svc
/// each built a <c>/v1/internal/**</c> seam <em>for this BFF</em> and their own files say so, so the
/// decision that changes their rows is made there and admin-bff supplies the RBAC gate and the D-35
/// row. Nothing owns a suspend route, so <see cref="IModerationService"/> writes it here — see
/// <c>ModerationRepository</c>'s remark for why that exception is bounded.
/// </para>
/// </remarks>
internal static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapPost("/vehicles/{vehicleId:guid}/suspend", SuspendVehicleAsync)
            .WithName("suspendVehicle")
            .WithSummary("Remove a vehicle from dispatch and the live map (US-14.3).")
            .RequirePlatformWideFeature(FeatureAreas.Moderation, PermissionGrant.Write)
            .Audited(AdminAuditActions.VehicleSuspended, AdminAuditActions.VehicleEntity);

        admin.MapPost("/drivers/{driverId:guid}/suspend", SuspendDriverAsync)
            .WithName("suspendDriver")
            .WithSummary("Block a driver and end their live session (US-14.3).")
            .RequirePlatformWideFeature(FeatureAreas.Moderation, PermissionGrant.Write)
            .Audited(AdminAuditActions.DriverSuspended, AdminAuditActions.DriverEntity);

        admin.MapGet("/reports/queue", ListReportsAsync)
            .WithName("listReportQueue")
            .WithSummary("Pending vehicle reports (ADD Appendix C).")
            .RequireFeature(FeatureAreas.Moderation, PermissionGrant.Read);

        admin.MapPost("/reports/{reportId:guid}/resolve", ResolveReportAsync)
            .WithName("resolveReport")
            .WithSummary("Confirm or dismiss a report; the third confirmation delists (US-12.6).")
            .RequirePlatformWideFeature(FeatureAreas.Moderation, PermissionGrant.Write)
            .Audited(AdminAuditActions.ReportConfirmed, AdminAuditActions.ReportEntity);

        admin.MapGet("/support/tickets", ListTicketsAsync)
            .WithName("listSupportTicketQueue")
            .WithSummary("The agent ticket queue (US-16.3).")
            .RequireFeature(FeatureAreas.Support, PermissionGrant.Read);

        admin.MapPost("/support/tickets/{ticketId:guid}/resolve", ResolveTicketAsync)
            .WithName("resolveSupportTicket")
            .WithSummary("Answer and close a ticket (US-16.3).")
            .RequirePlatformWideFeature(FeatureAreas.Support, PermissionGrant.Write)
            .Audited(AdminAuditActions.TicketResolved, AdminAuditActions.TicketEntity);

        return admin;
    }

    private static async Task<Ok<ModerationResultResponse>> SuspendVehicleAsync(
        Guid vehicleId,
        ReasonBody? body,
        HttpContext context,
        IModerationService moderation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(moderation);

        var outcome = await moderation.SuspendVehicleAsync(
            vehicleId, RequireReason(body?.Reason), context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new ModerationResultResponse(outcome.SubjectId, outcome.Status, outcome.Reason));
    }

    private static async Task<Ok<ModerationResultResponse>> SuspendDriverAsync(
        Guid driverId,
        ReasonBody? body,
        HttpContext context,
        IModerationService moderation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(moderation);

        var outcome = await moderation.SuspendDriverAsync(
            driverId, RequireReason(body?.Reason), context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new ModerationResultResponse(outcome.SubjectId, outcome.Status, outcome.Reason));
    }

    private static async Task<Ok<CursorPage<ReportRowResponse>>> ListReportsAsync(
        string? cursor,
        int? limit,
        HttpContext context,
        IReportQueue reports,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);

        var page = PageRequest.Create(cursor, limit);

        return TypedResults.Ok(await reports.QueueAsync(page, context, cancellationToken));
    }

    /// <remarks>
    /// The audit action is chosen from the decision rather than taken from the route, because
    /// <c>REPORT_CONFIRMED</c> and <c>REPORT_DISMISSED</c> are what an auditor filters on — a single
    /// <c>REPORT_RESOLVED</c> would make "how many reports were upheld last month" a question that
    /// needs the JSON image parsed to answer.
    /// </remarks>
    private static async Task<Ok<ResolveReportResponse>> ResolveReportAsync(
        Guid reportId,
        ResolveReportBody? body,
        HttpContext context,
        IReportQueue reports,
        IAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(audit);

        var decision = body?.Decision?.Trim().ToUpperInvariant();

        if (decision is not (ReportDecisions.Confirmed or ReportDecisions.Dismissed))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["decision"] = [$"decision must be {ReportDecisions.Confirmed} or {ReportDecisions.Dismissed}."],
            });
        }

        var resolved = await reports.ResolveAsync(
            reportId, decision, body?.Note?.Trim(), context.User.RequireSubjectId(), context, cancellationToken);

        audit.Record(
            reportId,
            after: new
            {
                decision,
                note = body?.Note?.Trim(),
                confirmedCount = resolved.ConfirmedCount,
                vehicleDelisted = resolved.VehicleDelisted,
            },
            action: decision == ReportDecisions.Confirmed
                ? AdminAuditActions.ReportConfirmed
                : AdminAuditActions.ReportDismissed);

        return TypedResults.Ok(resolved);
    }

    private static async Task<Ok<CursorPage<TicketRowResponse>>> ListTicketsAsync(
        string? status,
        string? category,
        string? cursor,
        int? limit,
        HttpContext context,
        ISupportTicketQueue tickets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);

        var page = PageRequest.Create(cursor, limit);

        return TypedResults.Ok(await tickets.QueueAsync(status, category, page, context, cancellationToken));
    }

    private static async Task<Ok<TicketRowResponse>> ResolveTicketAsync(
        Guid ticketId,
        ResolveTicketBody? body,
        HttpContext context,
        ISupportTicketQueue tickets,
        IAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(audit);

        var response = body?.Response?.Trim();

        if (string.IsNullOrEmpty(response))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["response"] = ["response is required — it is shown verbatim to the person who raised the ticket."],
            });
        }

        var resolved = await tickets.ResolveAsync(
            ticketId, response, context.User.RequireSubjectId(), context, cancellationToken);

        audit.Record(ticketId, after: new { status = resolved.Status, response });

        return TypedResults.Ok(resolved);
    }

    private static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            // Not optional and not defaulted. A suspension with no recorded reason is one nobody
            // can appeal and nobody can explain, which is the half of D-35 the audit row cannot
            // supply on its own.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = ["reason is required and is recorded in the audit log."],
            });
        }

        return trimmed;
    }
}

/// <summary>The two verdicts <c>safety.vehicle_reports.status</c> admits for a decided report.</summary>
internal static class ReportDecisions
{
    public const string Confirmed = "CONFIRMED";
    public const string Dismissed = "DISMISSED";
}
