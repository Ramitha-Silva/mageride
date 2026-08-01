namespace MageRide.Registry.Domain;

/// <summary>
/// A row of <c>registry.driver_eligible_vehicles</c> — which vehicles a driver may go live on
/// and how they came by them (US-9.6, US-13.9; migration 0310).
/// </summary>
/// <param name="Source">
/// <see cref="EligibilitySources.Owned"/> for a vehicle the driver registered, or
/// <see cref="EligibilitySources.Assigned"/> for one a fleet lent them (US-13.9).
/// </param>
/// <param name="FleetId">The assigning fleet, for the "Temporarily assigned to me" group. Null when owned.</param>
/// <param name="IsGoLiveEligible">
/// APPROVED and not E-03 suspended. The projection answers it once so registry, dispatch and
/// trip-state cannot each derive it differently.
/// </param>
public sealed record EligibleVehicle(
    Guid DriverId,
    Guid VehicleId,
    string Source,
    Guid? FleetId,
    Guid OwnerId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string DispatchState,
    string OnboardingStatus,
    string DriverName,
    string? DriverPhotoUrl,
    DateTimeOffset CreatedAt,
    bool IsGoLiveEligible)
{
    public bool IsOwned => Source == EligibilitySources.Owned;
}

/// <summary>How a driver came by a vehicle (<c>registry.driver_eligible_vehicles.source</c>).</summary>
public static class EligibilitySources
{
    /// <summary>The driver registered it — Mode C in the Driver App (AL-27), Mode A/B in the Fleet Portal.</summary>
    public const string Owned = "owned";

    /// <summary>A fleet lent it to them (US-13.9, AL-23). Shown in a separate group and auto-expiring.</summary>
    public const string Assigned = "assigned";
}

/// <summary>A row of <c>registry.shares</c> — a Mode B tracking grant (D-22, US-4.1/4.2/4.3b).</summary>
/// <remarks>
/// A share is <b>visibility, not operation</b>: US-4.1 shares "tracking access", and the grantee
/// sees the vehicle's live position. The right to <em>drive</em> a fleet vehicle is
/// <c>registry.fleet_assignments</c> (US-13.9), which is a different table and a different screen.
/// </remarks>
public sealed record ShareGrant(
    Guid Id,
    Guid VehicleId,
    Guid GranteeUserId,
    string State,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>Whether the grantee can currently see the vehicle.</summary>
    public bool IsLive(DateTimeOffset now) =>
        State == ShareStates.Accepted && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>The <c>registry.shares.state</c> lifecycle (0306).</summary>
public static class ShareStates
{
    /// <summary>Granted, awaiting the sharee's acceptance. Visibility begins at ACCEPTED, not here (US-4.3b).</summary>
    public const string Pending = "PENDING";

    public const string Accepted = "ACCEPTED";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
}

/// <summary>
/// One entitled passenger on a Mode B vehicle's roster — a row of <c>subscription.grants</c>
/// (US-4.7, AL-23/AL-24).
/// </summary>
public sealed record Subscriber(
    Guid GrantId,
    Guid VehicleId,
    Guid PassengerId,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UnsubscribedAt);

/// <summary>A row of <c>subscription.access_requests</c> — a passenger asking for Mode B access (US-4.5).</summary>
public sealed record AccessRequest(
    Guid Id, Guid VehicleId, Guid PassengerId, string Status, DateTimeOffset RequestedAt);

// Δ AL-57 — `DriverPayout` REMOVED: `registry.driver_payouts` is dropped with D-11.

/// <summary>
/// The event types registry-svc writes to <c>registry.outbox</c> (migration 0309).
/// </summary>
public static class RegistryEventTypes
{
    /// <summary>
    /// D-22. fanout-svc turns it into a directed <c>RemoveFromGroupAsync</c> inside 200 ms, so the
    /// passenger loses live visibility without waiting for the next cell crossing (D6' §5.2).
    /// </summary>
    public const string ShareRevoked = "share.revoked";

    /// <summary>The counterpart, so a consumer's cache can be warmed as well as invalidated (D-23).</summary>
    public const string ShareGranted = "share.granted";

    /// <summary>US-2.16. The vehicle leaves the map and every live grant on it is revoked with it.</summary>
    public const string VehicleDeactivated = "vehicle.deactivated";
}

/// <summary>
/// One version of a driver's bank &amp; payout profile (AL-58, migration 0316).
/// </summary>
/// <remarks>
/// Shaped exactly like <c>registry.fleet_payout_profiles</c>: the platform must not hold a payee's
/// bank details in two shapes. <c>LankaqrUploadId</c> is load-bearing beyond payouts — AL-59 makes
/// a LankaQR ride payment the driver's OWN bank QR, and this is the image a passenger scans.
/// </remarks>
public sealed record DriverPayoutProfile(
    Guid Id,
    Guid DriverId,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    Guid? ProofUploadId,
    Guid? LankaqrUploadId,
    string Status,
    string? RejectionReason,
    Guid? VerifiedBy,
    DateTimeOffset? VerifiedAt)
{
    public bool IsPending => Status == DriverPayoutStatuses.PendingVerification;

    public bool IsVerified => Status == DriverPayoutStatuses.Verified;
}

/// <summary><c>registry.driver_payout_profiles.status</c> (migration 0316).</summary>
public static class DriverPayoutStatuses
{
    public const string PendingVerification = "pending_verification";
    public const string Verified = "verified";
    public const string Rejected = "rejected";

    /// <summary>The incumbent an approved edit displaced (0313's fourth value, mirrored).</summary>
    public const string Superseded = "superseded";
}

/// <summary>The document slots an AL-58 payout profile carries.</summary>
public static class DriverPayoutDocumentKinds
{
    public const string BankStatement = "bank_statement";
    public const string PassbookFirstPage = "passbook_first_page";

    /// <summary>AL-59: the driver's own bank-app LankaQR, which a passenger scans to pay them.</summary>
    public const string LankaqrCode = "lankaqr_code";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BankStatement, PassbookFirstPage, LankaqrCode,
    };
}
