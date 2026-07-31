using System.Security.Cryptography;
using System.Text;
using MageRide.Fleet.Organisation;
using MageRide.Fleet.Vehicles;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Fleet.Endpoints;

/// <summary>
/// <c>/v1/internal/fleets</c> — the fleet-org verification queue and the officer's decision
/// (AL-39, AL-49).
/// </summary>
/// <remarks>
/// <para>
/// D3' §0 puts the whole <c>/v1/internal/**</c> family on service-to-service mTLS and the API
/// gateway refuses the prefix at the edge (C008). Until a mesh exists (C042) the in-cluster hop is
/// guarded by a shared secret; without <c>Fleet:InternalApiKey</c> the family is not mapped at all,
/// so a deployment that forgets it gets 404s rather than an open door — the same shape as
/// registry-svc's, ride-svc's and support-svc's internal routes.
/// </para>
/// <para>
/// <b>Δ C058.</b> <c>fleet.yaml</c> has no internal plane: AL-39 states the officer's routes on
/// admin-bff (<c>GET /v1/admin/verification/queues/fleet-org</c>, <c>…/org/{orgId}</c>,
/// <c>…/{subjectId}/approve|reject</c>) and admin-bff is a BFF — it holds no fleet tables and its
/// own description says approving an org "sets the payout profile to <c>verified</c>", which is a
/// write on <c>registry.fleet_payout_profiles</c>. These four routes are what it forwards to.
/// Raised in the C058 handoff.
/// </para>
/// <para>
/// No route here checks a <c>verification_officer</c> role. It never sees the officer's bearer —
/// admin-bff does, RBAC-gated deny-by-default, and passes the resolved <c>officerId</c> on the
/// body, which is also what <c>audit.events</c> records (D-35). Exactly support-svc's split.
/// </para>
/// </remarks>
public static class InternalFleetEndpoints
{
    /// <summary>Carries <c>Fleet:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalFleetEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service, not a user: there is no bearer to present
        // and the kernel's fallback policy would otherwise 401 every call. The filter is what
        // actually authenticates it.
        var internalFleets = endpoints.MapGroup("/v1/internal/fleets")
            .WithTags("fleets")
            .AllowAnonymous()
            .AddEndpointFilter(new FleetInternalApiKeyFilter(apiKey));

        internalFleets.MapGet("/queue", ListQueueAsync).WithName("listInternalFleetOrgQueue");
        internalFleets.MapGet("/{fleetId}", ReadVerificationAsync).WithName("getInternalFleetVerification");
        internalFleets.MapPost("/{fleetId}/approve", ApproveAsync).WithName("approveInternalFleetOrg");
        internalFleets.MapPost("/{fleetId}/reject", RejectAsync).WithName("rejectInternalFleetOrg");

        // Δ C059 — the officer's decision on one of the org's *vehicles*, and the AL-50 gate.
        //
        // AL-50 states the gate on registry-svc ("registry-svc blocks status='APPROVED' until every
        // required doc is verified"), and registry-svc's approval path is AL-30's four-step Mode C
        // wizard: it refuses a Mode A/B vehicle outright and derives its verdict from
        // registry.onboarding_steps, a table a fleet vehicle has no rows in. So the sentence names a
        // service that structurally cannot hold the gate for the vehicles it is about. These three
        // routes are where it actually lives — same column, same effect, and the same split C058
        // made for the organisation itself. Raised in the C059 handoff.
        internalFleets.MapGet("/{fleetId}/vehicles/{vehicleId}", ReadVehicleAsync)
            .WithName("getInternalFleetVehicleVerification");
        internalFleets.MapPost("/{fleetId}/vehicles/{vehicleId}/approve", ApproveVehicleAsync)
            .WithName("approveInternalFleetVehicle");
        internalFleets.MapPost("/{fleetId}/vehicles/{vehicleId}/reject", RejectVehicleAsync)
            .WithName("rejectInternalFleetVehicle");

