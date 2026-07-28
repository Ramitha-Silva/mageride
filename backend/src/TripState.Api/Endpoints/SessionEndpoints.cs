using MageRide.Shared.Auth;
using MageRide.TripState.Configuration;
using MageRide.TripState.Domain;
using MageRide.TripState.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.TripState.Endpoints;

/// <summary>
/// <c>/v1/sessions</c> — <c>backend/contracts/trip-state.yaml</c>'s public surface.
/// </summary>
/// <remarks>
/// <para>
/// The four driver routes demand the <c>driver</c> role; the two rating routes do not. Rating a
/// journey is something a <i>passenger</i> does (US-18.1), and opening the Driver App does not
/// grant the driver role (C020 decision 4) — so those two demand authentication and check
/// participation instead, which is stronger than a role.
/// </para>
/// <para>
/// <b>The path parameter on <c>/active</c> is a vehicle id, not a session id.</b> D3' Part 2 spells
/// it that way and the contract keeps it: the client asking is a driver app at cold start that
/// knows which vehicle it is on and does not yet know whether a session exists.
/// </para>
/// </remarks>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var sessions = endpoints.MapGroup("/v1/sessions").WithTags("sessions");

        var driver = sessions.MapGroup(string.Empty)
            .RequireMageRideRole(MageRideRoles.Driver, MageRideRoles.FleetOwner);

        driver.MapPost("/start", StartAsync).WithName("startSession");
        driver.MapPost("/{sessionId}/end", EndAsync).WithName("endSession");
        driver.MapPost("/{sessionId}/restart", RestartAsync).WithName("restartSession");
        driver.MapGet("/{vehicleId}/active", GetActiveAsync).WithName("getActiveSession");

        // Authenticated, but no role gate — see the remarks above.
        sessions.MapPost("/{sessionId}/rating", RatePassengerJourneyAsync)
            .WithName("ratePassengerJourney")
            .WithTags("ratings");

        sessions.MapPost("/{sessionId}/driver-rating", RateSessionPassengerAsync)
            .WithName("rateSessionPassenger")
            .WithTags("ratings")
            .RequireMageRideRole(MageRideRoles.Driver, MageRideRoles.FleetOwner);

        return endpoints;
    }

    private static async Task<Created<SessionResponse>> StartAsync(
        StartSessionBody? body,
        HttpContext context,
        ISessionService service,
        IOptions<TripStateOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var session = await service.StartAsync(
            new StartSessionCommand(
                context.User.RequireSubjectId(),
                body?.VehicleId,
                body?.Mode,
                body?.RouteId,
                body?.AutoEndAtDestination ?? false),
            cancellationToken);

        return TypedResults.Created(
            $"/v1/sessions/{session.VehicleId}/active", SessionResponse.From(session, options.Value.RestartGrace));
    }

    private static async Task<Ok<SessionResponse>> EndAsync(
        string sessionId,
        HttpContext context,
        ISessionService service,
        IOptions<TripStateOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var session = await service.EndAsync(context.User.RequireSubjectId(), sessionId, cancellationToken);

        return TypedResults.Ok(SessionResponse.From(session, options.Value.RestartGrace));
    }

    private static async Task<Ok<SessionResponse>> RestartAsync(
        string sessionId,
        HttpContext context,
        ISessionService service,
        IOptions<TripStateOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var session = await service.RestartAsync(context.User.RequireSubjectId(), sessionId, cancellationToken);

        return TypedResults.Ok(SessionResponse.From(session, options.Value.RestartGrace));
    }

    /// <summary>
    /// The client's cold-start read. Answers <c>200</c> with <c>null</c> when the vehicle is idle —
    /// not 404, which the contract reserves for a vehicle the caller may not see.
    /// </summary>
    private static async Task<Ok<SessionResponse?>> GetActiveAsync(
        string vehicleId,
        HttpContext context,
        ISessionService service,
        IOptions<TripStateOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var session = await service.GetActiveAsync(context.User.RequireSubjectId(), vehicleId, cancellationToken);

        return TypedResults.Ok<SessionResponse?>(
            session is null ? null : SessionResponse.From(session, options.Value.RestartGrace));
    }

    private static Task<Created<RatingResponse>> RatePassengerJourneyAsync(
        string sessionId,
        RatingBody? body,
        HttpContext context,
        IRatingService service,
        CancellationToken cancellationToken) =>
        RateAsync(sessionId, body, context, service, RatingDirections.PassengerToDriver, cancellationToken);

    private static Task<Created<RatingResponse>> RateSessionPassengerAsync(
        string sessionId,
        RatingBody? body,
        HttpContext context,
        IRatingService service,
        CancellationToken cancellationToken) =>
        RateAsync(sessionId, body, context, service, RatingDirections.DriverToPassenger, cancellationToken);

    private static async Task<Created<RatingResponse>> RateAsync(
        string sessionId,
        RatingBody? body,
        HttpContext context,
        IRatingService service,
        string direction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var rating = await service.RateAsync(
            new RateSessionCommand(
                context.User.RequireSubjectId(), sessionId, body?.Stars, body?.Text, body?.PassengerId, direction),
            cancellationToken);

        return TypedResults.Created($"/v1/sessions/{sessionId}", RatingResponse.From(rating));
    }
}
