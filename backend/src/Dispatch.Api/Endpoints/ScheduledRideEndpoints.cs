using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Scheduling;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Dispatch.Endpoints;

/// <summary>
/// <c>/v1/rides/schedule</c>, <c>/v1/rides/scheduled/{driverId}</c> and <c>/v1/rides/job-board</c>
/// — the advance-booking half of <c>backend/contracts/dispatch.yaml</c> (C035).
/// </summary>
/// <remarks>
/// <para>
/// The passenger books and the driver browses, so the group is split by role rather than by path:
/// <c>POST/DELETE /v1/rides/schedule</c> require <c>passenger</c>, the Job Board and the upcoming
/// list require <c>driver</c>. Deny-by-default does the rest — a driver cannot book a ride for
/// somebody and a passenger cannot read the board.
/// </para>
/// <para>
/// <b>The board is post-intent only.</b> There is no accept route here and there is not meant to
/// be: US-6A.5 has the driver register interest, and the ride reaches them at T-30 min as an
/// ordinary offer on the dispatch screen, which is accepted through ride-svc like every other one.
/// A Job Board "accept" would be a second way to win a ride, with its own race against the first.
/// </para>
/// </remarks>
public static class ScheduledRideEndpoints
{
    public static IEndpointRouteBuilder MapScheduledRideEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var passenger = endpoints.MapGroup("/v1/rides")
            .WithTags("job-board")
            .RequireMageRideRole(MageRideRoles.Passenger);

        passenger.MapPost("/schedule", ScheduleAsync).WithName("scheduleRide");
        passenger.MapDelete("/schedule/{scheduledRideId}", CancelScheduledAsync).WithName("cancelScheduledRide");

        var driver = endpoints.MapGroup("/v1/rides")
            .WithTags("job-board")
            .RequireMageRideRole(MageRideRoles.Driver);

        driver.MapGet("/job-board", JobBoardAsync).WithName("listJobBoard");
        driver.MapPost("/job-board/{rideId}/intent", PostIntentAsync).WithName("postJobBoardIntent");
        driver.MapGet("/scheduled/{driverId}", UpcomingAsync).WithName("listDriverScheduledRides");

        return endpoints;
    }

    private static async Task<Created<ScheduledRideResponse>> ScheduleAsync(
        ScheduleRideBody? body,
        HttpContext context,
        IScheduledRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var row = await service.ScheduleAsync(
            new ScheduleRideCommand(
                PassengerId: context.User.RequireSubjectId(),
                PickupLat: body?.PickupLat,
                PickupLng: body?.PickupLng,
                DestLat: body?.DestLat,
                DestLng: body?.DestLng,
                PickupTime: body?.PickupTime,
                VehicleType: body?.VehicleType,
                PaymentMethod: body?.PaymentMethod),
            cancellationToken);

        return TypedResults.Created($"/v1/rides/schedule/{row.Id}", ScheduledRideResponse.From(row));
    }

    private static async Task<NoContent> CancelScheduledAsync(
        string scheduledRideId,
        HttpContext context,
        IScheduledRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        await service.CancelAsync(
            context.User.RequireSubjectId(), RequireId(scheduledRideId, "scheduledRideId"), cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<CursorPage<ScheduledRideResponse>>> JobBoardAsync(
        double? lat,
        double? lng,
        int? radius,
        HttpContext context,
        IScheduledRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var page = await service.JobBoardAsync(
            context.User.RequireSubjectId(),
            RequireOrigin(lat, lng),
            radius,
            PageRequest.FromQuery(context.Request),
            cancellationToken);

        return TypedResults.Ok(page.Select(static entry => ScheduledRideResponse.From(entry)));
    }

    private static async Task<Ok<JobBoardIntentResponse>> PostIntentAsync(
        string rideId,
        HttpContext context,
        IScheduledRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        // The contract calls the path parameter `rideId` and its own description says what it is:
        // "the `dispatch.scheduled_rides` id shown on the board". Spelled the contract's way rather
        // than corrected, because a generated client is built from the contract.
        var scheduledRideId = RequireId(rideId, "rideId");
        var driverId = context.User.RequireSubjectId();

        var intentId = await service.PostIntentAsync(driverId, scheduledRideId, cancellationToken);

        return TypedResults.Ok(new JobBoardIntentResponse(intentId, scheduledRideId));
    }

    private static async Task<Ok<CursorPage<ScheduledRideResponse>>> UpcomingAsync(
        string driverId,
        HttpContext context,
        IScheduledRideService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var subject = RequireId(driverId, "driverId");

        // A driver reads their own upcoming rides and nobody else's. The route is templated by
        // driver id because D3' prints it that way, not because it is a directory.
        if (subject != context.User.RequireSubjectId())
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "A driver may only read their own upcoming scheduled rides.");
        }

        var page = await service.UpcomingForDriverAsync(
            subject, PageRequest.FromQuery(context.Request), cancellationToken);

        return TypedResults.Ok(page.Select(static row => ScheduledRideResponse.From(row)));
    }

    private static GeoPoint RequireOrigin(double? lat, double? lng)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (lat is not { } latitude || double.IsNaN(latitude) || latitude is < -90 or > 90)
        {
            errors["lat"] = ["lat is required and must be between -90 and 90."];
        }

        if (lng is not { } longitude || double.IsNaN(longitude) || longitude is < -180 or > 180)
        {
            errors["lng"] = ["lng is required and must be between -180 and 180."];
        }

        return errors.Count == 0
            ? new GeoPoint(lat!.Value, lng!.Value)
            : throw new MageRideValidationException(errors);
    }

    internal static Guid RequireId(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });
}

