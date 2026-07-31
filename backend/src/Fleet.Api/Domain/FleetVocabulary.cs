using MageRide.Shared.Errors;

namespace MageRide.Fleet.Domain;

/// <summary>
/// The organisation lifecycle, verbatim from <c>registry.fleets.status</c> (migration 0301).
/// </summary>
/// <remarks>
/// <b><c>fleet.yaml</c>'s <c>FleetStatus</c> enum also lists <c>SUSPENDED</c>, and the CHECK does
/// not.</b> No row can hold it, no spec line gives it a transition, and nothing in D3', D5' or
/// URD Epic 13 suspends an organisation — the platform suspends *vehicles* and *drivers*
/// (US-14.3). It is absent here rather than accepted-and-never-written, because a status this
/// service could return and the database could not store is a 500 waiting for the first operator
/// who tries it. Raised as a micro-change-set in the C058 handoff.
/// </remarks>
public static class FleetStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Pending, Approved, Rejected };
}

/// <summary>
/// The modes a fleet may operate (AL-03).
/// </summary>
/// <remarks>
/// Mode C is absent and stays absent: it is the on-demand plane, ride-svc's, and a driver's own
/// standby vehicle is onboarded in the Driver App through registry-svc. The database says the same
/// thing in <c>registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))</c>, so a route that forgot
/// this would fail on a constraint rather than create a Mode C fleet vehicle.
/// </remarks>
public static class FleetModes
{
    public const string PublicTransport = "A";
    public const string Private = "B";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { PublicTransport, Private };

    /// <summary>Strings rather than <see langword="char"/>: the column is <c>CHAR(1)</c>, which
    /// Npgsql binds as <c>bpchar</c> — a text type — in both directions.</summary>
    public static bool IsFleetMode(string? mode) => mode is not null && All.Contains(mode);
}

/// <summary>
/// <c>registry.vehicles.mode_b_billing</c> — "Service payment" in the UI (AL-51).
/// </summary>
/// <remarks>
/// AL-51 renamed the <b>label</b> and nothing else: the column, the request field and the
/// <c>/classification</c> path are intentionally unchanged, because a label is not worth a client
/// or a database migration. The values are <c>free</c> and <c>paid</c> on the wire and in the row.
/// </remarks>
public static class ModeBBilling
{
    public const string Free = "free";
    public const string Paid = "paid";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Free, Paid };
}

/// <summary>
/// The payout-profile lifecycle (<c>registry.fleet_payout_profiles.status</c>, §26 + migration 0313).
/// </summary>
/// <remarks>
/// <see cref="Superseded"/> is C058's addition and is the only status the service moves a row to
/// without an officer having decided anything: it is where the incumbent verified row goes when a
/// later edit is approved in its place, because <c>ux_payout_profile_verified</c> admits exactly
/// one verified row per org. See migration 0313's header for the full argument.
/// </remarks>
public static class PayoutProfileStatuses
{
    public const string PendingVerification = "pending_verification";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { PendingVerification, Verified, Rejected, Superseded };
}

/// <summary>
/// The three payout-document slots (AL-49, <c>fleet.yaml</c> <c>uploadPayoutProfileDocument</c>).
/// </summary>
/// <remarks>
/// BR-31.1 asks for two pieces of evidence — "latest bank statement <b>or</b> passbook first page",
/// and the bank-app-generated LankaQR image — which is why the first two share
/// <c>proof_upload_id</c> and the third has <c>lankaqr_upload_id</c> to itself (§26). The wire
/// names are the <c>docs.uploads.kind</c> values; that column is deliberately un-CHECKed (1301)
/// and this set is what keeps it from becoming free text.
/// </remarks>
public static class PayoutDocumentKinds
{
    public const string BankStatement = "bank_statement";
    public const string PassbookFirstPage = "passbook_first_page";
    public const string LankaQrCode = "lankaqr_code";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { BankStatement, PassbookFirstPage, LankaQrCode };

    /// <summary>
    /// Which column on the profile the upload lands in — <see langword="true"/> for the LankaQR
    /// image, <see langword="false"/> for the two proof-of-account slots.
    /// </summary>
    public static bool IsLankaQr(string kind) => string.Equals(kind, LankaQrCode, StringComparison.Ordinal);
}

/// <summary>
/// The codes this service raises, named once so a route does not have to reach across the whole
/// kernel registry to say "not approved".
/// </summary>
/// <remarks>
/// <b>Aliases, not declarations.</b> Every one is declared in <see cref="MageRideErrors"/> — the
/// platform keeps a single, collision-free key space (D3' §0), and admin-bff and the Fleet Portal
/// branch on these codes without referencing this assembly. C057 declared the GTFS lifecycle's
/// three the same way.
/// <para>
/// <c>payout-profile-not-verified</c> is deliberately absent: BR-31.1's 409 was already in the
/// registry, declared by C002 because subscription-svc raises it too. It is referenced directly as
/// <see cref="MageRideErrors.PayoutProfileNotVerified"/>.
/// </para>
/// </remarks>
public static class FleetErrors
{
    /// <inheritdoc cref="MageRideErrors.FleetNotApproved"/>
    public static readonly ErrorCode FleetNotApproved = MageRideErrors.FleetNotApproved;

    /// <inheritdoc cref="MageRideErrors.NotFleetMember"/>
    public static readonly ErrorCode NotFleetMember = MageRideErrors.NotFleetMember;

    /// <inheritdoc cref="MageRideErrors.FleetRoleInsufficient"/>
    public static readonly ErrorCode FleetRoleInsufficient = MageRideErrors.FleetRoleInsufficient;

    /// <inheritdoc cref="MageRideErrors.FleetNotFound"/>
    public static readonly ErrorCode FleetNotFound = MageRideErrors.FleetNotFound;

    /// <inheritdoc cref="MageRideErrors.PayoutProfileNotFound"/>
    public static readonly ErrorCode PayoutProfileNotFound = MageRideErrors.PayoutProfileNotFound;

    /// <inheritdoc cref="MageRideErrors.BusinessRegistrationExists"/>
    public static readonly ErrorCode BusinessRegistrationExists = MageRideErrors.BusinessRegistrationExists;

    /// <inheritdoc cref="MageRideErrors.FleetMemberExists"/>
    public static readonly ErrorCode MemberExists = MageRideErrors.FleetMemberExists;
}
