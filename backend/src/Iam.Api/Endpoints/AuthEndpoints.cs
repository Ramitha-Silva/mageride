using MageRide.Iam.Auth;
using MageRide.Iam.Mqtt;
using MageRide.Iam.Otp;
using MageRide.Iam.Sessions;
using MageRide.Iam.SignIn;
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
/// <c>/v1/auth</c> and <c>/v1/admin/auth</c> — every sign-in surface AL-07 lists, the token
/// lifecycle, and the MQTT session token E-02 decouples from it.
/// </summary>
/// <remarks>
/// <para>
/// Every failure leaves as RFC 7807 by throwing a <see cref="MageRideException"/>; the kernel's
/// handler turns it into the registry code the contract's <c>x-error-codes</c> names. Handlers
/// therefore only describe the success shape.
/// </para>
/// <para>
/// <b>There is no MFA route and there must never be one (AL-37).</b> <c>/v1/admin/auth/mfa/verify</c>
/// and the TOTP enrolment pair are listed as removed at the top of
/// <c>backend/contracts/iam.yaml</c>; D3' §0 and D7' §4.2 still carry pre-AL-37 wording and are
/// superseded. Every sign-in below answers with a token pair or an error — none of them can
/// return a challenge.
/// </para>
/// <para>
/// Profile, saved addresses, the nine-role RBAC surface and PDPA are C027 and stay unmapped.
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

        // Portal sign-in (AL-07). Anonymous by definition — these are how a caller *gets* a
        // credential — and each carries its own surface rule in the handler.
        auth.MapPost("/password", SignInWithPasswordAsync).AllowAnonymous().WithName("signInWithPassword");
        auth.MapPost("/google", SignInWithGoogleAsync).AllowAnonymous().WithName("signInWithGoogle");
        auth.MapPost("/apple", SignInWithAppleAsync).AllowAnonymous().WithName("signInWithApple");

        auth.MapPost("/mqtt-token", IssueMqttTokenAsync).RequireAuthorization().WithName("issueMqttToken");

        // Its own path, not a member of the /v1/auth group: D3' Δ 2026-06-28 item 5 puts the
        // Admin Portal's sign-in under /v1/admin, and the gateway routes /v1/admin/auth/** here.
        endpoints.MapPost("/v1/admin/auth/login", AdminLoginAsync)
            .AllowAnonymous()
            .WithName("adminLogin")
            .WithTags("auth");

        return endpoints;
    }

    private static async Task<Ok<RequestOtpResponse>> RequestOtpAsync(
        RequestOtpBody? body, HttpContext context, IOtpService otp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(otp);

        var app = RequireApp(body?.Role);

        var dispatched = await otp.RequestAsync(
            new RequestOtpCommand(body?.Phone, body?.DeviceId, app, Platform(context), body?.FcmToken),
            cancellationToken);

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
            User: UserProfileResponse.From(verified.User, verified.Principal.Roles, verified.Principal.Fleet),
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

    /// <summary><c>POST /v1/auth/password</c> — Admin and Fleet portals (AL-07, no MFA per AL-37).</summary>
    private static async Task<Ok<AuthSessionResponse>> SignInWithPasswordAsync(
        PasswordLoginBody? body,
        HttpContext context,
        IPortalSignInService portals,
        InternalAccessPolicy internalAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(internalAccess);

        RefuseApps(context);

        // No surface: the contract puts both portals on this route, so the account's roles decide
        // which one it is signing in to.
        var signedIn = await portals.WithPasswordAsync(
            new PasswordSignInCommand(
                body?.Email, body?.Password, Surface: null, WebDeviceKeys.From(context), internalAccess.ClientAddress(context)),
            cancellationToken);

        return TypedResults.Ok(AuthSessionResponse.From(signedIn));
    }

    /// <summary><c>POST /v1/auth/google</c> — Admin and Fleet portals (AL-07).</summary>
    private static async Task<Ok<AuthSessionResponse>> SignInWithGoogleAsync(
        IdTokenLoginBody? body,
        HttpContext context,
        IPortalSignInService portals,
        InternalAccessPolicy internalAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(internalAccess);

        RefuseApps(context);

        var signedIn = await portals.WithProviderAsync(
            new ProviderSignInCommand(
                IdentityProviders.Google,
                body?.IdToken,
                Surface: null,
                WebDeviceKeys.From(context),
                internalAccess.ClientAddress(context)),
            cancellationToken);

        return TypedResults.Ok(AuthSessionResponse.From(signedIn));
    }

    /// <summary><c>POST /v1/auth/apple</c> — Fleet Portal only (AL-07).</summary>
    private static async Task<Ok<AuthSessionResponse>> SignInWithAppleAsync(
        IdTokenLoginBody? body,
        HttpContext context,
        IPortalSignInService portals,
        InternalAccessPolicy internalAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(internalAccess);

        RefuseApps(context);

        var signedIn = await portals.WithProviderAsync(
            new ProviderSignInCommand(
                IdentityProviders.Apple,
                body?.IdToken,
                // Apple is the one method AL-07 gives to a single surface, so the surface is
                // named here rather than derived — an admin with an Apple ID does not get in.
                MageRideApps.Fleet,
                WebDeviceKeys.From(context),
                internalAccess.ClientAddress(context)),
            cancellationToken);

        return TypedResults.Ok(AuthSessionResponse.From(signedIn));
    }

    /// <summary>
    /// <c>POST /v1/admin/auth/login</c> — password or a Google OIDC authorization code
    /// (Δ 2026-06-28 item 5, AL-37). No MFA step follows either arm.
    /// </summary>
    private static async Task<Ok<AuthSessionResponse>> AdminLoginAsync(
        AdminLoginBody? body,
        HttpContext context,
        IPortalSignInService portals,
        IGoogleAuthCodeExchange googleCodes,
        InternalAccessPolicy internalAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portals);
        ArgumentNullException.ThrowIfNull(googleCodes);
        ArgumentNullException.ThrowIfNull(internalAccess);

        RefuseApps(context);

        var deviceKey = WebDeviceKeys.From(context);
        var address = internalAccess.ClientAddress(context);
        var hasCode = !string.IsNullOrWhiteSpace(body?.GoogleAuthCode);
        var hasPassword = !string.IsNullOrWhiteSpace(body?.Email) || !string.IsNullOrWhiteSpace(body?.Password);

        if (hasCode == hasPassword)
        {
            // The contract's body is a oneOf. Both arms or neither is a client bug, and picking
            // one for them would silently ignore a credential they meant to send.
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["body"] = ["Send either {email, password} or {googleAuthCode}, not both and not neither."],
            });
        }

        PortalSignIn signedIn;
        if (hasCode)
        {
            var idToken = await googleCodes.ExchangeAsync(body!.GoogleAuthCode!, body.RedirectUri, cancellationToken);

            signedIn = await portals.WithProviderAsync(
                new ProviderSignInCommand(
                    IdentityProviders.Google, idToken, MageRideApps.Admin, deviceKey, address),
                cancellationToken);
        }
        else
        {
            signedIn = await portals.WithPasswordAsync(
                new PasswordSignInCommand(body!.Email, body.Password, MageRideApps.Admin, deviceKey, address),
                cancellationToken);
        }

        return TypedResults.Ok(AuthSessionResponse.From(signedIn));
    }

    /// <summary><c>POST /v1/auth/mqtt-token</c> — the E-02 session credential.</summary>
    private static async Task<Ok<IssueMqttTokenResponse>> IssueMqttTokenAsync(
        IssueMqttTokenBody? body, HttpContext context, IMqttTokenService tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tokens);

        var issued = await tokens.IssueAsync(
            new MqttTokenCommand(
                context.User.RequireSubjectId(),
                context.User.DeviceId(),
                body?.VehicleId,
                body?.DeviceId,
                body?.RideId),
            cancellationToken);

        return TypedResults.Ok(new IssueMqttTokenResponse(issued.MqttJwt, issued.ExpiresIn));
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

    /// <summary>
    /// Refuses a portal sign-in that arrived from one of the apps (AL-07).
    /// </summary>
    /// <remarks>
    /// The contract puts this at the gateway ("the gateway rejects this route for an app
    /// <c>X-Platform</c>"), and it is repeated here because the fence matters more than the
    /// hop it is enforced at: the passenger and driver apps are Phone OTP only, and a build that
    /// reached iam-svc directly — the compose stack, a port-forward, a future BFF — would
    /// otherwise have a way in that AL-07 says does not exist. Only an explicit app platform is
    /// refused; a browser sends no <c>X-Platform</c> at all.
    /// </remarks>
    private static void RefuseApps(HttpContext context)
    {
        var platform = context.Request.Headers[MageRideHeaders.Platform].ToString();

        if (string.Equals(platform, ClientPlatforms.Android, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(platform, ClientPlatforms.Ios, StringComparison.OrdinalIgnoreCase))
        {
            throw new MageRideException(
                MageRideErrors.Forbidden,
                "The passenger and driver apps sign in by phone OTP only (AL-07).");
        }
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
    /// <c>iam.devices.platform</c> is <c>NOT NULL CHECK (platform IN ('android','ios','web'))</c>,
    /// but neither otp/request nor otp/verify carries a platform field and the gateway does not
    /// require the header. Android is the default on the OTP routes because it is the only app
    /// platform shipped so far (C025); a portal sign-in does not come through here and is always
    /// <c>web</c>. Recorded in the C020 handoff as a contract gap.
    /// </remarks>
    private static string Platform(HttpContext context) =>
        string.Equals(
            context.Request.Headers[MageRideHeaders.Platform].ToString(),
            ClientPlatforms.Ios,
            StringComparison.OrdinalIgnoreCase)
            ? ClientPlatforms.Ios
            : ClientPlatforms.Android;
}
