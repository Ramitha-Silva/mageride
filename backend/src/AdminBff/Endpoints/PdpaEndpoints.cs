using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Authorization;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Pdpa;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// pdpa-svc's surface, served by admin-bff: the data subject's three routes under
/// <c>/v1/pdpa</c> and the operator's three under <c>/v1/admin/pdpa</c> (E-06, US-1.8).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>/v1/pdpa</c> is the one prefix on this service that is not <c>/v1/admin</c>, and it is a
/// named exception rather than a widening of AL-02.</b> D3' heads the family "pdpa-svc (via
/// admin-bff) — data rights (`/v1/pdpa`)" and marks all three routes <c>Bearer</c>; C008's
/// <c>gateway-routes.json</c> already sends <c>/v1/pdpa/**</c> to this cluster at Order 90; and
/// iam-svc's <c>DELETE /v1/users/me</c> answers <c>202</c> with <c>Location: /v1/pdpa/{requestId}</c>
/// — a URL a passenger's app follows. The platform decided this before C065 existed. What AL-02
/// forbids is a driver-facing or passenger-facing <em>console</em>; three routes by which a person
/// exercises a statutory right over their own record are not one, and
/// <c>AdminBffApplication.GuardTheSurface</c> names the prefix explicitly so a fourth family cannot
/// arrive here by accident.
/// </para>
/// <para>
/// <b>The subject's routes are authenticated and not feature-gated, because URD §2.3 has no cell for
/// them.</b> The End-user-account-management row gives PAX and DRV ➖ — correctly, since it is about
/// operating on <em>other people's</em> accounts. Gating an own-account right on it would refuse
/// every data subject their own data. So these three are <c>RequireAuthorization</c> and are scoped
/// in the handler by the <c>sub</c> claim, which is the same shape as <c>/v1/admin/session</c> and
/// as every <c>/v1/me/**</c> route iam-svc serves.
/// </para>
/// <para>
/// <b>A request that is not yours is a 404, not a 403.</b> The house rule wallet-svc states for
/// credit transfers, and it matters more here: telling the two apart would make this endpoint an
/// oracle over whether a given id is somebody's live erasure request.
/// </para>
/// <para>
/// <b>The subject's own POSTs write an <c>audit.events</c> row.</b> Unusual on this surface —
/// D-35 is about operator decisions — and necessary: a statutory clock that starts when somebody
/// presses a button in the Passenger App needs the press in the same immutable log the fulfilment
/// lands in, or the platform can prove it acted and cannot prove when it was asked.
/// </para>
/// <para>
/// <b>The operator's three are End-user account management, held platform-wide.</b> The row's two ◐
/// cells name who is bounded — <c>◐ verification</c> for the Verification Officer,
/// <c>◐ on tickets</c> for the Support CSR — and neither qualifier is "fulfil an erasure against any
/// account on the platform", which is the only thing these routes do. <c>AdminMenu</c>'s <c>pdpa</c>
/// entry already carries the identical pair and flag, so the nav cannot promise what the API
/// refuses.
/// </para>
/// </remarks>
internal static class PdpaEndpoints
{
    /// <summary>The data subject's prefix. The one route family on this service outside <c>/v1/admin</c>.</summary>
    public const string SubjectPrefix = "/v1/pdpa";

    private const int DefaultQueueRows = 100;
    private const int MaxQueueRows = 500;

    /// <summary>The subject's three routes. Mapped onto the audited group, outside <c>/v1/admin</c>.</summary>
    public static IEndpointRouteBuilder MapPdpaSubjectEndpoints(this IEndpointRouteBuilder pdpa)
    {
        ArgumentNullException.ThrowIfNull(pdpa);

        pdpa.MapPost("/export", RequestExportAsync)
            .WithName("requestDataExport")
            .WithSummary("Ask for a copy of your data (E-06, US-1.8).")
            .RequireAuthorization()
            .Audited(AdminAuditActions.PdpaRequested, AdminAuditActions.PdpaRequestEntity);

        pdpa.MapPost("/erasure", RequestErasureAsync)
            .WithName("requestDataErasure")
            .WithSummary("Ask for your data to be erased (E-06).")
            .RequireAuthorization()
            .Audited(AdminAuditActions.PdpaRequested, AdminAuditActions.PdpaRequestEntity);

        pdpa.MapGet("/{requestId:guid}", GetRequestAsync)
            .WithName("getPdpaRequest")
            .WithSummary("Status, and the signed download once an export is fulfilled.")
            .RequireAuthorization();

        return pdpa;
    }

    /// <summary>The operator's three, on the ordinary <c>/v1/admin</c> group.</summary>
    public static IEndpointRouteBuilder MapPdpaAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/pdpa/queue", ListQueueAsync)
            .WithName("listPdpaQueue")
            .WithSummary("Open data-rights requests by statutory deadline, or the decided history.")
            .RequirePlatformWideFeature(FeatureAreas.AccountManagement, PermissionGrant.Read);

