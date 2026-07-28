using MageRide.Shared.Auth;

namespace MageRide.Iam.Rbac;

/// <summary>
/// URD §2.3 — the Feature Permission Matrix, compiled into the service (AL-06).
/// </summary>
/// <remarks>
/// <para>
/// Twenty-one feature areas by nine canonical roles, every cell explicit. <b>There is no default
/// and no fall-through</b>: <see cref="Cell"/> answers <see cref="PermissionCell.Denied"/> for a
/// pair it does not hold, so an area somebody forgets to add here denies everybody rather than
/// admitting them, and an endpoint that names an unknown area answers 403.
/// </para>
/// <para>
/// <b>Read-only, deliberately.</b> URD §2.3's own RBAC row lets a Super Admin "define
/// permissions", and it is tempting to read that as a table an operator edits. It cannot be: the
/// principal who would edit it is the principal it constrains, so a writable matrix is a Super
/// Admin one <c>UPDATE</c> away from granting themselves something URD §2.3 forbids — and the
/// matrix's only job is to make that impossible. Changing it is a spec change, a code change and
/// a deploy, in that order. What a Super Admin does at runtime is grant and revoke *roles*
/// (<c>iam.user_roles</c>), which is what "assign roles" in the same sentence means.
/// </para>
/// <para>
/// The rows are laid out as the spec prints them, one line per row, so the transcription can be
/// read against the markdown by eye as well as by <c>PermissionMatrixTests</c>, which parses
/// <c>specs/user-requirements-document.md</c> and compares all 189 cells.
/// </para>
/// </remarks>
public static class PermissionMatrix
{
    /// <summary>Column order of URD §2.3: DRV · PAX · FLT · ADM · S.ADM · VER · CSR · FIN · AUD.</summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        MageRideRoles.Driver,
        MageRideRoles.Passenger,
        MageRideRoles.FleetOwner,
        MageRideRoles.Admin,
        MageRideRoles.SuperAdmin,
        MageRideRoles.VerificationOfficer,
        MageRideRoles.SupportCsr,
        MageRideRoles.FinanceOfficer,
        MageRideRoles.Auditor,
    ];

    private static readonly IReadOnlyDictionary<FeatureArea, IReadOnlyDictionary<string, PermissionCell>> Rows =
        Build();

    /// <summary>The cell for one (area, role) pair. Unknown pairs are ➖, never an exception.</summary>
    public static PermissionCell Cell(FeatureArea area, string role)
    {
        ArgumentNullException.ThrowIfNull(area);

        return Rows.TryGetValue(area, out var row) && row.TryGetValue(role, out var cell)
            ? cell
            : PermissionCell.Denied;
    }

    /// <summary>The whole row for an area, keyed by role. Every one of the nine is present.</summary>
    public static IReadOnlyDictionary<string, PermissionCell> Row(FeatureArea area)
    {
        ArgumentNullException.ThrowIfNull(area);

        return Rows.TryGetValue(area, out var row)
            ? row
            : Columns.ToDictionary(static role => role, static _ => PermissionCell.Denied, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<FeatureArea, IReadOnlyDictionary<string, PermissionCell>> Build()
    {
        var rows = new Dictionary<FeatureArea, IReadOnlyDictionary<string, PermissionCell>>();

        //     Feature area                             DRV               PAX       FLT          ADM         S.ADM  VER               CSR                  FIN                   AUD
        Row(FeatureAreas.Passenger, /*               */ "➖",             "✅",     "➖",        "👁",       "👁",  "➖",             "👁",                "➖",                 "👁");
        Row(FeatureAreas.DriverApp, /*               */ "◐ own",          "➖",     "➖",        "👁",       "👁",  "👁",             "👁",                "➖",                 "👁");
        Row(FeatureAreas.DriverWallet, /*            */ "◐ own",          "➖",     "➖",        "👁",       "👁",  "➖",             "👁",                "👁",                 "👁");
        Row(FeatureAreas.DriverWalletAdjustments, /* */ "➖",             "➖",     "➖",        "👁",       "✅",  "➖",             "➖",                "✅",                 "👁");
        Row(FeatureAreas.FleetOperations, /*         */ "◐ assigned",     "➖",     "◐ own org", "👁",       "✅",  "👁",             "👁",                "➖",                 "👁");
        Row(FeatureAreas.FleetMonitoring, /*         */ "➖",             "➖",     "◐ own org", "👁",       "👁",  "➖",             "👁",                "👁",                 "👁");
        Row(FeatureAreas.FleetBilling, /*            */ "➖",             "➖",     "◐ own org", "👁",       "✅",  "➖",             "👁",                "✅",                 "👁");
        Row(FeatureAreas.TrackerBinding, /*          */ "◐ own",          "➖",     "◐ own org", "👁",       "✅",  "➖",             "➖",                "➖",                 "👁");
        Row(FeatureAreas.TrackerDecommission, /*     */ "➖",             "➖",     "◐ own org", "✅",       "✅",  "➖",             "➖",                "➖",                 "👁");
        Row(FeatureAreas.Verification, /*            */ "➖",             "➖",     "➖",        "✅",       "✅",  "✅",             "👁",                "➖",                 "👁");
        Row(FeatureAreas.Support, /*                 */ "raise",          "raise",  "◐ own org", "✅",       "✅",  "➖",             "✅",                "◐ financial",        "👁");
        Row(FeatureAreas.Refunds, /*                 */ "raise",          "raise",  "➖",        "✅",       "✅",  "➖",             "◐ raise/recommend", "✅ approve/execute", "👁");
        Row(FeatureAreas.Moderation, /*              */ "➖",             "report", "➖",        "✅",       "✅",  "◐ at onboarding", "◐ temp on reports", "➖",                "👁");
        Row(FeatureAreas.Finance, /*                 */ "➖",             "➖",     "➖",        "👁",       "✅",  "➖",             "➖",                "✅",                 "👁");
        Row(FeatureAreas.PlatformPricing, /*         */ "➖",             "➖",     "➖",        "⚙",        "✅",  "➖",             "➖",                "⚙ rates",            "👁");
        Row(FeatureAreas.PlatformSettings, /*        */ "➖",             "➖",     "➖",        "◐ subset", "✅",  "➖",             "➖",                "➖",                 "👁");
        Row(FeatureAreas.RoleManagement, /*          */ "➖",             "➖",     "➖",        "➖",       "✅",  "➖",             "➖",                "➖",                 "👁");
        Row(FeatureAreas.AccountManagement, /*       */ "➖",             "➖",     "➖",        "✅",       "✅",  "◐ verification", "◐ on tickets",      "➖",                 "👁");
        Row(FeatureAreas.Analytics, /*               */ "➖",             "➖",     "◐ own org", "✅",       "✅",  "➖",             "👁",                "◐ financial",        "👁");
        Row(FeatureAreas.AuditTrail, /*              */ "➖",             "➖",     "➖",        "👁",       "👁",  "➖",             "➖",                "👁",                 "✅ read");
        Row(FeatureAreas.Announcements, /*           */ "➖",             "➖",     "➖",        "✅",       "✅",  "➖",             "➖",                "➖",                 "👁");

        if (rows.Count != FeatureAreas.All.Count)
        {
            // A row that was declared in FeatureAreas and never transcribed here would silently
            // deny everybody. That is the safe direction, and still a bug.
            var missing = FeatureAreas.All.Where(area => !rows.ContainsKey(area)).Select(static area => area.Key);
            throw new InvalidOperationException(
                "URD §2.3 rows declared in FeatureAreas but not transcribed into PermissionMatrix: " +
                string.Join(", ", missing));
        }

        return rows;

        void Row(FeatureArea area, params string[] symbols)
        {
            if (symbols.Length != Columns.Count)
            {
                throw new InvalidOperationException(
                    $"URD §2.3 has {Columns.Count} role columns; '{area.Key}' was given {symbols.Length}.");
            }

            var cells = new Dictionary<string, PermissionCell>(StringComparer.Ordinal);

            for (var column = 0; column < symbols.Length; column++)
            {
                cells[Columns[column]] = PermissionCell.Parse(symbols[column]);
            }

            rows[area] = cells;
        }
    }
}