        return endpoints;
    }

    private static async Task<Ok<FleetQueueResponse>> ListQueueAsync(
        string? status,
        int? limit,
        IFleetVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var rows = await verification.ListQueueAsync(status?.Trim(), limit, cancellationToken);

        return TypedResults.Ok(new FleetQueueResponse([.. rows.Select(FleetQueueRowResponse.From)]));
    }

    private static async Task<Ok<FleetVerificationResponse>> ReadVerificationAsync(
        string fleetId, IFleetVerificationService verification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var detail = await verification.ReadAsync(
            RequestIds.Require(fleetId, "fleetId"), cancellationToken);

        return TypedResults.Ok(new FleetVerificationResponse(
            FleetResponse.From(detail.Fleet),
            detail.PayoutProfile?.Status,
            detail.PayoutProfile is null ? null : PayoutProfileResponse.From(detail.PayoutProfile),
            [
                .. detail.Documents.Select(document =>
                    new VerificationDocumentResponse(document.Id.ToString(), document.Kind, document.CreatedAt)),
            ]));
    }

    private static async Task<Ok<VerificationDecisionResponse>> ApproveAsync(
        string fleetId,
        VerificationDecisionBody? body,
        IFleetVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var decision = await verification.ApproveAsync(
            RequestIds.Require(fleetId, "fleetId"),
            RequestIds.Require(body?.OfficerId, "officerId"),
            cancellationToken);

        return TypedResults.Ok(VerificationDecisionResponse.From(decision));
    }

    private static async Task<Ok<VerificationDecisionResponse>> RejectAsync(
        string fleetId,
        VerificationDecisionBody? body,
        IFleetVerificationService verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);

        var decision = await verification.RejectAsync(
            RequestIds.Require(fleetId, "fleetId"),
            RequestIds.Require(body?.OfficerId, "officerId"),
            body?.Reason ?? string.Empty,
            cancellationToken);

        return TypedResults.Ok(VerificationDecisionResponse.From(decision));
    }

    // ---------------------------------------------------------------------------------------
    // Δ C059 — one fleet vehicle, and AL-50's approval gate
    // ---------------------------------------------------------------------------------------

    private static async Task<Ok<VehicleDecisionResponse>> ReadVehicleAsync(
        string fleetId,
        string vehicleId,
        IVehicleApprovalService approvals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        var decision = await approvals.ReadAsync(
            RequestIds.Require(fleetId, "fleetId"),
            RequestIds.Require(vehicleId, "vehicleId"),
            cancellationToken);

        return TypedResults.Ok(VehicleDecisionResponse.From(decision));
    }

    private static async Task<Ok<VehicleDecisionResponse>> ApproveVehicleAsync(
        string fleetId,
        string vehicleId,
        VehicleDecisionBody? body,
        IVehicleApprovalService approvals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        // `officerId` is required for the same reason it is on the organisation's approval: this
        // service never sees the officer's bearer, admin-bff resolves it, and it is what
        // `audit.events` records (D-35). It is validated and not stored — `registry.vehicles` has
        // no column for who approved a vehicle, which is registry-svc's schema and not this
        // component's to change. Named in the C059 handoff.
        _ = RequestIds.Require(body?.OfficerId, "officerId");

        var decision = await approvals.ApproveAsync(
            RequestIds.Require(fleetId, "fleetId"),
            RequestIds.Require(vehicleId, "vehicleId"),
            cancellationToken);

        return TypedResults.Ok(VehicleDecisionResponse.From(decision));
    }

    private static async Task<Ok<VehicleDecisionResponse>> RejectVehicleAsync(
        string fleetId,
        string vehicleId,
        VehicleDecisionBody? body,
        IVehicleApprovalService approvals,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        _ = RequestIds.Require(body?.OfficerId, "officerId");

        var decision = await approvals.RejectAsync(
            RequestIds.Require(fleetId, "fleetId"),
            RequestIds.Require(vehicleId, "vehicleId"),
            body?.Reason ?? string.Empty,
            cancellationToken);

        return TypedResults.Ok(VehicleDecisionResponse.From(decision));
    }
}

/// <summary>Refuses a request that does not carry the internal shared secret.</summary>
/// <remarks>
/// Fixed-time comparison: the header is a secret, and an early-exit <c>string ==</c> leaks its
/// prefix to anybody willing to time a few thousand requests. Same shape as registry-svc's and
/// support-svc's filters.
/// </remarks>
internal sealed class FleetInternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalFleetEndpoints.ApiKeyHeader].ToString();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route is service-to-service only (D3' §0).");
        }

        return await next(context);
    }
}
