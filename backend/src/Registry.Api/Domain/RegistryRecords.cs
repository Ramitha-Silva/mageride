namespace MageRide.Registry.Domain;

/// <summary>A row of <c>registry.vehicles</c>, as far as the walking skeleton reads it.</summary>
/// <remarks>
/// Deliberately not the whole table. <c>rejection_reason</c>, <c>vehicle_photo_url</c> and the
/// AL-24 Mode B billing columns belong to C028/C029 and to the Fleet Portal; reading them here
/// would make this the full vehicle service.
/// </remarks>
public sealed record Vehicle(
    Guid Id,
    Guid OwnerId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string OnboardingStatus,
    string DispatchState,
    string DriverName,
    string? DriverPhotoUrl,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Whether this vehicle may be selected as the driver's live publisher (US-9.6). Approval is
    /// the gate — a PENDING registration has not been checked and a DEACTIVATED one is off the
    /// map (US-2.16).
    /// </summary>
    public bool IsSelectable => Status == RegistrationStatuses.Approved;
}

/// <summary>
/// A row of <c>registry.driver_profiles</c> — the driver's registry-side identity, plus the one
/// vehicle they have selected to go live on (US-9.6/US-9.7, migration 0308).
/// </summary>
/// <remarks>
/// This slice writes <see cref="DisplayName"/> and <see cref="PhotoUrl"/> as a side effect of
/// registering a vehicle. Profile Setup proper (<c>PUT /v1/drivers/profile</c>: driving-licence
/// upload, AL-29 field extraction, the NIC and licence-class fields) is C029's.
/// </remarks>
/// <param name="NicNo">
/// AL-29. Extracted from the licence scan, or typed by the driver when it was unclear. The value
/// itself carries no provenance — <c>registry.document_fields</c> does — so a NIC here is the
/// current best answer and not necessarily a verified one.
/// </param>
/// <param name="AllowedVehicleTypes">The licence classes (AL-29). Same provenance caveat.</param>
/// <param name="VerifiedAt">
/// When Profile Setup last came back with nothing pending. Cleared again by a re-submission that
/// introduces a manual or doubtful field, because "verified" has to mean the current values.
/// </param>
public sealed record DriverProfile(
    Guid DriverId,
    string DisplayName,
    string? PhotoUrl,
    Guid? ActiveVehicleId,
    DateTimeOffset? ActiveVehicleSelectedAt,
    string? NicNo = null,
    string[]? AllowedVehicleTypes = null,
    DateTimeOffset? VerifiedAt = null);
