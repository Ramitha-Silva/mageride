namespace MageRide.Shared.Auth;

/// <summary>
/// One capability from the URD §2.3 legend. A cell is a set of these, and a caller's effective
/// set is the bitwise union across every role held (AL-06).
/// </summary>
/// <remarks>
/// Flags rather than a rank, because the legend does not form a ladder. ⚙ Configure is not
/// "less than" 👁 Read-only and neither contains "raise"; a Finance Officer who may configure
/// rates but not change a tariff's structure, and a CSR who may recommend a refund but not
/// execute one, are different shapes of authority, not different amounts of it. A union of
/// ranks would have to pick one of them to be bigger and would be wrong either way.
/// </remarks>
[Flags]
public enum PermissionGrant
{
    /// <summary>➖ No access. The deny-by-default value, and what an unmapped area evaluates to.</summary>
    None = 0,

    /// <summary>👁 May read the records in this area.</summary>
    Read = 1 << 0,

    /// <summary>✅ May create, edit and execute in this area.</summary>
    Write = 1 << 1,

    /// <summary>⚙ May change settings in this area, but not the records the settings govern.</summary>
    Configure = 1 << 2,

    /// <summary>"raise" / "report" — may submit a request or report, but never adjudicate it.</summary>
    Raise = 1 << 3,

    /// <summary>
    /// ◐ Bounded to records the caller owns, or that belong to their organisation.
    /// </summary>
    /// <remarks>
    /// <b>This flag is a fence, not an answer.</b> iam-svc knows the caller's roles; it does not
    /// know whether ride 7 belongs to them or vehicle 9 belongs to their fleet. A service that
    /// sees <see cref="OwnScope"/> has been told "allowed, and you must bound it" — the
    /// <c>Qualifier</c> on the cell names how (<c>own</c>, <c>own org</c>, <c>assigned</c>,
    /// <c>financial</c>, <c>at onboarding</c>, …). Treating it as a plain grant is the bug this
    /// remark exists to prevent.
    /// </remarks>
    OwnScope = 1 << 4,
}

/// <summary>
/// One cell of the URD §2.3 matrix: the symbol as the spec prints it, and what it means.
/// </summary>
/// <param name="Symbol">
/// The cell verbatim — <c>✅</c>, <c>◐ own org</c>, <c>⚙ rates</c>, <c>raise</c>, <c>➖</c>. Kept
/// so <c>GET /v1/admin/rbac/matrix</c> can hand a portal the spec's own wording, and so a test
/// can compare this table against the markdown it was transcribed from.
/// </param>
/// <param name="Grants">What the symbol authorises.</param>
/// <param name="Qualifier">
/// The scope note the cell carries, if any: <c>own</c>, <c>own org</c>, <c>assigned</c>,
/// <c>financial</c>, <c>subset</c>, <c>rates</c>, <c>at onboarding</c>, <c>temp on reports</c>,
/// <c>on tickets</c>, <c>verification</c>, <c>raise/recommend</c>, <c>approve/execute</c>,
/// <c>read</c>.
/// </param>
public sealed record PermissionCell(string Symbol, PermissionGrant Grants, string? Qualifier)
{
    /// <summary>➖ — the value every unmapped (area, role) pair takes.</summary>
    public static readonly PermissionCell Denied = new(Symbols.None, PermissionGrant.None, null);

    /// <summary>The legend glyphs, as URD §2.3 prints them.</summary>
    public static class Symbols
    {
        public const string Full = "✅";        // ✅ Full (create/edit/execute)
        public const string Configure = "⚙";   // ⚙ Configure (settings only)
        public const string ReadOnly = "\U0001F441"; // 👁 Read-only
        public const string OwnScope = "◐";    // ◐ Own-scope / limited
        public const string None = "➖";        // ➖ No access
        public const string Raise = "raise";        // end user may submit, not adjudicate
        public const string Report = "report";      // ditto, on the Moderation row
    }

