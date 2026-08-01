using MageRide.Shared.Auth;

namespace MageRide.Iam.Domain;

/// <summary>A row of <c>iam.users</c>, as far as the auth half of iam-svc reads it.</summary>
/// <remarks>
/// Deliberately not the whole table. Emergency contacts, notification preferences and the PDPA
/// columns belong to C027; reading them here would make this the profile service.
/// </remarks>
public sealed record IamUser(
    Guid Id,
    string? Phone,
    string? Email,
    string Role,
    string? FirstName,
    string? PhotoUrl,
    string Language,
    string? OperatingCityCode,
    string DefaultPaymentMethod,
    bool IsBlocked,
    DateTimeOffset CreatedAt);

/// <summary>A row of <c>iam.otp_attempts</c> — one in-flight sign-in (D-32).</summary>
public sealed record OtpAttempt(
    Guid Id,
    string Phone,
    Guid AuthId,
    byte[] OtpHash,
    short Attempts,
    DateTimeOffset ExpiresAt,
    bool Verified,
    string? DeviceId,
    string? App,
    string? FcmToken,
    DateTimeOffset CreatedAt);

/// <summary>A row of <c>iam.sessions</c> — the refresh-token record (D-29).</summary>
/// <param name="FamilyId">
/// The rotation lineage this session belongs to. A sign-in starts a family; a rotation keeps it.
/// Replaying a spent token revokes only its own family, so a token left over from an older
/// sign-in cannot end the session a newer sign-in just opened (0106).
/// </param>
public sealed record AuthSession(
    Guid Jti,
    Guid UserId,
    Guid DeviceId,
    string App,
    Guid FamilyId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// A row of <c>iam.user_credentials</c> — the portal password verifier and the AL-37 lock-out
/// that replaced the MFA step (0107).
/// </summary>
public sealed record UserCredential(
    Guid UserId,
    string PasswordHash,
    DateTimeOffset PasswordUpdatedAt,
    short FailedAttempts,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastLoginAt)
{
    /// <summary>Whether the lock-out is in force at <paramref name="now"/>.</summary>
    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

/// <summary>A row of <c>iam.federated_identities</c> — a Google or Apple binding (AL-07, 0107).</summary>
public sealed record FederatedIdentity(
    Guid Id,
    Guid UserId,
    string Provider,
    string Subject,
    string? Email,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastLoginAt);

// A row of iam.fleet_members — the fleet_role / fleet_id claim pair (AL-03) — is
// MageRide.Shared.Auth.FleetScope. It moved into the kernel with the URD §2.3 matrix (C062): the
// evaluator that narrows the fleet_owner column by it is now shared, and two records of the same
// shape either side of that boundary is how the two start disagreeing.

/// <summary>
/// Everything a session and its access token need about the account behind it.
/// </summary>
/// <remarks>
/// One record for all four surfaces, which is what makes "the same unified account token" true:
/// phone-OTP and portal sign-in differ in how the account was identified, never in what the
/// resulting token says.
/// </remarks>
/// <param name="Roles">Every canonical role held; permissions are their union (AL-06).</param>
/// <param name="Fleet">Fleet membership, when there is one. Absent for every non-fleet account.</param>
public sealed record SessionPrincipal(
    Guid UserId,
    IReadOnlyList<string> Roles,
    FleetScope? Fleet);

/// <summary>
/// What <c>POST /v1/auth/mqtt-token</c> needs to know about a vehicle before it mints a
/// credential for it (E-02).
/// </summary>
/// <param name="OwnerId"><c>registry.vehicles.owner_id</c> — the driver who may publish for it.</param>
public sealed record VehiclePublisher(Guid VehicleId, Guid OwnerId, string Status);

/// <summary>
/// A non-terminal Mode C ride, as the MQTT token endpoint reads it to size its TTL (E-02).
/// </summary>
/// <param name="StartedAt">When the ride was created. The only temporal anchor
/// <c>rides.rides</c> offers — nothing in D4' §5 records an expected end.</param>
public sealed record ActiveRide(Guid RideId, Guid DriverId, Guid? VehicleId, string State, DateTimeOffset StartedAt);
