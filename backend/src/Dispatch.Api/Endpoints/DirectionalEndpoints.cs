using MageRide.Dispatch.Directional;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Presence;
using MageRide.Shared.Auth;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Dispatch.Endpoints;

/// <summary>
/// <c>POST</c>/<c>GET</c>/<c>DELETE /v1/standby/directional</c> and
/// <c>PUT /v1/admin/dispatch/directional-config</c> — Directional Travel's whole HTTP surface
/// (DT-01, DT-03, DT-08; <c>backend/contracts/dispatch.yaml</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three verbs on one path, and each means something different to the daily budget.</b> The
/// <c>POST</c> spends a use, the <c>GET</c> spends nothing, and the <c>DELETE</c> spends nothing
/// <em>extra</em> — it turns the filter off and leaves the use its activation already consumed
/// (US-6A.19). That is why the turn-off answers with <c>usesRemaining</c>: the number is the same
/// one the driver saw a moment ago, and showing it is what makes the rule visible rather than
/// surprising.
/// </para>
/// <para>
/// <b>The filter is always the caller's own.</b> There is no <c>{driverId}</c> anywhere in this
/// family — the subject is the bearer token, so one driver cannot read or clear another's, and
/// support staff have no route in either (unlike the level and stats reads next door, which they
/// need for a call about missing Job Board rides).
/// </para>
/// </remarks>
public static class DirectionalEndpoints
{
    public static IEndpointRouteBuilder MapDirectionalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var standby = endpoints.MapGroup("/v1/standby/directional")
            .WithTags("standby")
            .RequireMageRideRole(MageRideRoles.Driver);

        standby.MapPost(string.Empty, SetAsync).WithName("setDirectionalFilter");
        standby.MapGet(string.Empty, GetAsync).WithName("getDirectionalFilter");
        standby.MapDelete(string.Empty, ClearAsync).WithName("clearDirectionalFilter");

        endpoints.MapPut("/v1/admin/dispatch/directional-config", UpdateConfigAsync)
            .WithTags("dispatch-admin")
            .WithName("updateDirectionalConfig")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        return endpoints;
    }

    private static async Task<Created<DirectionalFilterResponse>> SetAsync(
        SetDirectionalBody? body,
        HttpContext context,
        IDirectionalService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var state = await service.SetAsync(
            new SetDirectionalCommand(
                DriverId: context.User.RequireSubjectId(),
                Destination: body?.Destination,
                Label: body?.Label),
            cancellationToken);

        // 201 with no Location: the resource is the driver's single live filter and is read back
        // from the collection path itself, which is what GET /v1/standby/directional is.
        return TypedResults.Created(
            "/v1/standby/directional",
            new DirectionalFilterResponse(
                state.Filter!.Id.ToString(),
                state.Filter.ExpiresAt,
                state.UsesRemaining,
                state.MaxDurationSec));
    }

    private static async Task<Ok<DirectionalStateResponse>> GetAsync(
        HttpContext context, IDirectionalService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var state = await service.GetAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(DirectionalStateResponse.From(state));
    }

    private static async Task<Ok<DirectionalClearedResponse>> ClearAsync(
        HttpContext context, IDirectionalService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var state = await service.TurnOffAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(new DirectionalClearedResponse(false, state.UsesRemaining));
    }

    private static async Task<Ok<DirectionalConfigResponse>> UpdateConfigAsync(
        DirectionalConfigBody? body, IDirectionalService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        // Every member optional, like the level-config PUT beside it: an admin changing θ_max should
        // not have to restate the daily-use limit, and a PUT that silently reset the other five
        // would be an odd way to widen an angle.
        var updated = await service.UpdateConfigAsync(
            new DirectionalConfigUpdate(
                body?.ThetaMaxDeg,
                body?.DetourMaxM,
                body?.ProgressMinM,
                body?.MaxUsesPerDay,
                body?.MaxDurationSec,
                body?.ClearOnFirstTrip),
            cancellationToken);

        return TypedResults.Ok(DirectionalConfigResponse.From(updated));
    }
}

/// <summary>The body of <c>POST /v1/standby/directional</c>.</summary>
/// <param name="Label">
/// Optional, ≤ 60 characters — the driver's own name for the destination ("Home"), echoed back on
/// the filter card. Never interpreted: it is a label, not a place.
/// </param>
public sealed record SetDirectionalBody(StandbyPlace? Destination, string? Label);

/// <summary>The 201 of <c>POST /v1/standby/directional</c> (DT-01).</summary>
public sealed record DirectionalFilterResponse(
    string FilterId, DateTimeOffset ExpiresAt, int UsesRemaining, int MaxDurationSec);

/// <summary>The 200 of <c>GET /v1/standby/directional</c> (DT-08).</summary>
/// <param name="TimeRemainingSec">
/// Zero when nothing is active, and never negative — a filter past its deadline that the expiry
/// sweep has not reached yet reads as gone, because that is what it is (the DT-02 predicate applies
/// the same <c>expires_at &gt; now()</c> bound).
/// </param>
public sealed record DirectionalStateResponse(
    bool Active,
    StandbyPlace? Destination,
    string? Label,
    DateTimeOffset? ExpiresAt,
    int TimeRemainingSec,
    int UsesRemaining)
{
    public static DirectionalStateResponse From(DirectionalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new DirectionalStateResponse(
            state.Active,
            state.Filter is { } filter
                ? new StandbyPlace(filter.Destination.Latitude, filter.Destination.Longitude)
                : null,
            state.Filter?.Label,
            state.Filter?.ExpiresAt,
            (int)Math.Max(0d, Math.Floor(state.TimeRemaining.TotalSeconds)),
            state.UsesRemaining);
    }
}

/// <summary>The 200 of <c>DELETE /v1/standby/directional</c>. <c>active</c> is always false.</summary>
public sealed record DirectionalClearedResponse(bool Active, int UsesRemaining);

/// <summary>The body of <c>PUT /v1/admin/dispatch/directional-config</c>.</summary>
public sealed record DirectionalConfigBody(
    int? ThetaMaxDeg,
    int? DetourMaxM,
    int? ProgressMinM,
    int? MaxUsesPerDay,
    int? MaxDurationSec,
    bool? ClearOnFirstTrip);

/// <summary>The contract's <c>DirectionalConfig</c>.</summary>
public sealed record DirectionalConfigResponse(
    int ThetaMaxDeg,
    int DetourMaxM,
    int ProgressMinM,
    int MaxUsesPerDay,
    int MaxDurationSec,
    bool ClearOnFirstTrip)
{
    public static DirectionalConfigResponse From(DirectionalConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new DirectionalConfigResponse(
            row.ThetaMaxDeg, row.DetourMaxM, row.ProgressMinM, row.MaxUsesPerDay, row.MaxDurationSec,
            row.ClearOnFirstTrip);
    }
}