    /// <summary>
    /// Reads a URD §2.3 cell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mechanical on purpose: glyph decides the base capability, then the qualifier narrows it.
    /// The three narrowings are the three places URD §2.3 writes a verb into the cell instead of
    /// relying on the legend —
    /// </para>
    /// <list type="bullet">
    /// <item><c>read</c> ("✅ read", Auditor on the audit trail) drops <see cref="PermissionGrant.Write"/>,
    /// because URD §2.4 says the Auditor has "no write access anywhere". The ✅ is about breadth,
    /// not about writing.</item>
    /// <item><c>raise</c>/<c>recommend</c> ("◐ raise/recommend", CSR on refunds) trades
    /// <see cref="PermissionGrant.Write"/> for <see cref="PermissionGrant.Raise"/> — the same row
    /// gives Finance "✅ approve/execute", and the pair only makes sense if the CSR cannot.</item>
    /// <item><c>subset</c> ("◐ subset", Admin on system settings) trades it for
    /// <see cref="PermissionGrant.Configure"/>, matching the sibling row where Admin is a plain ⚙.</item>
    /// </list>
    /// <para>
    /// Anything else is scope, and scope is data (see <see cref="PermissionGrant.OwnScope"/>).
    /// </para>
    /// </remarks>
    public static PermissionCell Parse(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var trimmed = symbol.Trim();
        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var glyph = separator < 0 ? trimmed : trimmed[..separator];
        var qualifier = separator < 0 ? null : trimmed[(separator + 1)..].Trim();

        if (string.IsNullOrEmpty(qualifier))
        {
            qualifier = null;
        }

        var grants = glyph switch
        {
            // ✅ carries Configure as well as Write. The legend reads "Full (create/edit/execute)"
            // against "Configure (settings only)": Full is the broader authority in the same area,
            // and changing a setting is editing one. Without this a Super Admin — ✅ on both
            // Platform-config rows — would be refused a screen the Admin beside them (⚙) may use,
            // which is the one reading of the legend URD §2.4 rules out in as many words.
            Symbols.Full => PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.Configure,
            Symbols.Configure => PermissionGrant.Read | PermissionGrant.Configure,
            Symbols.ReadOnly => PermissionGrant.Read,
            Symbols.OwnScope => PermissionGrant.Read | PermissionGrant.Write | PermissionGrant.OwnScope,
            Symbols.None => PermissionGrant.None,
            Symbols.Raise or Symbols.Report => PermissionGrant.Raise,
            _ => throw new ArgumentException(
                $"'{glyph}' is not a URD §2.3 legend symbol. The legend is ✅ ⚙ 👁 ◐ ➖ plus the words " +
                "'raise' and 'report'.",
                nameof(symbol)),
        };

        return new PermissionCell(trimmed, Narrow(grants, qualifier), qualifier);
    }

    /// <summary>Whether this cell authorises everything in <paramref name="needed"/>.</summary>
    public bool Satisfies(PermissionGrant needed) => needed != PermissionGrant.None && (Grants & needed) == needed;

    private static PermissionGrant Narrow(PermissionGrant grants, string? qualifier)
    {
        if (qualifier is null || grants == PermissionGrant.None)
        {
            return grants;
        }

        if (string.Equals(qualifier, "read", StringComparison.OrdinalIgnoreCase))
        {
            return (grants & ~(PermissionGrant.Write | PermissionGrant.Configure)) | PermissionGrant.Read;
        }

        if (qualifier.Contains("raise", StringComparison.OrdinalIgnoreCase) ||
            qualifier.Contains("recommend", StringComparison.OrdinalIgnoreCase))
        {
            return (grants & ~PermissionGrant.Write) | PermissionGrant.Read | PermissionGrant.Raise;
        }

        if (string.Equals(qualifier, "subset", StringComparison.OrdinalIgnoreCase))
        {
            return (grants & ~PermissionGrant.Write) | PermissionGrant.Read | PermissionGrant.Configure;
        }

        return grants;
    }
}

/// <summary>
/// One row of URD §2.3 — a feature area every service gates against.
/// </summary>
/// <param name="Key">
/// Stable kebab identifier. The wire name and what an endpoint names in
/// <c>RequireFeature</c>; unlike <see cref="Label"/> it survives a wording change in the URD.
/// </param>
/// <param name="Label">The row's URD §2.3 wording, bold markers stripped.</param>
public sealed record FeatureArea(string Key, string Label)
{
    public override string ToString() => Key;
}