        admin.MapPost("/pdpa/{requestId:guid}/fulfill", FulfilAsync)
            .WithName("fulfillPdpaRequest")
            .WithSummary("Deliver an export, or carry out an erasure honouring the statutory hold list.")
            .RequirePlatformWideFeature(FeatureAreas.AccountManagement, PermissionGrant.Write)
            .Audited(AdminAuditActions.PdpaFulfilled, AdminAuditActions.PdpaRequestEntity);

        admin.MapPost("/pdpa/{requestId:guid}/reject", RejectAsync)
            .WithName("rejectPdpaRequest")
            .WithSummary("Refuse a request, with the reason the subject is shown.")
            .RequirePlatformWideFeature(FeatureAreas.AccountManagement, PermissionGrant.Write)
            .Audited(AdminAuditActions.PdpaRejected, AdminAuditActions.PdpaRequestEntity);

        return admin;
    }

    // ---------------------------------------------------------------------------------------
    // The data subject
    // ---------------------------------------------------------------------------------------

    private static Task<Accepted<PdpaAcceptedResponse>> RequestExportAsync(
        HttpContext context, IPdpaService pdpa, CancellationToken cancellationToken) =>
        AcceptAsync(context, pdpa, PdpaKinds.Export, cancellationToken);

    private static Task<Accepted<PdpaAcceptedResponse>> RequestErasureAsync(
        HttpContext context, IPdpaService pdpa, CancellationToken cancellationToken) =>
        AcceptAsync(context, pdpa, PdpaKinds.Erasure, cancellationToken);

    /// <remarks>
    /// The <c>Location</c> is the status route, which is what iam-svc's <c>DELETE /v1/users/me</c>
    /// already points its own 202 at — so a client that followed one is following the same URL shape
    /// whichever of the two opened the request.
    /// </remarks>
    private static async Task<Accepted<PdpaAcceptedResponse>> AcceptAsync(
        HttpContext context, IPdpaService pdpa, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pdpa);

        var (request, holds) = await pdpa.RequestAsync(
            context.User.RequireSubjectId(), kind, cancellationToken);

        return TypedResults.Accepted(
            $"{SubjectPrefix}/{request.Id:D}",
            new PdpaAcceptedResponse(
                request.Id,
                request.DueBy,
                string.Equals(kind, PdpaKinds.Erasure, StringComparison.Ordinal)
                    ? [.. holds.Select(hold => hold.Code)]
                    : null));
    }

    private static async Task<Ok<PdpaRequestResponse>> GetRequestAsync(
        Guid requestId, HttpContext context, IPdpaService pdpa, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pdpa);

        var view = await pdpa.StatusAsync(requestId, cancellationToken);

        if (view.Request.UserId != context.User.RequireSubjectId())
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such PDPA request.");
        }

        return TypedResults.Ok(Map(view));
    }

    // ---------------------------------------------------------------------------------------
    // The operator
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<PdpaRequestResponse>>> ListQueueAsync(
        string? status, int? limit, IPdpaService pdpa, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdpa);

        var rows = await pdpa.QueueAsync(
            status,
            limit is null or < 1 ? DefaultQueueRows : Math.Min(limit.Value, MaxQueueRows),
            cancellationToken);

        return TypedResults.Ok<IReadOnlyList<PdpaRequestResponse>>([.. rows.Select(Map)]);
    }

    private static async Task<Ok<PdpaRequestResponse>> FulfilAsync(
        Guid requestId,
        FulfilPdpaBody? body,
        HttpContext context,
        IPdpaService pdpa,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pdpa);

        return TypedResults.Ok(Map(await pdpa.FulfilAsync(
            requestId,
            body?.Outcome,
            body?.HoldReason,
            body?.ArtifactUrl,
            context.User.RequireSubjectId(),
            cancellationToken)));
    }

    private static async Task<Ok<PdpaRequestResponse>> RejectAsync(
        Guid requestId,
        ReasonBody? body,
        HttpContext context,
        IPdpaService pdpa,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pdpa);

        return TypedResults.Ok(Map(await pdpa.RejectAsync(
            requestId, body?.Reason ?? string.Empty, context.User.RequireSubjectId(), cancellationToken)));
    }

    private static PdpaRequestResponse Map(PdpaRequestView view) => new(
        view.Request.Id,
        view.Request.Kind,
        view.Request.Status,
        view.Request.UserId,
        view.Request.RequestedAt,
        view.Request.DueBy,
        view.Request.FulfilledAt,
        view.Request.HoldReason,
        view.Request.DecisionReason,
        [.. view.Holds.Select(hold => new PdpaHoldResponse(hold.Code, hold.Blocking, hold.Count))],
        view.DownloadUrl,
        view.DownloadExpiresAt);
}
