using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace MageRide.Shared.Auth;

/// <summary>
/// Requires <paramref name="Needed"/> in <paramref name="Area"/>, evaluated against URD §2.3.
/// </summary>
public sealed record FeaturePermissionRequirement(FeatureArea Area, PermissionGrant Needed) : IAuthorizationRequirement;

/// <summary>
/// The deny-by-default half of AL-06: an endpoint states the feature area and capability it
/// needs, and this decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three ways to be refused, and all of them are refusals.</b> The requirement is never
/// succeeded unless (a) the area is one of the twenty-one URD §2.3 rows, (b) the caller holds a
/// role, and (c) the union of those roles' cells covers every capability asked for. A feature
/// area that was invented at a call site and never transcribed into
/// <see cref="PermissionMatrix"/> therefore answers <c>403</c> — it does not fall through to the
/// authenticated-user fallback, which is exactly the failure mode the C027 fence names.
/// </para>
/// <para>
/// <b>Roles come from the token, not from a query.</b> The access token carries the union
/// (repeated <c>role</c> claims) and the fleet pair, and C026 re-reads the account on every
/// refresh, so a grant made by a Super Admin reaches the caller within one rotation. Reading
/// <c>iam.user_roles</c> here instead would put a database round trip on the authorization path
/// of every privileged request to buy a freshness window measured against a 30-minute token.
/// </para>
/// </remarks>
public sealed class FeatureAuthorizationHandler(IPermissionEvaluator evaluator)
    : AuthorizationHandler<FeaturePermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, FeaturePermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // An area that is not in the catalog is not a permission that can be granted. Denying is
        // the whole point; succeeding here would make a typo an open door.
        if (FeatureAreas.Find(requirement.Area.Key) is not { } area || area != requirement.Area)
        {
            return Task.CompletedTask;
        }

        if (requirement.Needed == PermissionGrant.None)
        {
            return Task.CompletedTask;
        }

        var roles = context.User.Roles();
        if (roles.Count == 0)
        {
            return Task.CompletedTask;
        }

        FleetScope? fleet = context.User.TryGetFleetScope(out var fleetRole, out var fleetId)
            ? new FleetScope(fleetId, fleetRole)
            : null;

        var effective = evaluator.Evaluate(context.User.SubjectId() ?? Guid.Empty, [.. roles], fleet);

        if (effective.Allows(area, requirement.Needed))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class FeatureAuthorizationEndpointExtensions
{
    /// <summary>
    /// Gates an endpoint on a URD §2.3 (feature area, capability) pair.
    /// </summary>
    /// <remarks>
    /// Preferred over <c>RequireMageRideRole</c> wherever the spec has a matrix row, because the
    /// row is the thing that changes: naming <c>super_admin</c> at the call site duplicates a
    /// decision URD §2.3 already made and drifts the day the spec adds a role to the cell.
    /// </remarks>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, FeatureArea area, PermissionGrant needed)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(area);

        return builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new FeaturePermissionRequirement(area, needed)));
    }
}
