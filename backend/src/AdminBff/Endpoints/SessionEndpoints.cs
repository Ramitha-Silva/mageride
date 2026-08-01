using MageRide.AdminBff.Navigation;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// <c>GET /v1/admin/session</c> — what the Admin Portal fetches the moment sign-in completes
/// (URD §2.2). <b>Δ C062.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Sign-in itself is not here, and must not be.</b> AL-07 puts Password and Google on iam-svc's
/// <c>POST /v1/admin/auth/login</c>, `gateway-routes.json` sends <c>/v1/admin/auth/**</c> there at
/// Order 20, and iam-svc owns every credential, the failed-attempt lock-out and the optional IP
/// allow-list. A second credential path in the BFF would be a second place a password could be
/// checked and a second place the lock-out could be forgotten. What the portal needs *after*
/// sign-in — who am I, what may I do, what menu do I draw — has nowhere to come from, and that is
/// this route.
/// </para>
/// <para>
/// <b><c>mfaRequired</c> is on the wire and is always <see langword="false"/>.</b> A field that
/// cannot be true is usually noise; this one is the opposite. AL-37 removed the second factor and
/// D3' §0 and D7' §4.2 still carry the pre-AL-37 wording, so a portal built from those documents
/// would wait for a challenge that is never coming. Answering the question explicitly is what stops
/// that, and <c>NoLoginPathAsksForASecondFactor</c> asserts it against the running route table.
/// </para>
/// <para>
/// <b>The permission list and the menu come from one evaluation.</b> URD §2.2 requires the UI to be
/// "rendered from the same permission model the API enforces server-side"; two calls, or two
/// projections of two evaluations, would be two chances to disagree.
/// </para>
/// <para>
/// <b>Gated on authentication and not on a feature area</b>, deliberately: every internal role may
/// ask what it is allowed to do. Anything narrower would mean a role could sign in and not be told
/// it has no menu — which is a blank console with no explanation rather than a refusal.
/// </para>
/// </remarks>
internal static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/session", GetAsync)
            .WithName("getAdminSession")
            .WithSummary("Identity, effective permissions and the role-scoped menu manifest (URD §2.2).")
            .RequireAuthorization(MageRidePolicies.InternalStaff);

        return admin;
    }

    private static Ok<AdminSessionResponse> GetAsync(HttpContext context, IPermissionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluator);

        var principal = context.User;
        var roles = principal.Roles();

        var fleet = principal.TryGetFleetScope(out var fleetRole, out var fleetId)
            ? new FleetScope(fleetId, fleetRole)
            : null;

        var effective = evaluator.Evaluate(principal.RequireSubjectId(), [.. roles], fleet);

        return TypedResults.Ok(new AdminSessionResponse(
            principal.RequireSubjectId(),
            [.. roles.Order(StringComparer.Ordinal)],
            [
                .. effective.Permissions
                    .Where(static permission => permission.Grants != PermissionGrant.None)
                    .Select(static permission => new AdminPermissionResponse(
                        permission.Area.Key,
                        permission.Area.Label,
                        permission.Symbol,
                        Describe(permission.Grants),
                        permission.Qualifier,
                        permission.ScopedGrants != PermissionGrant.None)),
            ],
            [
                .. AdminMenu.For(evaluator, principal).Select(static group => new AdminMenuGroupResponse(
                    group.Key,
                    group.LabelKey,
                    [
                        .. group.Items.Select(static item => new AdminMenuItemResponse(
                            item.Key, item.LabelKey, item.Path, item.OwnedBy)),
                    ])),
            ],
            MfaRequired: false));
    }

    /// <summary>The capability flags as a list, so a portal reads verbs rather than a bitmask.</summary>
    private static IReadOnlyList<string> Describe(PermissionGrant grants) =>
    [
        .. new[]
            {
                (PermissionGrant.Read, "read"),
                (PermissionGrant.Write, "write"),
                (PermissionGrant.Configure, "configure"),
                (PermissionGrant.Raise, "raise"),
            }
            .Where(pair => grants.HasFlag(pair.Item1))
            .Select(static pair => pair.Item2),
    ];
}
