using MageRide.AdminBff.Auditing;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// Marks a route as sitting inside the audited <c>/v1/admin</c> group.
/// </summary>
/// <remarks>
/// Metadata rather than a comment, because it is what <c>AdminBffApplication</c>'s start-up guard
/// checks: an endpoint mapped outside the group carries neither this nor the D-35 filter, and the
/// service refuses to start rather than serving one mutation the interceptor cannot see.
/// </remarks>
public sealed record AdminSurfaceMarker;

/// <summary>
/// The one place every admin-bff route is mapped, and the only place the D-35 filter is attached.
/// </summary>
/// <remarks>
/// <para>
/// <b>One group, so the two fences cannot be forgotten per route.</b> The audit interceptor and the
/// <see cref="AdminSurfaceMarker"/> are group-level conventions: a route added to any of the
/// families below inherits both, and a route added anywhere else fails the start-up guard. AL-06's
/// gate is per route rather than per group, because the (feature area, capability) pair differs on
/// every one of them and a group-level default would be a role decision nobody made.
/// </para>
/// <para>
/// <b>C063, C064 and C065 map onto this same group.</b> Verification queues, the three directories
/// and finance/PDPA are separate components on this project; each adds a
/// <c>Map…Endpoints(this IEndpointRouteBuilder admin)</c> here and gets the interceptor, the marker
/// and the RBAC test's coverage without touching either fence.
/// </para>
/// </remarks>
public static class AdminEndpoints
{
    /// <summary>Every route on this surface lives under this prefix (AL-02).</summary>
    public const string Prefix = "/v1/admin";

    public static IEndpointRouteBuilder MapAdminBffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var admin = endpoints.MapGroup(Prefix)
            .WithTags("admin-bff")
            .WithMetadata(new AdminSurfaceMarker())
            .AddEndpointFilter<AuditInterceptor>();

        admin.MapSessionEndpoints();
        admin.MapDashboardEndpoints();
        admin.MapModerationEndpoints();
        admin.MapConfigurationEndpoints();
        admin.MapAnnouncementEndpoints();
        admin.MapAuditLogEndpoints();
        admin.MapGtfsProxyEndpoints();

        return endpoints;
    }
}
