using MageRide.Iam.Profiles;
using MageRide.Iam.Rbac;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>GET /v1/me/permissions</c> and <c>/v1/admin/rbac/**</c> — the deny-by-default RBAC surface
/// (AL-06, URD §2.2/§2.3).
/// </summary>
/// <remarks>
/// <para>
/// The five admin routes are the only <b>privileged</b> endpoints iam-svc has, and all five are
/// gated on URD §2.3's "User &amp; role management (RBAC)" row, which reads
/// <c>➖ ➖ ➖ ➖ ✅ ➖ ➖ ➖ 👁</c>: writable by Super Admin, readable by Auditor, refused to the
/// other seven roles. <b>An Admin is refused too</b> — URD §2.4 spells it out ("Admin … **No**
/// RBAC/role management") and it is the single most surprising cell in the matrix, so
/// <c>RbacEndpointTests</c> asserts it for every role by name rather than by category.
/// </para>
/// <para>
/// <c>GET /v1/me/permissions</c> is not gated: it describes the caller to the caller, and it is
/// what URD §2.2 means by "the UI is rendered from the same permission model the API enforces
/// server-side". A portal that could not read its own grants would have to guess at its menus.
/// </para>
/// </remarks>
public static class RbacEndpoints
{
    public static IEndpointRouteBuilder MapRbacEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/v1/me/permissions", MyPermissionsAsync)
            .WithTags("rbac")
            .WithName("getMyPermissions")
            .RequireAuthorization();

        var rbac = endpoints.MapGroup("/v1/admin/rbac").WithTags("rbac");

        // Read is 👁 on the RBAC row — Super Admin and Auditor.
        rbac.MapGet("/matrix", () => TypedResults.Ok(PermissionMatrixResponse.Build()))
            .WithName("getPermissionMatrix")
            .RequireFeature(FeatureAreas.RoleManagement, PermissionGrant.Read);

        rbac.MapGet("/roles", CatalogAsync)
            .WithName("listRoles")
            .RequireFeature(FeatureAreas.RoleManagement, PermissionGrant.Read);

        rbac.MapGet("/users/{userId}", GetGrantsAsync)
            .WithName("getUserRoleGrants")
            .RequireFeature(FeatureAreas.RoleManagement, PermissionGrant.Read);

        // Write is ✅ — Super Admin alone.
        rbac.MapPost("/users/{userId}/roles", GrantAsync)
            .WithName("grantRole")
            .RequireFeature(FeatureAreas.RoleManagement, PermissionGrant.Write);

        rbac.MapDelete("/users/{userId}/roles/{role}", RevokeAsync)
            .WithName("revokeRole")
            .RequireFeature(FeatureAreas.RoleManagement, PermissionGrant.Write);

        return endpoints;
    }

    private static async Task<Ok<EffectivePermissionsResponse>> MyPermissionsAsync(
        HttpContext context, IProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var effective = await profiles.PermissionsAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(EffectivePermissionsResponse.From(effective));
    }

    private static async Task<Ok<RoleCatalogResponse>> CatalogAsync(
        IRoleAdminService roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var catalog = await roles.CatalogAsync(cancellationToken);

        return TypedResults.Ok(new RoleCatalogResponse([.. catalog.Select(RoleCatalogEntryResponse.From)]));
    }

    private static async Task<Ok<UserRoleGrantsResponse>> GetGrantsAsync(
        string userId, IRoleAdminService roles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var grants = await roles.GetAsync(UserEndpoints.RequireId(userId, "account"), cancellationToken);

        return TypedResults.Ok(UserRoleGrantsResponse.From(grants));
    }

    private static async Task<Ok<UserRoleGrantsResponse>> GrantAsync(
        string userId,
        GrantRoleBody? body,
        HttpContext context,
        IRoleAdminService roles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roles);

        var grants = await roles.GrantAsync(
            context.User.RequireSubjectId(),
            UserEndpoints.RequireId(userId, "account"),
            body?.Role,
            cancellationToken);

        return TypedResults.Ok(UserRoleGrantsResponse.From(grants));
    }

    private static async Task<Ok<UserRoleGrantsResponse>> RevokeAsync(
        string userId,
        string role,
        HttpContext context,
        IRoleAdminService roles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roles);

        var grants = await roles.RevokeAsync(
            context.User.RequireSubjectId(),
            UserEndpoints.RequireId(userId, "account"),
            role,
            cancellationToken);

        return TypedResults.Ok(UserRoleGrantsResponse.From(grants));
    }
}
