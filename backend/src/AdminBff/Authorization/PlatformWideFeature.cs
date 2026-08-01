using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Authorization;

namespace MageRide.AdminBff.Authorization;

/// <summary>
/// Requires <paramref name="Needed"/> in <paramref name="Area"/> <b>unscoped</b> — held platform-wide
/// rather than only within the caller's own records.
/// </summary>
public sealed record PlatformWideFeatureRequirement(FeatureArea Area, PermissionGrant Needed) : IAuthorizationRequirement;

/// <summary>
/// The other half of URD §2.3's ◐ cells: a caller who holds a capability only within a scope this
/// surface cannot express does not hold it here.
/// </summary>
/// <remarks>
/// <para>
/// <b>◐ is a fence, and a fence nobody stands on is not a fence.</b> The kernel's
/// <c>PermissionGrant.OwnScope</c> means "allowed, and you must bound it", with the cell's qualifier
/// naming how. On the Moderation row that qualifier is <c>at onboarding</c> for a Verification
/// Officer and <c>temp on reports</c> for a Support CSR — neither of which is "suspend this driver
/// platform-wide", which is the only moderation action this component offers. Treating ◐ as a plain
/// grant would hand both roles the ban button that URD §2.4 reserves to Admin and Super Admin
/// ("limited temporary actions (block on reports)"); ignoring the cell entirely would refuse them
/// the queue they are supposed to work. So the read stays open and the platform-wide write does not.
/// </para>
/// <para>
/// <b>Scope is per capability, and the union is additive.</b> A person who is both a Support CSR and
/// an Admin holds Moderation · Write unscoped from the Admin column, so this succeeds for them — the
/// evaluator answers <c>RequiresOwnScope</c> only when <em>no</em> role grants the capability
/// platform-wide. That is the case the flag exists to get right.
/// </para>
/// <para>
/// <b>What this is not.</b> It is not a second gate on top of the matrix: it is the matrix's own
/// qualifier applied where the qualifier's action does not exist. Cells whose ◐ describes a
/// <em>subset of settings</em> (Admin on Platform config) rather than a subset of records are
/// deliberately left to <c>RequireFeature</c> — the subset there is the endpoint set itself.
/// </para>
/// </remarks>
internal sealed class PlatformWideFeatureHandler(IPermissionEvaluator evaluator)
    : AuthorizationHandler<PlatformWideFeatureRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformWideFeatureRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var roles = context.User.Roles();

        if (roles.Count == 0)
        {
            return Task.CompletedTask;
        }

        var fleet = context.User.TryGetFleetScope(out var fleetRole, out var fleetId)
            ? new FleetScope(fleetId, fleetRole)
            : null;

        var effective = evaluator.Evaluate(context.User.SubjectId() ?? Guid.Empty, [.. roles], fleet);
        var permission = effective.For(requirement.Area);

        if (permission.Satisfies(requirement.Needed) && !permission.RequiresOwnScope(requirement.Needed))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class PlatformWideFeatureEndpointExtensions
{
    /// <summary>
    /// Gates an endpoint on a URD §2.3 pair that must be held <b>platform-wide</b>.
    /// </summary>
    /// <remarks>
    /// Adds the ordinary <see cref="FeaturePermissionRequirement"/> as well, so the route still
    /// states which row it belongs to and <c>RbacMatrixTests</c> can read the pair off the endpoint
    /// rather than being told it.
    /// </remarks>
    public static TBuilder RequirePlatformWideFeature<TBuilder>(
        this TBuilder builder, FeatureArea area, PermissionGrant needed)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(area);

        return builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(
                new FeaturePermissionRequirement(area, needed),
                new PlatformWideFeatureRequirement(area, needed)));
    }
}
