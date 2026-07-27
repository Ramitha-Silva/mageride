using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace MageRide.Shared.Auth;

/// <summary>Claim names carried by a MageRide access token (D3' §0 "Auth", D-29).</summary>
public static class MageRideClaims
{
    /// <summary>Subject — the <c>iam.users.id</c> the token was issued for.</summary>
    public const string Subject = "sub";

    /// <summary>
    /// Canonical role (AL-06). May appear more than once: effective permissions are the union of
    /// <c>iam.user_roles</c>.
    /// </summary>
    public const string Role = "role";

    /// <summary>Org-scoped fleet sub-role, <c>owner</c> | <c>manager</c> | <c>viewer</c> (AL-03).</summary>
    public const string FleetRole = "fleet_role";

    /// <summary>Fleet the <see cref="FleetRole"/> applies to.</summary>
    public const string FleetId = "fleet_id";

    /// <summary>Bound device (Android Keystore / iOS Keychain), for single-active-device (AL-08).</summary>
    public const string DeviceId = "device_id";

    /// <summary><c>passenger</c> | <c>driver</c> — which app the session belongs to (AL-08).</summary>
    public const string App = "app";

    /// <summary>Attestation result carried through from the gateway (D-30).</summary>
    public const string Attestation = "attestation";
}

/// <summary>The nine canonical roles (AL-06; ADD §9.1 <c>iam.roles</c>).</summary>
public static class MageRideRoles
{
    public const string Passenger = "passenger";
    public const string Driver = "driver";
    public const string FleetOwner = "fleet_owner";
    public const string Admin = "admin";
    public const string SuperAdmin = "super_admin";
    public const string VerificationOfficer = "verification_officer";
    public const string SupportCsr = "support_csr";
    public const string FinanceOfficer = "finance_officer";
    public const string Auditor = "auditor";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Passenger, Driver, FleetOwner, Admin, SuperAdmin, VerificationOfficer, SupportCsr, FinanceOfficer, Auditor,
    };

    /// <summary>The six back-office roles served by admin-bff (AL-02). Provisioned by Super Admin only.</summary>
    public static readonly IReadOnlySet<string> Internal = new HashSet<string>(StringComparer.Ordinal)
    {
        Admin, SuperAdmin, VerificationOfficer, SupportCsr, FinanceOfficer, Auditor,
    };

    public static bool IsKnown(string? role) => role is not null && All.Contains(role);
}

/// <summary>Org-scoped fleet sub-roles (AL-03), most to least privileged.</summary>
public static class FleetRoles
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Viewer = "viewer";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Owner, Manager, Viewer };

    /// <summary>Higher rank means more privilege. Unknown values rank below <see cref="Viewer"/>.</summary>
    public static int Rank(string? fleetRole) => fleetRole switch
    {
        Owner => 3,
        Manager => 2,
        Viewer => 1,
        _ => 0,
    };

    /// <summary><see langword="true"/> when <paramref name="held"/> is at least <paramref name="required"/>.</summary>
    public static bool Satisfies(string? held, string required) => Rank(held) >= Rank(required) && Rank(required) > 0;
}

/// <summary>The apps a session may belong to (AL-08). Portals are not app-scoped.</summary>
public static class MageRideApps
{
    public const string Passenger = "passenger";
    public const string Driver = "driver";
}

/// <summary>Reads MageRide claims off the authenticated principal.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The <c>sub</c> claim as a <see cref="Guid"/>, or <see langword="null"/> if absent or malformed.</summary>
    public static Guid? SubjectId(this ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(MageRideClaims.Subject)
                    ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary><see cref="SubjectId"/>, or a throw — for paths that already require authentication.</summary>
    public static Guid RequireSubjectId(this ClaimsPrincipal? principal) =>
        principal.SubjectId() ?? throw new InvalidOperationException("The principal carries no usable 'sub' claim.");

    /// <summary>Every canonical role held. The effective permission set is their union (AL-06).</summary>
    public static IReadOnlyCollection<string> Roles(this ClaimsPrincipal? principal) =>
        principal is null
            ? []
            : principal.FindAll(MageRideClaims.Role).Select(static c => c.Value).Where(MageRideRoles.IsKnown).ToArray();

    public static bool HasRole(this ClaimsPrincipal? principal, string role) =>
        principal is not null && principal.HasClaim(MageRideClaims.Role, role);

    public static string? FleetRole(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(MageRideClaims.FleetRole);

    public static Guid? FleetId(this ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(MageRideClaims.FleetId), out var id) ? id : null;

    public static string? DeviceId(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(MageRideClaims.DeviceId);

    public static string? App(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(MageRideClaims.App);

    /// <summary>
    /// The value written to <c>command_log.actor_type</c> (ADD §9.1). The first canonical role
    /// held, or <c>anonymous</c> when the caller is unauthenticated.
    /// </summary>
    public static string ActorType(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        var roles = principal.Roles();
        return roles.Count > 0 ? roles.OrderBy(static r => r, StringComparer.Ordinal).First() : "authenticated";
    }

    public static bool TryGetFleetScope(
        this ClaimsPrincipal? principal,
        [NotNullWhen(true)] out string? fleetRole,
        out Guid fleetId)
    {
        fleetRole = principal.FleetRole();
        fleetId = principal.FleetId() ?? Guid.Empty;
        return fleetRole is not null && FleetRoles.All.Contains(fleetRole) && fleetId != Guid.Empty;
    }
}