/// <summary>The body of <c>POST /v1/rides/schedule</c>.</summary>
/// <param name="DestLat">
/// AL-36: <b>required</b>. "Select the location to go" is the whole of Δ 2026-06-28 item 2, and a
/// booking without a destination is <c>400</c> before anything is written.
/// </param>
/// <param name="PaymentMethod">
/// Δ C035. Defaults to <c>cash</c>. The materialised ride's <c>payment_method</c> is NOT NULL over
/// a closed set, and choosing for the passenger in the service would take the choice away silently.
/// </param>
public sealed record ScheduleRideBody(
    double? PickupLat,
    double? PickupLng,
    double? DestLat,
    double? DestLng,
    DateTimeOffset? PickupTime,
    string? VehicleType,
    string? PaymentMethod);

/// <summary>The contract's <c>ScheduledRide</c>.</summary>
/// <param name="RideId">
/// <see langword="null"/> until the T-30 sweep materialises it — the one member that tells a client
/// whether this is still a booking or is now a ride.
/// </param>
/// <param name="DistanceM">Present on Job Board reads, where the card shows how far the pickup is.</param>
public sealed record ScheduledRideResponse(
    Guid ScheduledRideId,
    Guid? RideId,
    PlaceResponse Pickup,
    PlaceResponse Dropoff,
    string VehicleType,
    string PaymentMethod,
    DateTimeOffset PickupTime,
    string Status,
    int? DistanceM,
    int? IntentCount,
    bool? HasIntent)
{
    public static ScheduledRideResponse From(ScheduledRideRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ScheduledRideResponse(
            row.Id,
            row.RideId,
            PlaceResponse.From(row.Pickup),
            PlaceResponse.From(row.Dropoff),
            row.VehicleType,
            row.PaymentMethod,
            row.PickupTime,
            row.Status,
            DistanceM: null,
            IntentCount: null,
            HasIntent: null);
    }

    public static ScheduledRideResponse From(JobBoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return From(entry.Ride) with
        {
            DistanceM = (int)Math.Round(entry.DistanceM),
            IntentCount = entry.IntentCount,
            HasIntent = entry.HasIntent,
        };
    }
}

/// <summary>The contract's <c>Place</c> — a coordinate and its optional address.</summary>
/// <remarks>
/// <c>address</c> is never populated: <c>POST /v1/rides/schedule</c> takes bare coordinates, so
/// there is no address to echo. Omitted rather than sent empty, like every other null the platform
/// serialises.
/// </remarks>
public sealed record PlaceResponse(double Lat, double Lng)
{
    public static PlaceResponse From(GeoPoint point) => new(point.Latitude, point.Longitude);
}

/// <summary>The 200 of <c>POST /v1/rides/job-board/{rideId}/intent</c>.</summary>
public sealed record JobBoardIntentResponse(Guid IntentId, Guid ScheduledRideId);
