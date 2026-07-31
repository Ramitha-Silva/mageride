namespace MageRide.Fleet.Domain;

/// <summary>A row of <c>registry.fleets</c> — the Fleet Owner organisation (AL-03, migration 0301 + 0313).</summary>
public sealed record FleetOrganisation(
    Guid Id,
    Guid OwnerId,
    string Name,
    string? BusinessReg,
    string? ContactPhone,
    string? ContactEmail,
    string? Address,
    string Status,
    string? RejectionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsApproved => string.Equals(Status, FleetStatuses.Approved, StringComparison.Ordinal);
}

/// <summary>A bare row of <c>iam.fleet_members</c> — the seat, without the person (migration 0302).</summary>
public sealed record FleetMembership(Guid FleetId, Guid UserId, string FleetRole, DateTimeOffset CreatedAt);

/// <summary>A row of <c>iam.fleet_members</c> joined to the person it names (migration 0302).</summary>
/// <remarks>
/// No phone number. The view this is read through (<c>iam.fleet_members_fleet</c>, migration 1806)
/// does not project one: a sub-user's mobile is their own, and an org's team list is not a reason
/// to hand it to whoever holds the Owner seat.
/// </remarks>
public sealed record FleetMember(
    Guid FleetId,
    Guid UserId,
    string FleetRole,
    string? Email,
    string? Name,
    bool IsBlocked,
    DateTimeOffset CreatedAt);

/// <summary>
/// A row of <c>registry.fleet_payout_profiles</c> — one version of the org's bank details (AL-49).
/// </summary>
/// <remarks>
/// The table is versioned: an edit to a <c>verified</c> profile inserts a new row rather than
/// overwriting one, because BR-31.1 keeps Paid subscriptions collecting against the last verified
/// snapshot while the edit is in the queue. <see cref="Status"/> is therefore a property of this
/// <em>version</em>, never of the organisation.
/// </remarks>
public sealed record PayoutProfile(
    Guid Id,
    Guid FleetId,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    Guid? ProofUploadId,
    Guid? LankaqrUploadId,
    string Status,
    string? RejectionReason,
    Guid? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsVerified => string.Equals(Status, PayoutProfileStatuses.Verified, StringComparison.Ordinal);

    public bool IsPending =>
        string.Equals(Status, PayoutProfileStatuses.PendingVerification, StringComparison.Ordinal);
}

/// <summary>A row of <c>registry.fleet_vehicles_fleet</c> — the org's roster (migration 1806).</summary>
public sealed record FleetVehicle(
    Guid FleetId,
    Guid VehicleId,
    string Mode,
    string RegistrationNumber,
    string VehicleType,
    string Status,
    string DispatchState,
    string? ModeBBilling,
    // int, not long: registry.vehicles.default_monthly_fare_minor is INTEGER (migration 0303)
    // while fleet.yaml types the field int64. The column is the narrower of the two and therefore
    // the real bound — about Rs 21 million a month — so the endpoint refuses anything wider rather
    // than letting Postgres do it with a 22003. Raised in the C058 handoff.
    int? DefaultMonthlyFareMinor,
    string DriverName);

/// <summary>A row of the Verification Officer's fleet-org queue (AL-39, SCR-AP-003).</summary>
public sealed record FleetQueueRow(
    Guid FleetId,
    string Name,
    string? BusinessReg,
    string? ContactPhone,
    string Status,
    string? PayoutProfileStatus,
    int DocumentCount,
    DateTimeOffset CreatedAt);

/// <summary>A <c>docs.uploads</c> row this service wrote (D-36, migration 1301).</summary>
public sealed record PayoutDocument(
    Guid Id, Guid OwnerId, string StorageUrl, string Kind, DateTimeOffset? AutoDeleteAt, DateTimeOffset CreatedAt);

/// <summary>What an officer's decision produced, for the internal plane's response.</summary>
public sealed record VerificationDecision(
    FleetOrganisation Fleet, PayoutProfile? PayoutProfile);
