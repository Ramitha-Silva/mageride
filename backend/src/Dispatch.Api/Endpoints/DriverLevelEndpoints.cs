using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Levels;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Dispatch.Endpoints;

/// <summary>
/// <c>/v1/drivers/{driverId}/level</c> and <c>/stats</c>, and the <c>PUT
/// /v1/admin/drivers/level-config</c> that tunes both (D5' §4, US-6A.6/6A.8/6A.14, US-14.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the driver-facing view of the level, not the dispatch one.</b> The hot path reads it
/// through <c>Reputation.GetDriverLevel</c> over gRPC, which the contract says in as many words;
/// these two routes exist so the driver app can render the badge and the numbers behind it.
/// </para>
/// <para>
/// <b>Who may read.</b> The driver themselves, or a back-office role — a support agent taking a
/// call about "why did I stop getting Job Board rides" needs the same two numbers. Anybody else is
/// <c>403</c>, including another driver.
/// </para>
/// </remarks>
public static class DriverLevelEndpoints
{
    public static IEndpointRouteBuilder MapDriverLevelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var drivers = endpoints.MapGroup("/v1/drivers").WithTags("driver-level");

        drivers.MapGet("/{driverId}/level", GetLevelAsync).WithName("getDriverLevel");
        drivers.MapGet("/{driverId}/stats", GetStatsAsync).WithName("getDriverStats");

        endpoints.MapPut("/v1/admin/drivers/level-config", UpdateConfigAsync)
            .WithTags("dispatch-admin")
            .WithName("updateDriverLevelConfig")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        return endpoints;
    }

    private static async Task<Ok<DriverLevelResponse>> GetLevelAsync(
        string driverId, HttpContext context, IDriverLevelService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var subject = RequireReadable(driverId, context);
        var level = await service.GetLevelAsync(subject, cancellationToken);

        return TypedResults.Ok(
            new DriverLevelResponse(level.Level, level.RatingPoints, level.LevelUpThreshold));
    }

    private static async Task<Ok<DriverStatsResponse>> GetStatsAsync(
        string driverId, HttpContext context, IDriverLevelService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var stats = await service.GetStatsAsync(RequireReadable(driverId, context), cancellationToken);

        return TypedResults.Ok(new DriverStatsResponse(stats.AcceptanceRate, stats.NoShows, stats.Points));
    }

    private static async Task<Ok<LevelConfigResponse>> UpdateConfigAsync(
        LevelConfigBody? body, IDriverLevelService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        // The contract requires only `levelUpThreshold`; the other three keep whatever is live
        // rather than silently resetting to a default, because a PUT that quietly re-enabled the
        // Job Board for Level 1 would be an odd way to change a point threshold.
        var current = await service.GetConfigAsync(cancellationToken);

        var updated = await service.UpdateConfigAsync(
            new LevelConfigRow(
                LevelUpThreshold: body?.LevelUpThreshold ?? current.LevelUpThreshold,
                NoShowPenaltyPoints: body?.NoShowPenaltyPoints ?? current.NoShowPenaltyPoints,
                CancellationPenaltyPoints: body?.CancellationPenaltyPoints ?? current.CancellationPenaltyPoints,
                JobBoardMinLevel: body?.JobBoardMinLevel ?? current.JobBoardMinLevel),
            cancellationToken);

        return TypedResults.Ok(
            new LevelConfigResponse(
                updated.LevelUpThreshold, updated.NoShowPenaltyPoints, updated.CancellationPenaltyPoints,
                updated.JobBoardMinLevel));
    }

    /// <summary>The driver themselves, or a back-office role. Everybody else is 403.</summary>
    private static Guid RequireReadable(string? driverId, HttpContext context)
    {
        var subject = ScheduledRideEndpoints.RequireId(driverId, "driverId");

        if (subject == context.User.RequireSubjectId() || IsInternalStaff(context))
        {
            return subject;
        }

        throw new MageRideException(
            MageRideErrors.Forbidden, "A driver's level and stats are readable by that driver or by support staff.");
    }

    private static bool IsInternalStaff(HttpContext context) =>
        context.User.FindFirst(MageRideClaims.Role)?.Value is { } role && MageRideRoles.Internal.Contains(role);
}

/// <summary>The 200 of <c>GET /v1/drivers/{driverId}/level</c>.</summary>
public sealed record DriverLevelResponse(int Level, int RatingPoints, int LevelUpThreshold);

/// <summary>The 200 of <c>GET /v1/drivers/{driverId}/stats</c> (US-6A.14).</summary>
public sealed record DriverStatsResponse(double AcceptanceRate, int NoShows, int Points);

/// <summary>The body of <c>PUT /v1/admin/drivers/level-config</c>.</summary>
public sealed record LevelConfigBody(
    int? LevelUpThreshold, int? NoShowPenaltyPoints, int? CancellationPenaltyPoints, int? JobBoardMinLevel);

/// <summary>The contract's <c>LevelConfig</c>.</summary>
public sealed record LevelConfigResponse(
    int LevelUpThreshold, int NoShowPenaltyPoints, int CancellationPenaltyPoints, int JobBoardMinLevel);
