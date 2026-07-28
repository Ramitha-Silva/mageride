using MageRide.Dispatch.Presence;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Dispatch.Endpoints;

/// <summary>
/// <c>/v1/standby</c> — the walking skeleton's slice of <c>backend/contracts/dispatch.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two of the contract's routes. Everything else the document declares —
/// <c>POST/GET/DELETE /v1/standby/directional</c> (DT-01..DT-08), the scheduled-ride family, the
/// Job Board and its intents, driver level and stats, the internal no-show report and the two
/// admin configuration puts — is <b>C034/C035/C036</b> and is left unmapped rather than stubbed.
/// A stubbed <c>GET /v1/drivers/{id}/level</c> that always answers 3 is worse than a 404: it
/// reads as a working feature.
/// </para>
/// <para>
/// Both routes require the <c>driver</c> role. Opening the Driver App does not grant it (C020
/// decision 4), so a passenger signed in there carries <c>app=driver, role=passenger</c> and is
/// refused — deny-by-default working as intended.
/// </para>
/// </remarks>
public static class StandbyEndpoints
{
    public static IEndpointRouteBuilder MapStandbyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var standby = endpoints.MapGroup("/v1/standby")
            .WithTags("standby")
            .RequireMageRideRole(MageRideRoles.Driver);

        standby.MapPost("/online", GoOnlineAsync).WithName("goOnline");
        standby.MapPost("/offline", GoOfflineAsync).WithName("goOffline");

        return endpoints;
    }

    private static async Task<Ok<PresenceStateResponse>> GoOnlineAsync(
        GoOnlineBody? body, HttpContext context, IPresenceService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var row = await service.GoOnlineAsync(
            new GoOnlineCommand(
                DriverId: context.User.RequireSubjectId(),
                VehicleId: body?.VehicleId,
                Position: body?.Position,
                DriverHome: body?.DriverHome),
            cancellationToken);

        return TypedResults.Ok(new PresenceStateResponse(row.State));
    }

    private static async Task<Ok<PresenceStateResponse>> GoOfflineAsync(
        HttpContext context, IPresenceService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var row = await service.GoOfflineAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new PresenceStateResponse(row.State));
    }
}

/// <summary>The body of <c>POST /v1/standby/online</c>.</summary>
/// <param name="DriverHome">
/// The D-06 Job Board anchor. Stored but never read here — the 30 km <c>ST_DWithin</c> query it
/// exists for is C035's. Accepted so a client written against the contract is not rejected, and
/// persisted so the anchor is already there when the Job Board arrives.
/// </param>
public sealed record GoOnlineBody(string? VehicleId, StandbyPlace? Position, StandbyPlace? DriverHome);

/// <summary>
/// The 200 of both routes. <c>state</c> is the contract's <c>PresenceState</c> enum
/// (<c>OFFLINE | AVAILABLE | OFFERED | ON_RIDE</c>), which mirrors the
/// <c>dispatch.driver_presence.state</c> CHECK.
/// </summary>
/// <remarks>
/// D3' §route table writes <c>{state:online}</c> for this response. The OpenAPI document is the
/// machine-checkable form and wins (<c>backend/contracts/CLAUDE.md</c>): "online" is not one of
/// the four values <c>PresenceState</c> allows, and a driver who is <c>OFFERED</c> or
/// <c>ON_RIDE</c> has no way to say so in a two-value vocabulary.
/// </remarks>
public sealed record PresenceStateResponse(string State);
