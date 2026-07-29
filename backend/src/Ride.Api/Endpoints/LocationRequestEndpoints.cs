using MageRide.Ride.Rides;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Ride.Endpoints;

/// <summary>
/// <c>/v1/location-requests</c> — proxy booking's "where are you?" round-trip (P-02, P-13, §11.15).
/// </summary>
/// <remarks>
/// <para>
/// Not under <c>/v1/rides</c>, and not by accident: the request happens <em>before</em> a ride
/// exists, which is the whole reason it exists — the booker does not yet know the pickup, so there
/// is nothing to book. D3' publishes it as its own family for the same reason.
/// </para>
/// <para>
/// <b>Both sides are passengers.</b> The booker and the rider are ordinary accounts, so all four
/// routes carry the passenger role and the ride-level check is per request:
/// <c>LocationRequestService</c> matches the authenticated rider against
/// <c>rides.location_requests.rider_id</c> inside the conditional <c>UPDATE</c>, so one rider can
/// never answer another's prompt.
/// </para>
/// </remarks>
public static class LocationRequestEndpoints
{
    public static IEndpointRouteBuilder MapLocationRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var requests = endpoints.MapGroup("/v1/location-requests")
            .WithTags("location-requests")
            .RequireMageRideRole(MageRideRoles.Passenger);

        requests.MapPost("/", IssueAsync).WithName("createLocationRequest");
        requests.MapGet("/{requestId}", GetAsync).WithName("getLocationRequest");
        requests.MapPost("/{requestId}/confirm", ConfirmAsync).WithName("confirmLocationRequest");
        requests.MapPost("/{requestId}/decline", DeclineAsync).WithName("declineLocationRequest");

        return endpoints;
    }

    private static async Task<IResult> IssueAsync(
        LocationRequestBody? body,
        HttpContext context,
        ILocationRequestService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var request = await service.IssueAsync(
            new IssueLocationRequestCommand(
                BookerId: context.User.RequireSubjectId(),
                RiderPhone: body?.RiderPhone,
                RideDraftId: body?.RideDraftId),
            cancellationToken);

        // 202 for both outcomes. `RiderNotRegistered` is not a failure — AL-45 sends that rider an
        // SMS with a `pickup_confirm` link and the same state machine answers — so a status that
        // said otherwise would have the booker's app show an error for a request that is live.
        return TypedResults.Accepted(
            $"/v1/location-requests/{request.RequestId}", LocationRequestResponse.From(request));
    }

    private static async Task<Ok<LocationRequestResponse>> GetAsync(
        string requestId,
        HttpContext context,
        ILocationRequestService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var request = await service.GetAsync(
            context.User.RequireSubjectId(), RequireRequestId(requestId), cancellationToken);

        return TypedResults.Ok(LocationRequestResponse.From(request));
    }

    private static async Task<Ok<LocationRequestResponse>> ConfirmAsync(
        string requestId,
        ConfirmLocationBody? body,
        HttpContext context,
        ILocationRequestService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var request = await service.ConfirmAsync(
            context.User.RequireSubjectId(),
            RequireRequestId(requestId),
            RequireGeo(body),
            body?.Accuracy,
            cancellationToken);

        return TypedResults.Ok(LocationRequestResponse.From(request));
    }

    /// <summary>
    /// P-02's fence, at the edge: the route takes <b>no body</b>, so there is no parameter a
    /// coordinate could arrive in and no code path that could store one.
    /// </summary>
    private static async Task<Ok<LocationRequestResponse>> DeclineAsync(
        string requestId,
        HttpContext context,
        ILocationRequestService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var request = await service.DeclineAsync(
            context.User.RequireSubjectId(), RequireRequestId(requestId), cancellationToken);

        return TypedResults.Ok(LocationRequestResponse.From(request));
    }

    /// <summary>
    /// A malformed request id is <c>404</c> rather than <c>400</c>, for the same reason a malformed
    /// ride id is: the value is opaque to the client and "not a well-formed handle" tells an
    /// attacker nothing "no such request" would not.
    /// </summary>
    internal static Guid RequireRequestId(string? requestId) =>
        Ulids.TryParse(requestId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, $"No location request '{requestId}'.");

    internal static GeoPoint RequireGeo(ConfirmLocationBody? body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (body?.Lat is not { } lat || double.IsNaN(lat) || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat is required and must be between -90 and 90."];
        }

        if (body?.Lng is not { } lng || double.IsNaN(lng) || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng is required and must be between -180 and 180."];
        }

        if (body?.Accuracy is { } accuracy && (double.IsNaN(accuracy) || accuracy < 0))
        {
            errors["accuracy"] = ["accuracy is in metres and cannot be negative."];
        }

        return errors.Count == 0
            ? new GeoPoint(body!.Lat!.Value, body.Lng!.Value)
            : throw new MageRideValidationException(errors);
    }
}
