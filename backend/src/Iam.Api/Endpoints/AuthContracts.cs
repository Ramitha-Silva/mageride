using MageRide.Iam.Domain;
using MageRide.Iam.Sessions;

namespace MageRide.Iam.Endpoints;

// The wire shapes of backend/contracts/iam.yaml, one record per schema. Nullable where the
// contract makes a field optional, and nullable where it does not either — a missing required
// field must come back as 400 validation-failed, not as a framework 400 with no error code.

/// <summary><c>POST /v1/auth/otp/request</c>.</summary>
public sealed record RequestOtpBody(string? Phone, string? DeviceId, string? FcmToken, string? Role);

/// <summary><c>iam.yaml#/components/schemas/PasswordLogin</c> — both password routes.</summary>
public sealed record PasswordLoginBody(string? Email, string? Password);

/// <summary><c>POST /v1/auth/google</c> and <c>POST /v1/auth/apple</c>.</summary>
public sealed record IdTokenLoginBody(string? IdToken);

/// <summary>
/// <c>POST /v1/admin/auth/login</c> — the contract's <c>oneOf(PasswordLogin, {googleAuthCode})</c>
/// as one nullable shape.
/// </summary>
/// <remarks>
/// Minimal APIs bind one body type, and a <c>oneOf</c> is not one. The arms are told apart by
/// which fields arrived, and a body carrying neither — or both — is a 400 rather than a guess.
/// </remarks>
public sealed record AdminLoginBody(string? Email, string? Password, string? GoogleAuthCode, string? RedirectUri);

/// <summary><c>POST /v1/auth/mqtt-token</c>.</summary>
public sealed record IssueMqttTokenBody(string? VehicleId, string? DeviceId, string? RideId);

/// <summary><c>POST /v1/auth/mqtt-token</c> — 200.</summary>
public sealed record IssueMqttTokenResponse(string MqttJwt, int ExpiresIn);

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
/// The 200 of every portal sign-in — Google, Apple, password and the Admin Portal login: a
/// flattened <c>allOf(TokenPair, {user})</c>.
/// </summary>
/// <remarks>
/// The same token pair the apps get, with the same claim set behind it. That is the point of
/// AL-07 having four sign-in methods and one identity model: everything downstream of iam-svc
/// sees one kind of session.
/// </remarks>
public sealed record AuthSessionResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    UserProfileResponse User)
{
    public static AuthSessionResponse From(MageRide.Iam.SignIn.PortalSignIn signedIn)
    {
        ArgumentNullException.ThrowIfNull(signedIn);

        return new AuthSessionResponse(
            signedIn.Session.Access.Value,
            signedIn.Session.RefreshToken,
            signedIn.Session.Access.ExpiresInSeconds,
            UserProfileResponse.From(signedIn.User, signedIn.Principal.Roles, signedIn.Principal.Fleet));
    }
}

/// <summary>
/// <c>iam.yaml#/components/schemas/UserProfile</c>, as far as the skeleton populates it.
/// </summary>
/// <remarks>
/// <c>notifPrefs</c> is absent: notification preferences are part of the profile surface C027
/// owns. Every field the contract marks required — <c>userId</c>, <c>phone</c>, <c>role</c> — is
/// here, and <c>phone</c> is an empty string for a portal identity, which has an email instead
/// (<c>iam.users</c> requires one credential or the other, not both).
/// </remarks>
public sealed record UserProfileResponse(
    string UserId,
    string Phone,
    string? Email,
    string? FirstName,
    string? PhotoUrl,
    string Role,
    IReadOnlyList<string> Roles,
    string? FleetRole,
    string Language,
    string? OperatingCityCode,
    string DefaultPaymentMethod,
    DateTimeOffset CreatedAt)
{
    public static UserProfileResponse From(IamUser user, IReadOnlyList<string> roles, FleetMembership? fleet = null)
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
            FleetRole: fleet?.FleetRole,
            Language: user.Language,
            OperatingCityCode: user.OperatingCityCode,
            DefaultPaymentMethod: user.DefaultPaymentMethod,
            CreatedAt: user.CreatedAt);
    }
}
