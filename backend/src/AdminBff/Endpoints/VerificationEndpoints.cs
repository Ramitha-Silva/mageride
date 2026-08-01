using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Verification;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-003/003a/003b/003c — the verification queues, the subject detail, the per-field decision,
/// the two verdicts and the audited document viewer (AL-39, AL-29, AL-30, AL-49).
/// </summary>
/// <remarks>
/// <para>
/// <b>One URD §2.3 row for the whole family.</b> Verification is Admin ✅, Super Admin ✅,
/// Verification Officer ✅, Support CSR 👁, Auditor 👁, and everybody else ➖ — the only row on the
/// matrix with no ◐ qualifier anywhere in it, which is why every route here uses plain
/// <c>RequireFeature</c>: there is no scope to bound. Reads take <c>Read</c>, which is exactly D3's
/// <c>[verification|admin|support]</c> on the document viewer plus the Auditor's read-only cell;
/// the three decisions take <c>Write</c>, which the two 👁 cells do not satisfy.
/// </para>
/// <para>
/// <b>The viewer is a read that declares an audit action, and that is deliberate.</b>
/// <c>.Audited(DOC_VIEW)</c> on a GET is what <c>AuditEndpointExtensions</c> documents as AL-39's
/// case: the handler records what was opened and the interceptor writes it, exactly as for a
/// mutation. It is the reason the detail's <c>thumbUrl</c>/<c>fullUrl</c> point here rather than at
/// the bucket — see <c>IDocumentLinks</c>.
/// </para>
/// </remarks>
internal static class VerificationEndpoints
{
    public static IEndpointRouteBuilder MapVerificationEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/verification/queues/driving-license", ListDrivingLicenseQueueAsync)
            .WithName("listDrivingLicenseQueue")
            .WithSummary("Drivers awaiting licence verification (SCR-AP-003).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapGet("/verification/queues/vehicle-registration", ListVehicleRegistrationQueueAsync)
            .WithName("listVehicleRegistrationQueue")
            .WithSummary("Vehicles held by at least one flagged field (SCR-AP-003).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapGet("/verification/queues/fleet-org", ListFleetOrgQueueAsync)
            .WithName("listFleetOrgQueue")
            .WithSummary("Fleet organisations awaiting KYC and payout verification (AL-49).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapGet("/verification/queues/driver-payout", ListDriverPayoutQueueAsync)
            .WithName("listDriverPayoutQueue")
            .WithSummary("Drivers awaiting a bank & payout decision (AL-58, AL-59).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        // Before the subject route, so the literal segment is what a two-segment path matches.
        admin.MapGet("/verification/payout/{driverId:guid}", GetDriverPayoutAsync)
            .WithName("getDriverPayoutVerification")
            .WithSummary("A driver's bank details and payout evidence (AL-58, AL-59).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapPost("/verification/payout/{driverId:guid}/approve", ApproveDriverPayoutAsync)
            .WithName("approveDriverPayoutProfile")
            .WithSummary("Verify where a driver's swept earnings go (AL-58).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Write)
            .Audited(AdminAuditActions.PayoutProfileApproved, AdminAuditActions.PayoutProfileEntity);

        admin.MapPost("/verification/payout/{driverId:guid}/reject", RejectDriverPayoutAsync)
            .WithName("rejectDriverPayoutProfile")
            .WithSummary("Refuse a driver's bank details, with a reason (AL-58).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Write)
            .Audited(AdminAuditActions.PayoutProfileRejected, AdminAuditActions.PayoutProfileEntity);

        admin.MapGet("/verification/org/{orgId:guid}", GetOrgVerificationAsync)
            .WithName("getOrgVerification")
            .WithSummary("Fleet-org KYC, payout profile and evidence (SCR-AP-003c).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapGet("/verification/{subjectId:guid}", GetVerificationSubjectAsync)
            .WithName("getVerificationSubject")
            .WithSummary("Flagged fields, documents and the per-step breakdown (SCR-AP-003a).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read);

        admin.MapPut("/verification/{subjectId:guid}/fields/{fieldKey}", DecideFieldAsync)
            .WithName("decideVerificationField")
            .WithSummary("Confirm a flagged field, or edit and confirm it (US-2.4a/2.10a).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Write)
            .Audited(AdminAuditActions.FieldConfirmed);

        admin.MapPost("/verification/{subjectId:guid}/approve", ApproveAsync)
            .WithName("approveVerificationSubject")
            .WithSummary("Approve a driver, vehicle or fleet organisation (US-2.9).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Write)
            .Audited(AdminAuditActions.VerificationApproved);

        admin.MapPost("/verification/{subjectId:guid}/reject", RejectAsync)
            .WithName("rejectVerificationSubject")
            .WithSummary("Refuse a driver, vehicle or fleet organisation, with a reason (US-2.15).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Write)
            .Audited(AdminAuditActions.VerificationRejected);

        admin.MapGet("/documents/{docId:guid}", ViewDocumentAsync)
            .WithName("viewDocument")
            .WithSummary("Open a document in the full-size viewer (SCR-AP-003b, US-24.8).")
            .RequireFeature(FeatureAreas.Verification, PermissionGrant.Read)
            .Audited(AdminAuditActions.DocumentViewed, AdminAuditActions.DocumentEntity);

        return admin;
    }

    private static async Task<Ok<CursorPage<DriverQueueRowResponse>>> ListDrivingLicenseQueueAsync(
        string? search,
        string? status,
        string? cursor,
        int? limit,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.DriverQueueAsync(
            search, status, PageRequest.Create(cursor, limit), cancellationToken));
    }

    private static async Task<Ok<CursorPage<VehicleQueueRowResponse>>> ListVehicleRegistrationQueueAsync(
        string? search,
        string? status,
        string? cursor,
        int? limit,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.VehicleQueueAsync(
            search, status, PageRequest.Create(cursor, limit), cancellationToken));
    }

    private static async Task<Ok<CursorPage<OrgQueueRowResponse>>> ListFleetOrgQueueAsync(
        string? search,
        string? status,
        string? cursor,
        int? limit,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.OrgQueueAsync(
            search, status, PageRequest.Create(cursor, limit), context, cancellationToken));
    }

    private static async Task<Ok<CursorPage<DriverPayoutQueueRowResponse>>> ListDriverPayoutQueueAsync(
        string? search,
        string? status,
        string? cursor,
        int? limit,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.DriverPayoutQueueAsync(
            search, status, PageRequest.Create(cursor, limit), cancellationToken));
    }

    private static async Task<Ok<DriverPayoutVerificationResponse>> GetDriverPayoutAsync(
        Guid driverId,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.DriverPayoutDetailAsync(driverId, cancellationToken));
    }

    /// <remarks>
    /// <b>Not the same decision as <c>POST /verification/{driverId}/approve</c>, and deliberately
    /// not the same route.</b> That one is the driver's identity — their licence and their NIC. This
    /// one authorises money to be sent to a bank account. The ADD (§1.18 AL-58) describes the
    /// officer deciding this "through the existing AL-39 queue, whose subject-agnostic routes
    /// already take a driver id": the queue family is reused, but sharing the verdict route would
    /// have made one button decide two unrelated questions — and a refusal aimed at an illegible
    /// bank statement would have refused the driver's licence and stopped them driving. Raised as a
    /// micro-change-set.
    /// </remarks>
    private static async Task<Ok<DriverPayoutDecisionResponse>> ApproveDriverPayoutAsync(
        Guid driverId,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.ApproveDriverPayoutAsync(
            driverId, context.User.RequireSubjectId(), context, cancellationToken));
    }

    private static async Task<Ok<DriverPayoutDecisionResponse>> RejectDriverPayoutAsync(
        Guid driverId,
        ReasonBody? body,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        var reason = body?.Reason?.Trim();

        if (string.IsNullOrEmpty(reason) || reason.Length > 1000)
        {
            // Shown verbatim on SCR-DA-022a. "Rejected" with nothing to read leaves a driver unable
            // to fix the one thing standing between them and being paid.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] =
                [
                    "reason is required, must be at most 1000 characters, and is shown verbatim to the driver.",
                ],
            });
        }

        return TypedResults.Ok(await verification.RejectDriverPayoutAsync(
            driverId, reason, context.User.RequireSubjectId(), context, cancellationToken));
    }

    private static async Task<Ok<VerificationDetailResponse>> GetVerificationSubjectAsync(
        Guid subjectId,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.DetailAsync(subjectId, context, cancellationToken));
    }

    private static async Task<Ok<OrgVerificationResponse>> GetOrgVerificationAsync(
        Guid orgId,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.OrgDetailAsync(orgId, context, cancellationToken));
    }

    /// <remarks>
    /// One PUT for both of D3' Part 2's superseded routes: an absent <c>value</c> is
    /// "confirm as is", a present one is "edit and confirm". The distinction is null-versus-absent
    /// and not empty-versus-set — clearing a field to the empty string is not a correction anybody
    /// makes, and treating <c>""</c> as an edit would blank a licence number on a mis-click.
    /// </remarks>
    private static async Task<Ok<DecideFieldResponse>> DecideFieldAsync(
        Guid subjectId,
        string fieldKey,
        DecideFieldBody? body,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        var key = fieldKey?.Trim();

        if (string.IsNullOrEmpty(key) || key.Length > 60)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["fieldKey"] = ["fieldKey is required and must be at most 60 characters."],
            });
        }

        var value = body?.Value;

        if (value is { Length: > 500 })
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["value"] = ["value must be at most 500 characters."],
            });
        }

        return TypedResults.Ok(await verification.DecideFieldAsync(
            subjectId,
            key,
            string.IsNullOrWhiteSpace(value) ? null : value,
            context.User.RequireSubjectId(),
            context,
            cancellationToken));
    }

    private static async Task<Ok<VerificationDecisionResponse>> ApproveAsync(
        Guid subjectId,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        return TypedResults.Ok(await verification.ApproveAsync(
            subjectId, context.User.RequireSubjectId(), context, cancellationToken));
    }

    private static async Task<Ok<VerificationDecisionResponse>> RejectAsync(
        Guid subjectId,
        ReasonBody? body,
        HttpContext context,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verification);

        var reason = body?.Reason?.Trim();

        if (string.IsNullOrEmpty(reason) || reason.Length > 1000)
        {
            // US-2.15: the reason is shown verbatim to the applicant. A refusal with nothing to read
            // is a screen that says "rejected" and gives the driver no way to fix it.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] =
                [
                    "reason is required, must be at most 1000 characters, and is shown verbatim to the applicant.",
                ],
            });
        }

        return TypedResults.Ok(await verification.RejectAsync(
            subjectId, reason, context.User.RequireSubjectId(), context, cancellationToken));
    }

    /// <remarks>
    /// <b>302, and the signed URL is minted here.</b> The contract types this as a redirect to a
    /// short-lived signed object-storage URL, and answering it from a route that has already
    /// recorded the <c>DOC_VIEW</c> row is what makes AL-39's two obligations hold together — a
    /// pre-signed link handed out earlier would be a fetch nobody records.
    /// </remarks>
    private static async Task<RedirectHttpResult> ViewDocumentAsync(
        Guid docId,
        string? variant,
        IVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var view = await verification.ViewDocumentAsync(docId, variant, cancellationToken);

        return TypedResults.Redirect(view.SignedUrl, permanent: false, preserveMethod: false);
    }
}
