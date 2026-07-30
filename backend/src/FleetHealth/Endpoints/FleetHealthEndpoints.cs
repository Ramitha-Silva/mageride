using MageRide.FleetHealth.Persistence;
using MageRide.FleetHealth.Rollups;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.FleetHealth.Endpoints;

/// <summary>
/// <c>GET /v1/fleets/{fleetId}/health</c> — <c>backend/contracts/fleet-health.yaml</c>'s only
/// operation (US-3.13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two gates, and they answer different questions.</b> The role gate keeps a passenger's or a
/// driver's token off the surface entirely (AL-06, deny-by-default). The fleet check inside decides
/// whether <i>this</i> organisation's token may read <i>this</i> organisation's fleet — which a role
/// cannot say, because every fleet operator on the platform holds the same one.
/// </para>
/// <para>
/// <b>Any fleet sub-role passes.</b> D3' marks the route "any sub-role", so <c>viewer</c> reads it as
/// well as <c>owner</c>: a health dashboard is the least privileged thing in the Fleet Portal and the
/// people who watch it are not the people who onboard vehicles.
/// </para>
/// <para>
/// <b>A fleet that is not the caller's is 403; a fleet that does not exist is 404.</b> Unusually for this
/// platform — the house rule elsewhere is that "not yours" and "does not exist" must be the same answer
/// so a scoped read cannot be used to enumerate other people's resources. It does not apply here: a
/// fleet operator's own organisation id is in their token, so the only path they can construct is their
/// own, and D3' declares both codes on the operation. Telling them apart is what lets an operator whose
/// token was minted before their org was approved see a useful error.
/// </para>
/// <para>
/// <b>The row filtering is not here.</b> It is the <c>app.fleet_id</c> GUC and the
/// <c>telemetry.device_health_fleet</c> security-barrier view (<see cref="IFleetHealthService"/>), so the
/// check below decides whether the request is answered at all and never which rows come back.
/// </para>
/// </remarks>
public static class FleetHealthEndpoints
{
    public static IEndpointRouteBuilder MapFleetHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var fleets = endpoints.MapGroup("/v1/fleets")
            .WithTags("fleet-health")
            .RequireMageRideRole(
                MageRideRoles.FleetOwner, MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        fleets.MapGet("/{fleetId:guid}/health", GetAsync).WithName("getFleetHealth");

        return endpoints;
    }

    private static async Task<Ok<FleetHealthRollupResponse>> GetAsync(
        Guid fleetId,
        HttpContext context,
        IFleetHealthService health,
        IFleetRollupRepository fleets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(fleets);

        if (!IsAdministrator(context))
        {
            if (!context.User.TryGetFleetScope(out _, out var scopedFleetId))
            {
                // A `fleet_owner` token with no org scope. Either the sign-in predates the org's
                // creation or the claim was dropped — in both cases the caller has no fleet, and
                // answering with anything would mean choosing one for them.
                throw new MageRideException(
                    MageRideErrors.Forbidden,
                    "This token carries no fleet scope. Sign in again to pick up the fleet_id and " +
                    "fleet_role claims (AL-03).");
            }

            if (scopedFleetId != fleetId)
            {
                throw new MageRideException(
                    MageRideErrors.Forbidden, "This token is scoped to a different fleet organisation.");
            }
        }

        if (!await fleets.FleetExistsAsync(fleetId, cancellationToken))
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such fleet organisation.");
        }

        return TypedResults.Ok(FleetHealthResponses.From(await health.ReadAsync(fleetId, cancellationToken)));
    }

    /// <summary>
    /// Whether the caller may read a fleet they do not belong to.
    /// </summary>
    /// <remarks>
    /// The two platform roles AL-06 gives blanket authority, and nothing else — the same rule
    /// provisioning-svc applies on the tracker surface. A <c>verification_officer</c> or a
    /// <c>support_csr</c> is refused by the group's role gate before this is consulted.
    /// </remarks>
    private static bool IsAdministrator(HttpContext context) =>
        context.User.HasRole(MageRideRoles.Admin) || context.User.HasRole(MageRideRoles.SuperAdmin);
}
