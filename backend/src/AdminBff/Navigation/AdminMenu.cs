using MageRide.Shared.Auth;

namespace MageRide.AdminBff.Navigation;

/// <summary>
/// One entry in the Admin Portal's left nav.
/// </summary>
/// <param name="Key">Stable identifier the portal keys its route table and its icons off.</param>
/// <param name="LabelKey">
/// A resource key, never a label. D-26 makes every user-facing string trilingual and the portal
/// (C103) owns the Si/Ta/En bundles; a server that shipped English text here would be the one place
/// on the platform that could not be translated.
/// </param>
/// <param name="Path">The portal route. Where an item is served by another service, this is still
/// the Admin Portal path — the gateway decides which process answers it.</param>
/// <param name="Area">The URD §2.3 row the item is gated on.</param>
/// <param name="Needed">What the caller must hold in <paramref name="Area"/> to see it.</param>
/// <param name="OwnedBy">
/// Which service answers the item's API. Recorded because six of the twenty are <b>not</b>
/// admin-bff's — see <see cref="AdminMenu"/>'s remark — and a nav manifest that hid that would be
/// read as a claim about who owns the data.
/// </param>
/// <param name="PlatformWide">
/// Whether the screen behind it acts platform-wide, and therefore needs the capability held
/// unscoped — mirroring <c>RequirePlatformWideFeature</c> on the endpoint.
/// </param>
/// <remarks>
/// <b>The flag exists so the nav cannot promise what the API refuses.</b> URD §2.3's ◐ cells give a
/// Verification Officer <c>◐ verification</c> on end-user account management and a Support CSR
/// <c>◐ on tickets</c>; neither is "fulfil a PDPA erasure". Without the flag both would be handed a
/// Data-rights menu entry whose every button answers 403.
/// </remarks>
public sealed record AdminMenuItem(
    string Key,
    string LabelKey,
    string Path,
    FeatureArea Area,
    PermissionGrant Needed,
    string OwnedBy,
    bool PlatformWide = false);

/// <summary>A nav group, as SCR-AP-001 draws them.</summary>
public sealed record AdminMenuGroup(string Key, string LabelKey, IReadOnlyList<AdminMenuItem> Items);

/// <summary>
/// The role-scoped menu manifest that drives the Admin Portal's navigation (URD §2.2: "Each role
/// sees only the menus and records its permissions allow").
/// </summary>
/// <remarks>
/// <para>
/// <b>The manifest is filtered by the same matrix the API enforces, not by a second list of
/// roles.</b> URD §2.2 says the UI "is rendered from the same permission model the API enforces
/// server-side", and the only way to make that true rather than aspirational is for the nav to be a
/// projection of URD §2.3 — so each item names an (area, capability) pair and
/// <see cref="For(IPermissionEvaluator, System.Security.Claims.ClaimsPrincipal)"/> keeps the ones
/// the caller satisfies. A hand-written per-role menu would be a third copy of the matrix and would
/// drift from both.
/// </para>
/// <para>
/// <b>It is nav, not authorization.</b> Hiding an item hides nothing: the endpoint behind it is
/// gated independently, and AL-06's own wording — "deny-by-default, independent of what the UI
/// shows" (US-21.1) — is about exactly this. A caller who guesses a path gets a 403, not a page.
/// </para>
/// <para>
/// <b>Six items are answered by other services and are here anyway.</b> The Admin Portal is one
/// application (AL-02) and its Configuration group has to contain the GTFS Dataset Manager
/// (transit-svc, AL-54), the daily-fee rates and voucher tiers (subscription-svc), the Driver-Level
/// parameters (dispatch-svc) and RBAC provisioning (iam-svc) — each of which is owned by the service
/// that owns the table it writes, which is the platform's single-writer rule. The gateway routes
/// those paths past admin-bff at Order 20 (`gateway-routes.json`). What admin-bff contributes is
/// that the operator sees one console.
/// </para>
/// </remarks>
public static class AdminMenu
{
    private const string Self = "admin-bff";

