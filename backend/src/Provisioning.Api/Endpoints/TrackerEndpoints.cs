using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Provisioning.Endpoints;

/// <summary>
/// <c>/v1/trackers</c> — <c>backend/contracts/provisioning.yaml</c>'s public surface (T-02, T-08).
/// </summary>
/// <remarks>
/// <para>
/// Every route is authenticated and then checks what the caller may do with the *vehicle*, which
/// is stronger than a role: a tracker belongs to a vehicle, and a vehicle belongs either to the
/// driver who owns it or to the fleet whose roster carries it (AL-03). The role gate on the group
/// keeps a passenger's token off the surface entirely; the ownership check inside decides whether
/// this particular driver may act on this particular vehicle.
/// </para>
/// <para>
/// <b><c>DELETE /v1/trackers/{imei}</c> is admin-only and separately mapped.</b> D3''s route table
/// marks it "admin — decommission, revoke ≤60s (US-3.8, T-12)", and a decommission is not a thing
/// an owner does to their own tracker; the owner's verb is the unbind below.
/// </para>
/// </remarks>
public static class TrackerEndpoints
{
    public static IEndpointRouteBuilder MapTrackerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var trackers = endpoints.MapGroup("/v1/trackers")
            .WithTags("trackers")
            .RequireMageRideRole(
                MageRideRoles.Driver, MageRideRoles.FleetOwner, MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        trackers.MapPost("/bind", BindAsync).WithName("bindTracker");

        // ⚠ Not in D3' or in provisioning.yaml as shipped — a C030 micro-change-set, added to the
        // contract in the same change. D6' §4.3 makes `tracker.unbound` half of the IMEI cache's
        // invalidation pair and gives it no producer: the only route that could emit it is
        // `DELETE /v1/trackers/{imei}`, which is an *admin* decommission. An owner moving their
        // tracker from one vehicle to another had no way to release it, and the anti-clone rule
        // would then quarantine the vehicle they moved it to.
        trackers.MapPost("/unbind", UnbindAsync).WithName("unbindTracker");

        trackers.MapGet("/{imei}", GetAsync).WithName("getTracker");
        trackers.MapPost("/{imei}/switch-source", SwitchSourceAsync).WithName("switchTrackerSource");

        trackers.MapDelete("/{imei}", DecommissionAsync)
            .WithName("decommissionTracker")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        return endpoints;
    }

    private static async Task<Created<BindingResponse>> BindAsync(
        BindTrackerBody? body, HttpContext context, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var bound = await service.BindTrackerAsync(
            new BindTrackerCommand(
                context.User.RequireSubjectId(),
                IsAdministrator(context),
                body?.Imei,
                body?.VehicleId,
                body?.Method,
                body?.BindCode,
                body?.CredentialType,
                context.Connection.RemoteIpAddress),
            cancellationToken);

        // Location names the tracker, not the binding: `GET /v1/trackers/{imei}` is the only read
        // route the contract has, and a binding id addresses nothing.
        return TypedResults.Created($"/v1/trackers/{bound.Binding.Imei}", BindingResponse.From(bound));
    }

    private static async Task<NoContent> UnbindAsync(
        UnbindTrackerBody? body, HttpContext context, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        await service.UnbindAsync(
            context.User.RequireSubjectId(), IsAdministrator(context), body?.Imei, cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<TrackerResponse>> GetAsync(
        string imei, HttpContext context, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var detail = await service.GetAsync(
            context.User.RequireSubjectId(), IsAdministrator(context), imei, cancellationToken);

        return TypedResults.Ok(TrackerResponse.From(detail));
    }

    private static async Task<Ok<SwitchSourceResponse>> SwitchSourceAsync(
        string imei,
        SwitchSourceBody? body,
        HttpContext context,
        ITrackerService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var binding = await service.SwitchSourceAsync(
            context.User.RequireSubjectId(), IsAdministrator(context), imei, body?.Source, cancellationToken);

        return TypedResults.Ok(new SwitchSourceResponse(binding.Source ?? PublisherSources.Hardware));
    }

    private static async Task<NoContent> DecommissionAsync(
        string imei, HttpContext context, ITrackerService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        await service.DecommissionAsync(context.User.RequireSubjectId(), imei, cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Whether the caller may act outside the vehicles they own or operate.
    /// </summary>
    /// <remarks>
    /// The two platform roles AL-06 gives blanket authority, and nothing else. A
    /// <c>verification_officer</c> or a <c>support_csr</c> reaching this surface is refused by the
    /// group's role gate before this is consulted.
    /// </remarks>
    internal static bool IsAdministrator(HttpContext context) =>
        context.User.HasRole(MageRideRoles.Admin) || context.User.HasRole(MageRideRoles.SuperAdmin);
}
