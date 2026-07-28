using MageRide.Iam.Domain;
using MageRide.Iam.Sessions;

namespace MageRide.Iam.Endpoints;

// The wire shapes of backend/contracts/iam.yaml, one record per schema. Nullable where the
// contract makes a field optional, and nullable where it does not either — a missing required
// field must come back as 400 validation-failed, not as a framework 400 with no error code.

/// <summary><c>POST /v1/auth/otp/request</c>.</summary>
public sealed record RequestOtpBody(string? Phone, string? DeviceId, string? FcmToken, string? Role);

/// <summary><c>POST /v1/auth/otp/request</c> — 200.</summary>
public sealed record RequestOtpResponse(string AuthId, int AttemptsRemaining, int CooldownSeconds, bool IsBlocked);

/// <summary><c>POST /v1/auth/otp/resend</c>.</summary>
public sealed record ResendOtpBody(string? AuthId);

/// <summary><c>POST /v1/auth/otp/resend</c> — 200.</summary>
public sealed record ResendOtpResponse(int AttemptsRemaining, int CooldownSeconds);

/// <summary><c>POST /v1/auth/otp/verify</c>.</summary>
public sealed record VerifyOtpBody(string? AuthId, string? Otp, string? DeviceId);

/// <summary><c>POST /v1/auth/refresh</c>.</summary>
public sealed record RefreshSessionBody(string? RefreshToken);

/// <summary><c>_shared.yaml#/components/schemas/TokenPair</c>.</summary>
public sealed record TokenPairResponse(string AccessToken, string RefreshToken, int ExpiresIn)
{
    public static TokenPairResponse From(IssuedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new TokenPairResponse(session.Access.Value, session.RefreshToken, session.Access.ExpiresInSeconds);
    }
}

/// <summary><c>POST /v1/auth/otp/verify</c> — 200: a flattened <c>allOf(TokenPair, {user, isNewUser})</c>.</summary>
public sealed record VerifyOtpResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    UserProfileResponse User,
    bool IsNewUser);

/// <summary>
/// <c>iam.yaml#/components/schemas/UserProfile</c>, as far as the skeleton populates it.
/// </summary>
/// <remarks>
/// <c>fleetRole</c> and <c>notifPrefs</c> are absent: fleet membership is C058 and notification
/// preferences are part of the profile surface C027 owns. Every field the contract marks required
/// — <c>userId</c>, <c>phone</c>, <c>role</c> — is here.
/// </remarks>
public sealed record UserProfileResponse(
    string UserId,
    string Phone,
    string? Email,
    string? FirstName,
    string? PhotoUrl,
    string Role,
    IReadOnlyList<string> Roles,
    string Language,
    string? OperatingCityCode,
    string DefaultPaymentMethod,
    DateTimeOffset CreatedAt)
{
    public static UserProfileResponse From(IamUser user, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        return new UserProfileResponse(
            UserId: user.Id.ToString(),
            Phone: user.Phone ?? string.Empty,
            Email: user.Email,
            FirstName: user.FirstName,
            PhotoUrl: user.PhotoUrl,
            Role: user.Role,
            Roles: roles,
            Language: user.Language,
            OperatingCityCode: user.OperatingCityCode,
            DefaultPaymentMethod: user.DefaultPaymentMethod,
            CreatedAt: user.CreatedAt);
    }
}
