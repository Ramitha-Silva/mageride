namespace MageRide.Iam.Domain;

/// <summary>A row of <c>iam.users</c>, as far as the walking skeleton reads it.</summary>
/// <remarks>
/// Deliberately not the whole table. Emergency contacts, notification preferences and the PDPA
/// columns belong to C026/C027; reading them here would make this the profile service.
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
