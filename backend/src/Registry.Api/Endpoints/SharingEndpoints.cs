using MageRide.Registry.Sharing;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// Mode B sharing — <c>/v1/vehicles/{vehicleId}/share*</c>, <c>/subscribers*</c> and
/// <c>/v1/share-requests</c> (D-22, D-23; US-4.1–4.7, US-NEW.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two different populations, so two different role gates.</b> Granting, revoking and reading
/// a roster are things a vehicle's owner does, and every owner in this service is a driver — so
/// those sit under the <c>driver</c> role like the rest of <see cref="VehicleEndpoints"/>.
/// Accepting a grant, unsubscribing and asking for access are things the <em>other</em> party
/// does, and that party is a passenger (US-4.5's map tap, US-NEW.1's My Subscriptions screen) or
/// another driver (US-4.1 shares "to any driver app user"). Those routes therefore require
/// authentication and no particular role; the resource check is ownership or grantee identity,
/// which is stronger than a role would be.
/// </para>
/// <para>
/// <c>POST /v1/vehicles/{id}/device</c> is <b>not</b> here. The contract makes it a thin wrapper
/// over provisioning-svc's <c>POST /v1/trackers/bind</c> (T-02), where the credential mint and
/// the anti-clone quarantine live; that service is C030. A wrapper over nothing would answer 201
/// to a driver whose tracker was never bound.
/// </para>
/// </remarks>
public static class SharingEndpoints
{
    public static IEndpointRouteBuilder MapSharingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var owner = endpoints.MapGroup("/v1/vehicles")
            .WithTags("sharing")
            .RequireMageRideRole(MageRideRoles.Driver);

        owner.MapPost("/{vehicleId}/share", GrantAsync).WithName("createShareGrant");
        owner.MapDelete("/{vehicleId}/share/{grantId}", RevokeAsync).WithName("revokeShareGrant");
        owner.MapGet("/{vehicleId}/subscribers", ListSubscribersAsync).WithName("listVehicleSubscribers");

        var counterparty = endpoints.MapGroup("/v1/vehicles")
            .WithTags("sharing")
            .RequireAuthorization();

        counterparty.MapPost("/{vehicleId}/share/{grantId}/accept", AcceptAsync).WithName("acceptShareGrant");
        counterparty.MapDelete("/{vehicleId}/subscribers/{userId}", UnsubscribeAsync)
            .WithName("unsubscribeFromVehicle");

        endpoints.MapPost("/v1/share-requests", RequestAccessAsync)
            .WithTags("sharing")
            .WithName("requestVehicleAccess")
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<Created<CreateShareResponse>> GrantAsync(
        string vehicleId,
        CreateShareBody? body,
        HttpContext context,
        IShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var id = VehicleEndpoints.RequireVehicleId(vehicleId);

        var grant = await shares.GrantAsync(
            new GrantShareCommand(context.User.RequireSubjectId(), id, body?.UserId, body?.ExpiresAt),
            cancellationToken);

        return TypedResults.Created($"/v1/vehicles/{id}/share/{grant.Id}", CreateShareResponse.From(grant));
    }

    private static async Task<Ok<AcceptShareResponse>> AcceptAsync(
        string vehicleId,
        string grantId,
        HttpContext context,
        IShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var accepted = await shares.AcceptAsync(
            context.User.RequireSubjectId(),
            VehicleEndpoints.RequireVehicleId(vehicleId),
            RequireGrantId(grantId),
            cancellationToken);

        return TypedResults.Ok(AcceptShareResponse.From(accepted));
    }

    private static async Task<NoContent> RevokeAsync(
        string vehicleId,
        string grantId,
        HttpContext context,
        IShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        await shares.RevokeAsync(
            context.User.RequireSubjectId(),
            VehicleEndpoints.RequireVehicleId(vehicleId),
            RequireGrantId(grantId),
            cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<SubscriberPageResponse>> ListSubscribersAsync(
        string vehicleId,
        HttpContext context,
        IShareService shares,
        CancellationToken cancellationToken,
        string? cursor = null,
        int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var page = await shares.ListSubscribersAsync(
            context.User.RequireSubjectId(),
            VehicleEndpoints.RequireVehicleId(vehicleId),
            cursor,
            limit,
            cancellationToken);

        return TypedResults.Ok(SubscriberPageResponse.From(page));
    }

    private static async Task<NoContent> UnsubscribeAsync(
        string vehicleId,
        string userId,
        HttpContext context,
        IShareService shares,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        await shares.UnsubscribeAsync(
            context.User.RequireSubjectId(),
            VehicleEndpoints.RequireVehicleId(vehicleId),
            RequireGrantId(userId),
            cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Created<ShareRequestResponse>> RequestAccessAsync(
        ShareRequestBody? body, HttpContext context, IShareService shares, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shares);

        var request = await shares.RequestAccessAsync(
            context.User.RequireSubjectId(), body?.VehicleId, cancellationToken);

        return TypedResults.Created($"/v1/share-requests/{request.Id}", ShareRequestResponse.From(request));
    }

    /// <summary>
    /// Parses a grant or user id out of the path. 404 rather than 400, for the same reason
    /// <see cref="VehicleEndpoints.RequireVehicleId"/> is: the contract types both as an opaque
    /// ULID-or-UUID, so "not well-formed" and "no such row" are the same answer to a caller.
    /// </summary>
    private static Guid RequireGrantId(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRide.Shared.Errors.MageRideException(
                MageRide.Shared.Errors.MageRideErrors.NotFound, $"No such record '{value}'.");
}
