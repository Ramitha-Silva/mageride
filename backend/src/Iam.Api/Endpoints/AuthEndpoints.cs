using MageRide.Iam.Otp;
using MageRide.Iam.Sessions;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MageRide.Iam.Endpoints;

/// <summary>
/// <c>/v1/auth</c> — the walking skeleton's slice of <c>backend/contracts/iam.yaml</c>: the phone
/// OTP trio, refresh and logout.
/// </summary>
/// <remarks>
/// <para>
/// Every failure leaves as RFC 7807 by throwing a <see cref="MageRideException"/>; the kernel's
/// handler turns it into the registry code the contract's <c>x-error-codes</c> names. Handlers
/// therefore only describe the success shape.
/// </para>
/// <para>
/// The four portal routes the contract also declares (<c>/google</c>, <c>/apple</c>,
/// <c>/password</c>, <c>/admin/auth/login</c>) and <c>/mqtt-token</c> are C026's. They are
/// deliberately unmapped rather than stubbed — an endpoint that answers 200 with a token nobody
/// verified is worse than a 404.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    private const string BearerPrefix = "Bearer ";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var auth = endpoints.MapGroup("/v1/auth").WithTags("auth");

        auth.MapPost("/otp/request", RequestOtpAsync).AllowAnonymous().WithName("requestOtp");
        auth.MapPost("/otp/resend", ResendOtpAsync).AllowAnonymous().WithName("resendOtp");
        auth.MapPost("/otp/verify", VerifyOtpAsync).AllowAnonymous().WithName("verifyOtp");

        // Anonymous to ASP.NET Core: the contract's `refreshToken` scheme is a bearer of an
        // opaque token, which the JWT handler cannot validate. ISessionService authenticates it.
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().WithName("refreshSession");

        auth.MapPost("/logout", LogoutAsync).RequireAuthorization().WithName("logout");

        return endpoints;
    }

    private static async Task<Ok<RequestOtpResponse>> RequestOtpAsync(
        RequestOtpBody? body, HttpContext context, IOtpService otp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(otp);

        var app = RequireApp(body?.Role);

        var dispatched = await otp.RequestAsync(
            new RequestOtpCommand(body?.Phone, body?.DeviceId, app, Platform(context)), cancellationToken);

        return TypedResults.Ok(new RequestOtpResponse(
            AuthId: dispatched.AuthId.ToString(),
            AttemptsRemaining: dispatched.AttemptsRemaining,
            CooldownSeconds: dispatched.CooldownSeconds,
            // A blocked account never gets this far — it is answered 403 user-blocked. The field
            // is required by the contract, so it is present and always false on the 200 path.
            IsBlocked: false));
    }

    private static async Task<Ok<ResendOtpResponse>> ResendOtpAsync(
        ResendOtpBody? body, IOtpService otp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(otp);

        var dispatched = await otp.ResendAsync(RequireAuthId(body?.AuthId), cancellationToken);

        return TypedResults.Ok(new ResendOtpResponse(dispatched.AttemptsRemaining, dispatched.CooldownSeconds));
    }

    private static async Task<Ok<VerifyOtpResponse>> VerifyOtpAsync(
        VerifyOtpBody? body, HttpContext context, IOtpService otp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(otp);

        var verified = await otp.VerifyAsync(
            RequireAuthId(body?.AuthId), body?.Otp, body?.DeviceId, Platform(context), cancellationToken);

        return TypedResults.Ok(new VerifyOtpResponse(
            AccessToken: verified.Session.Access.Value,
            RefreshToken: verified.Session.RefreshToken,
            ExpiresIn: verified.Session.Access.ExpiresInSeconds,
            User: UserProfileResponse.From(verified.User, verified.Roles),
            IsNewUser: verified.IsNewUser));
    }

    private static async Task<Ok<TokenPairResponse>> RefreshAsync(
        RefreshSessionBody? body, HttpContext context, ISessionService sessions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);

        // The contract sends the token in the body and declares the `refreshToken` bearer scheme;
        // the KMP client (C013) sends both. Body first, header as the fallback.
        var token = body?.RefreshToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                token = authorization[BearerPrefix.Length..].Trim();
            }
        }

        var rotated = await sessions.RotateAsync(token, cancellationToken);

        return TypedResults.Ok(TokenPairResponse.From(rotated));
    }

    private static async Task<NoContent> LogoutAsync(
        HttpContext context, ISessionService sessions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);

        var userId = context.User.RequireSubjectId();
        var app = context.User.App() ?? MageRideApps.Passenger;

        // The access token's jti is its session's jti, so a logout ends exactly the session the
        // caller is holding rather than whatever happens to be active for the pair.
        Guid? sessionId =
            Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value, out var jti) ? jti : null;

        await sessions.LogoutAsync(userId, app, sessionId, cancellationToken);

        // 204 whether or not anything was revoked (contract: "Already-revoked sessions also answer 204").
        return TypedResults.NoContent();
    }

    /// <summary>The app surface a request belongs to, defaulting to passenger (AL-08).</summary>
    private static string RequireApp(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return MageRideApps.Passenger;
        }

        if (role is MageRideApps.Passenger or MageRideApps.Driver)
        {
            return role;
        }

        // "reseller" is neither a role nor a capability (AL-01), and the portals do not use this
        // endpoint (AL-07) — anything but the two app surfaces is a client bug.
        throw new MageRideValidationException(new Dictionary<string, string[]>
        {
            ["role"] = ["role must be 'passenger' or 'driver'."],
        });
    }

    private static Guid RequireAuthId(string? authId)
    {
        if (!Guid.TryParse(authId, out var parsed))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["authId"] = ["authId is required and must be the identifier returned by /v1/auth/otp/request."],
            });
        }

        return parsed;
    }

    /// <summary>
    /// The device platform, from the gateway's <c>X-Platform</c> header (D-31).
    /// </summary>
    /// <remarks>
    /// <c>iam.devices.platform</c> is <c>NOT NULL CHECK (platform IN ('android','ios'))</c>, but
    /// neither otp/request nor otp/verify carries a platform field and the gateway does not
    /// require the header. Android is the default because it is the only platform the walking
    /// skeleton ships (C025). Recorded in the C020 handoff as a contract gap.
    /// </remarks>
    private static string Platform(HttpContext context) =>
        string.Equals(
            context.Request.Headers[MageRideHeaders.Platform].ToString(),
            ClientPlatforms.Ios,
            StringComparison.OrdinalIgnoreCase)
            ? ClientPlatforms.Ios
            : ClientPlatforms.Android;
}
