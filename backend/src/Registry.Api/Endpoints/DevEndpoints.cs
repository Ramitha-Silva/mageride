using MageRide.Registry.Vehicles;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// <c>/v1/dev</c> — the seed path this component's scope calls for: "mark it APPROVED through a
/// seed/dev path", with the Verification-Officer queue skipped entirely.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not approval.</b> Real approval is AL-30's auto-approve once all four onboarding
/// steps come back VERIFIED, gated by AL-10's mandatory insurance document — neither of which
/// exists in this slice, because C021 is fenced out of upload and OCR. C029 owns both. This
/// endpoint flips the status so the skeleton has an approved vehicle to dispatch against, and it
/// says so in its own name.
/// </para>
/// <para>
/// It is under <c>/v1/dev</c> rather than beside the real routes so that a deployment can block
/// the whole prefix at the edge, and it is <b>not mapped at all</b> unless
/// <c>Registry:DevApprovalEnabled</c> resolves true (Development by default). An unmapped route
/// answers 404, so nothing about it is discoverable where it is off.
/// </para>
/// <para>
/// It still requires a driver bearer token and still refuses somebody else's vehicle. A seed
/// path that skipped authentication would be the one thing in the service an attacker could
/// reach without a session.
/// </para>
/// </remarks>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevSeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGroup("/v1/dev/vehicles")
            .WithTags("dev")
            .RequireMageRideRole(MageRideRoles.Driver)
            .MapPost("/{vehicleId}/approve", ApproveAsync)
            .WithName("devApproveVehicle");

        return endpoints;
    }

    private static async Task<Ok<ApproveVehicleResponse>> ApproveAsync(
        string vehicleId, HttpContext context, IVehicleService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var vehicle = await service.ApproveAsync(
            context.User.RequireSubjectId(), VehicleEndpoints.RequireVehicleId(vehicleId), cancellationToken);

        return TypedResults.Ok(ApproveVehicleResponse.From(vehicle));
    }
}