    /// <summary>Every nav group in SCR-AP-001's order, before any filtering.</summary>
    public static readonly IReadOnlyList<AdminMenuGroup> All =
    [
        new("overview", "nav.group.overview",
        [
            new("dashboard", "nav.dashboard", "/dashboard",
                FeatureAreas.Analytics, PermissionGrant.Read, Self),
            new("audit-log", "nav.auditLog", "/audit-log",
                FeatureAreas.AuditTrail, PermissionGrant.Read, Self),
        ]),

        new("onboarding", "nav.group.onboarding",
        [
            // C063's routes. Listed here because the nav manifest is one document and a group that
            // appeared only once its component landed would make the portal's shell depend on build
            // order rather than on the matrix.
            new("verification", "nav.verification", "/verification",
                FeatureAreas.Verification, PermissionGrant.Read, Self),
        ]),

        new("directories", "nav.group.directories",
        [
            // C064's three. Each names the row its endpoint names and carries the same
            // platform-wide flag — see DirectoryEndpoints for why these three rows and not the
            // obvious ones. A nav item gated on anything else would promise a screen the API
            // refuses, which is the failure this record's PlatformWide remark is about.
            new("passengers", "nav.passengers", "/passengers",
                FeatureAreas.Support, PermissionGrant.Read, Self, PlatformWide: true),
            new("drivers", "nav.drivers", "/drivers",
                FeatureAreas.DriverWallet, PermissionGrant.Read, Self, PlatformWide: true),
            new("vehicles", "nav.vehicles", "/vehicles",
                FeatureAreas.FleetMonitoring, PermissionGrant.Read, Self, PlatformWide: true),
        ]),

        new("moderation", "nav.group.moderation",
        [
            new("reports", "nav.reports", "/reports",
                FeatureAreas.Moderation, PermissionGrant.Read, Self),
            new("support-tickets", "nav.supportTickets", "/support/tickets",
                FeatureAreas.Support, PermissionGrant.Read, Self),
        ]),

        new("finance", "nav.group.finance",
        [
            // C065's.
            new("reconciliation", "nav.reconciliation", "/finance/reconciliation",
                FeatureAreas.Finance, PermissionGrant.Read, Self),
            new("wallet-adjustments", "nav.walletAdjustments", "/finance/adjustments",
                FeatureAreas.DriverWalletAdjustments, PermissionGrant.Write, Self),
            new("pdpa", "nav.pdpa", "/pdpa",
                FeatureAreas.AccountManagement, PermissionGrant.Write, Self, PlatformWide: true),
        ]),

        new("configuration", "nav.group.configuration",
        [
            new("fare-tariffs", "nav.fareTariffs", "/config/fares",
                FeatureAreas.PlatformPricing, PermissionGrant.Configure, Self),
            new("cities", "nav.cities", "/config/cities",
                FeatureAreas.PlatformSettings, PermissionGrant.Configure, Self),
            new("feature-flags", "nav.featureFlags", "/config/feature-flags",
                FeatureAreas.PlatformSettings, PermissionGrant.Configure, Self),
            new("trains", "nav.trains", "/config/trains",
                FeatureAreas.PlatformSettings, PermissionGrant.Configure, Self),
            new("announcements", "nav.announcements", "/announcements",
                FeatureAreas.Announcements, PermissionGrant.Write, Self),

            // Answered elsewhere; see the remark on this class.
            new("gtfs", "nav.gtfs", "/config/transit/gtfs",
                FeatureAreas.PlatformSettings, PermissionGrant.Configure, "transit-svc"),
            new("daily-fee-rates", "nav.dailyFeeRates", "/config/fees",
                FeatureAreas.PlatformPricing, PermissionGrant.Configure, "subscription-svc"),
            new("voucher-tiers", "nav.voucherTiers", "/config/voucher-tiers",
                FeatureAreas.PlatformPricing, PermissionGrant.Configure, "subscription-svc"),
            new("driver-levels", "nav.driverLevels", "/config/driver-levels",
                FeatureAreas.PlatformSettings, PermissionGrant.Configure, "dispatch-svc"),
        ]),

        new("access", "nav.group.access",
        [
            new("rbac", "nav.rbac", "/access/users",
                FeatureAreas.RoleManagement, PermissionGrant.Write, "iam-svc"),
        ]),
    ];

    /// <summary>
    /// The manifest as one caller sees it: items whose cell they do not satisfy are dropped, and a
    /// group left with no items is dropped with them.
    /// </summary>
    /// <remarks>
    /// An empty group would render as a heading with nothing under it — which tells a Verification
    /// Officer that a Finance section exists and they cannot reach it. That is information the nav
    /// has no reason to leak, and the 403 on the endpoint is the control either way.
    /// </remarks>
    public static IReadOnlyList<AdminMenuGroup> For(
        IPermissionEvaluator evaluator, System.Security.Claims.ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(principal);

        var fleet = principal.TryGetFleetScope(out var fleetRole, out var fleetId)
            ? new FleetScope(fleetId, fleetRole)
            : null;

        var effective = evaluator.Evaluate(principal.SubjectId() ?? Guid.Empty, [.. principal.Roles()], fleet);

        return
        [
            .. All
                .Select(group => group with
                {
                    Items =
                    [
                        .. group.Items.Where(item =>
                        {
                            var permission = effective.For(item.Area);

                            return permission.Satisfies(item.Needed)
                                   && (!item.PlatformWide || !permission.RequiresOwnScope(item.Needed));
                        }),
                    ],
                })
                .Where(group => group.Items.Count > 0),
        ];
    }
}