/// <summary>
/// The twenty-one feature areas of URD §2.3, in the order the spec prints them.
/// </summary>
/// <remarks>
/// Four of the rows share an opening phrase — two <c>Fleet</c>, two <c>Hardware trackers</c> and
/// two <c>Platform config</c> — so the keys name what distinguishes them rather than what they
/// have in common. <see cref="PermissionMatrixTests"/> in the test project parses the markdown
/// table out of <c>specs/user-requirements-document.md</c> and asserts these labels and every
/// cell against it, which is what "matching URD §2.3 exactly" is worth anything as.
/// </remarks>
public static class FeatureAreas
{
    public static readonly FeatureArea Passenger =
        new("passenger", "Passenger — book/track/rate rides & packages, SOS, trip history");

    public static readonly FeatureArea DriverApp =
        new("driver-app", "Driver app — register vehicle, sessions, accept dispatch, Directional Travel");

    public static readonly FeatureArea DriverWallet = new(
        "driver-wallet",
        "Driver wallet & credit transfers — top-up, bulk vouchers, initiate/approve driver-to-driver " +
        "credit transfers (Driver App only)");

    public static readonly FeatureArea DriverWalletAdjustments =
        new("driver-wallet-adjustments", "Driver wallet adjustments / reversals (back-office)");

    public static readonly FeatureArea FleetOperations =
        new("fleet-operations", "Fleet — org & vehicle onboarding, driver assignment, scheduling");

    public static readonly FeatureArea FleetMonitoring =
        new("fleet-monitoring", "Fleet — live fleet map, per-vehicle analytics");

    public static readonly FeatureArea FleetBilling =
        new("fleet-billing", "Fleet billing — monthly per-Mode-B-vehicle invoice, fleet wallet");

    public static readonly FeatureArea TrackerBinding =
        new("tracker-binding", "Hardware trackers — bind to vehicle, bulk onboard, fleet health");

    public static readonly FeatureArea TrackerDecommission =
        new("tracker-decommission", "Hardware trackers — decommission / revoke credentials");

    public static readonly FeatureArea Verification = new(
        "verification",
        "Onboarding/Verification — review docs, approve/reject registrations, background checks");

    public static readonly FeatureArea Support =
        new("support", "Support — tickets, trip disputes, block/unblock, temporary delisting");

    public static readonly FeatureArea Refunds = new("refunds", "Refunds — raise / approve / execute");

    public static readonly FeatureArea Moderation =
        new("moderation", "Moderation — suspend/ban driver or vehicle, review reports");

    public static readonly FeatureArea Finance = new(
        "finance",
        "Finance — payouts, settlements, reconciliation, wallet reversals/adjustments");

    public static readonly FeatureArea PlatformPricing = new(
        "platform-pricing",
        "Platform config — fare tariffs, daily-fee rates, bulk-voucher discount tiers, subscription pricing");

    public static readonly FeatureArea PlatformSettings = new(
        "platform-settings",
        "Platform config — Driver Level params, feature flags, system settings");

    public static readonly FeatureArea RoleManagement = new(
        "role-management",
        "User & role management (RBAC) — provision internal users, assign roles, define permissions");

    public static readonly FeatureArea AccountManagement = new(
        "account-management",
        "End-user account management — KYC status, deactivate/restore users");

    public static readonly FeatureArea Analytics =
        new("analytics", "Analytics & reporting — platform-wide dashboards");

    public static readonly FeatureArea AuditTrail = new("audit-trail", "Audit logs & admin-action trail");

    public static readonly FeatureArea Announcements = new("announcements", "Announcements / broadcast");

    /// <summary>Every area, in URD §2.3 row order.</summary>
    public static readonly IReadOnlyList<FeatureArea> All =
    [
        Passenger,
        DriverApp,
        DriverWallet,
        DriverWalletAdjustments,
        FleetOperations,
        FleetMonitoring,
        FleetBilling,
        TrackerBinding,
        TrackerDecommission,
        Verification,
        Support,
        Refunds,
        Moderation,
        Finance,
        PlatformPricing,
        PlatformSettings,
        RoleManagement,
        AccountManagement,
        Analytics,
        AuditTrail,
        Announcements,
    ];

    private static readonly IReadOnlyDictionary<string, FeatureArea> ByKey =
        All.ToDictionary(static area => area.Key, StringComparer.Ordinal);

    public static FeatureArea? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var area) ? area : null;

    /// <summary>Formats a grant set for a log line or an error detail.</summary>
    public static string Describe(PermissionGrant grants) =>
        grants == PermissionGrant.None
            ? "none"
            : grants.ToString().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
}
