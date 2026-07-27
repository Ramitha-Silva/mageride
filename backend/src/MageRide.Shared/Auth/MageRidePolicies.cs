using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace MageRide.Shared.Auth;

/// <summary>
/// Named authorization policies (AL-06 deny-by-default RBAC, AL-03 fleet sub-roles).
/// </summary>
/// <remarks>
/// Deny-by-default has two halves and both are wired by <c>AddMageRideAuthorization</c>: a
/// fallback policy so an endpoint with no <c>[Authorize]</c> still demands authentication, and
/// per-role policies so an endpoint states the role it needs rather than inspecting claims.
/// </remarks>
public static class MageRidePolicies
{
    /// <summary>Policy name for a canonical role, e.g. <c>role:admin</c>.</summary>
    public static string Role(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        if (!MageRideRoles.IsKnown(role))
        {
            throw new ArgumentException($"'{role}' is not one of the nine canonical roles (AL-06).", nameof(role));
        }

        return $"role:{role}";
    }

    /// <summary>
    /// Policy name for a minimum fleet sub-role, e.g. <c>fleet:manager</c>. Ranks are inclusive:
    /// an owner satisfies <c>fleet:manager</c> and <c>fleet:viewer</c>.
    /// </summary>
    public static string FleetRole(string fleetRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRole);

        if (!FleetRoles.All.Contains(fleetRole))
        {
            throw new ArgumentException($"'{fleetRole}' is not a fleet sub-role (AL-03).", nameof(fleetRole));
        }

        return $"fleet:{fleetRole}";
    }

    public static readonly string Passenger = Role(MageRideRoles.Passenger);
    public static readonly string Driver = Role(MageRideRoles.Driver);
    public static readonly string FleetOwner = Role(MageRideRoles.FleetOwner);
    public static readonly string Admin = Role(MageRideRoles.Admin);
    public static readonly string SuperAdmin = Role(MageRideRoles.SuperAdmin);
    public static readonly string VerificationOfficer = Role(MageRideRoles.VerificationOfficer);
    public static readonly string SupportCsr = Role(MageRideRoles.SupportCsr);
    public static readonly string FinanceOfficer = Role(MageRideRoles.FinanceOfficer);
    public static readonly string Auditor = Role(MageRideRoles.Auditor);

    /// <summary>Any of the six back-office roles (AL-02).</summary>
    public const string InternalStaff = "role:internal";

    public static readonly string FleetOwnerScope = FleetRole(FleetRoles.Owner);
    public static readonly string FleetManagerScope = FleetRole(FleetRoles.Manager);
    public static readonly string FleetViewerScope = FleetRole(FleetRoles.Viewer);
}

/// <summary>Requires a fleet sub-role of at least <paramref name="MinimumFleetRole"/> (AL-03).</summary>
public sealed record FleetRoleRequirement(string MinimumFleetRole) : IAuthorizationRequirement;

/// <inheritdoc cref="FleetRoleRequirement"/>
public sealed class FleetRoleHandler : AuthorizationHandler<FleetRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FleetRoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (FleetRoles.Satisfies(context.User.FleetRole(), requirement.MinimumFleetRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class MageRideAuthorizationEndpointExtensions
{
    /// <summary>Requires one of the given canonical roles (AL-06).</summary>
    public static TBuilder RequireMageRideRole<TBuilder>(this TBuilder builder, params string[] roles)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Length == 0)
        {
            throw new ArgumentException("Name at least one role.", nameof(roles));
        }

        foreach (var role in roles)
        {
            if (!MageRideRoles.IsKnown(role))
            {
                throw new ArgumentException($"'{role}' is not one of the nine canonical roles (AL-06).", nameof(roles));
            }
        }

        return builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(MageRideClaims.Role, roles));
    }

    /// <summary>Requires a fleet sub-role of at least <paramref name="minimumFleetRole"/> (AL-03).</summary>
    public static TBuilder RequireFleetRole<TBuilder>(this TBuilder builder, string minimumFleetRole)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(MageRidePolicies.FleetRole(minimumFleetRole));
    }
}
