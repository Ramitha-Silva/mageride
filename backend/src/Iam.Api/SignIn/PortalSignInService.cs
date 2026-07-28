using System.Net;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Iam.Sessions;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.SignIn;

/// <summary>Email + password, for either portal (AL-07).</summary>
/// <param name="Surface">
/// <c>admin</c> or <c>fleet</c> when the route serves one portal, <see langword="null"/> when it
/// serves both and the account's roles decide.
/// </param>
/// <param name="DeviceKey">The browser binding this session gets (<see cref="WebDeviceKeys"/>).</param>
public sealed record PasswordSignInCommand(
    string? Email, string? Password, string? Surface, string DeviceKey, IPAddress? ClientAddress);

/// <summary>Google or Apple, by verified ID token (AL-07).</summary>
public sealed record ProviderSignInCommand(
    string Provider, string? IdToken, string? Surface, string DeviceKey, IPAddress? ClientAddress);

/// <summary>A portal sign-in that succeeded.</summary>
public sealed record PortalSignIn(IssuedSession Session, IamUser User, SessionPrincipal Principal);

/// <summary>
/// The three portal sign-in methods AL-07 lists, and the two controls AL-37 kept when it removed
/// the MFA step from them.
/// </summary>
public interface IPortalSignInService
{
    Task<PortalSignIn> WithPasswordAsync(PasswordSignInCommand command, CancellationToken cancellationToken);

    Task<PortalSignIn> WithProviderAsync(ProviderSignInCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPortalSignInService"/>
/// <remarks>
/// <para>
/// <b>There is no second factor and there is no code path for one (AL-37).</b> Both methods return
/// a token pair or an error; neither can return a challenge. What replaced the factor is here:
/// the failed-attempt lock-out on <c>iam.user_credentials</c>, the session binding the
/// <c>device_key</c> and the C003 unique index give for free, and the optional
/// <see cref="InternalAccessPolicy"/>.
/// </para>
/// <para>
/// <b>No sign-in here creates an account.</b> A portal identity is provisioned — internal roles by
/// a Super Admin (AL-06), fleet users by their owner (AL-03) — so an unknown email or an unlinked
/// Google subject is a 403, not a first sign-in. This is the one place the portal flow differs
/// from the app flow, where a first phone-OTP verify does create the account.
/// </para>
/// </remarks>
public sealed class PortalSignInService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IUserRepository users,
    ICredentialRepository credentials,
    IDeviceRepository devices,
    ISessionService sessionService,
    IOidcTokenVerifier oidc,
    PasswordHasher passwords,
    InternalAccessPolicy internalAccess,
    IOptions<AuthPolicyOptions> options,
    TimeProvider timeProvider,
    ILogger<PortalSignInService> logger) : IPortalSignInService
{
    /// <summary>A portal session's <c>iam.devices.platform</c> (0107).</summary>
    private const string WebPlatform = "web";

    private readonly AuthPolicyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PortalSignIn> WithPasswordAsync(
        PasswordSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var email = RequireEmail(command.Email);
        if (string.IsNullOrEmpty(command.Password))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["password"] = ["password is required."],
            });
        }

        var now = timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var user = await users.FindByEmailAsync(connection, null, email, cancellationToken);
        var credential = user is null
            ? null
            : await credentials.FindAsync(connection, null, user.Id, cancellationToken);

        if (credential?.IsLockedAt(now) == true)
        {
            // Checked before the verifier runs: a locked account must not be a way to keep
            // testing passwords, and the right password must not unlock it early either.
            throw Locked(credential.LockedUntil!.Value - now);
        }

        // Always run a derivation, even with no account and no credential, so the response time
        // does not tell an attacker which addresses are registered (see PasswordHasher).
        var verified = passwords.Verify(command.Password, credential?.PasswordHash ?? passwords.DummyVerifier);

        if (user is null || credential is null || !verified)
        {
            if (user is not null && credential is not null)
            {
                var lockedUntil = await credentials.RecordFailureAsync(
                    connection,
                    null,
                    user.Id,
                    _options.MaxFailedAttempts,
                    _options.LockoutDuration,
                    now,
                    cancellationToken);

                if (lockedUntil is { } until && until > now)
                {
                    logger.LogWarning(
                        "Locked {UserId} until {LockedUntil} after {MaxFailedAttempts} failed sign-ins (AL-37)",
                        user.Id,
                        until,
                        _options.MaxFailedAttempts);

                    throw Locked(until - now);
                }
            }

            throw new MageRideException(MageRideErrors.Unauthorized, "The email or password is incorrect.");
        }

        var signedIn = await CompleteAsync(connection, user, command.Surface, command.DeviceKey, command.ClientAddress, now, cancellationToken);

        await credentials.RecordSuccessAsync(connection, null, user.Id, now, cancellationToken);

        return signedIn;
    }

    public async Task<PortalSignIn> WithProviderAsync(
        ProviderSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var asserted = await oidc.VerifyAsync(command.Provider, command.IdToken, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // The provider's subject first: it is the binding, and it survives the user changing
        // their address at the provider.
        var identity = await credentials.FindFederatedAsync(
            connection, null, asserted.Provider, asserted.Subject, cancellationToken);

        IamUser? user = identity is null
            ? null
            : await users.FindByIdAsync(connection, null, identity.UserId, cancellationToken);

        if (user is null && asserted is { Email: { } assertedEmail, EmailVerified: true })
        {
            // First sign-in for an account provisioned with this address. Only ever on a
            // *verified* address — an unverified one is a string the provider let somebody type.
            user = await users.FindByEmailAsync(connection, null, assertedEmail, cancellationToken);
        }

        if (user is null)
        {
            logger.LogWarning(
                "Refused a {Provider} sign-in: no provisioned account for subject {Subject}",
                asserted.Provider,
                asserted.Subject);

            // Not 401. The token was valid; the identity simply has no MageRide account, and one
            // is not created here — internal roles are provisioned by a Super Admin (AL-06) and
            // fleet users by their owner (AL-03).
            throw new MageRideException(
                MageRideErrors.Forbidden, "This identity has no MageRide portal account.");
        }

        var signedIn = await CompleteAsync(connection, user, command.Surface, command.DeviceKey, command.ClientAddress, now, cancellationToken);

        await credentials.LinkFederatedAsync(
            connection, null, user.Id, asserted.Provider, asserted.Subject, asserted.Email, now, cancellationToken);

        return signedIn;
    }

    /// <summary>
    /// The half every portal sign-in shares once the caller has been identified: block check,
    /// surface resolution, the AL-37 allow-list, the device row and the session.
    /// </summary>
    private async Task<PortalSignIn> CompleteAsync(
        Npgsql.NpgsqlConnection connection,
        IamUser user,
        string? requestedSurface,
        string deviceKey,
        IPAddress? clientAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (user.IsBlocked)
        {
            throw new MageRideException(MageRideErrors.UserBlocked, "This account is blocked.");
        }

        var principal = await users.PrincipalAsync(connection, null, user.Id, cancellationToken);
        var surface = ResolveSurface(principal, requestedSurface);

        internalAccess.Enforce(clientAddress, principal.Roles);

        Guid deviceRowId;
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            deviceRowId = await devices.EnsureAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                user.Id,
                deviceKey,
                WebPlatform,
                // A browser has no FCM registration; web push is not a MageRide surface (E-01
                // covers FCM and APNs only).
                fcmToken: null,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        var session = await sessionService.IssueAsync(principal, deviceRowId, deviceKey, surface, cancellationToken);

        logger.LogInformation("Issued a {Surface} portal session for {UserId}", surface, user.Id);

        return new PortalSignIn(session, user, principal);
    }

    /// <summary>
    /// Which portal this account is signing in to — the <c>app</c> claim and
    /// <c>iam.sessions.app</c> (0107).
    /// </summary>
    /// <remarks>
    /// The routes that serve one portal say so and the account has to qualify; the two that serve
    /// both (<c>/v1/auth/password</c>, <c>/v1/auth/google</c>) let the roles decide, internal
    /// first. An account with neither an internal role nor a fleet standing is a passenger or a
    /// driver, and those two surfaces are Phone-OTP only (AL-07) — so it is refused here rather
    /// than handed a session it could not have obtained from its own app.
    /// </remarks>
    private static string ResolveSurface(SessionPrincipal principal, string? requested)
    {
        var isInternal = principal.Roles.Any(MageRideRoles.Internal.Contains);
        var isFleet = principal.Fleet is not null || principal.Roles.Contains(MageRideRoles.FleetOwner);

        return requested switch
        {
            MageRideApps.Admin when isInternal => MageRideApps.Admin,
            MageRideApps.Admin => throw Forbidden("The Admin Portal is for internal roles only (AL-02)."),
            MageRideApps.Fleet when isFleet => MageRideApps.Fleet,
            MageRideApps.Fleet => throw Forbidden("The Fleet Portal is for fleet accounts only (AL-03)."),
            null when isInternal => MageRideApps.Admin,
            null when isFleet => MageRideApps.Fleet,
            null => throw Forbidden("The passenger and driver apps sign in by phone OTP only (AL-07)."),
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown portal surface."),
        };
    }

    private static MageRideException Forbidden(string detail) => new(MageRideErrors.Forbidden, detail);

    /// <summary>
    /// The contract maps the AL-37 lock-out onto <c>423 otp-locked</c> on both password routes —
    /// the same code the OTP entry budget uses, because it is the same fact: too many wrong
    /// guesses, come back later.
    /// </summary>
    private static MageRideException Locked(TimeSpan remaining) =>
        new MageRideException(
                MageRideErrors.OtpLocked,
                "Too many failed sign-in attempts; this account is temporarily locked (AL-37).")
            .WithExtension("retryAfterSeconds", (int)Math.Ceiling(Math.Max(1, remaining.TotalSeconds)));

    private static string RequireEmail(string? email)
    {
        // Shape only. Deliverability is not knowable here and a stricter grammar than "something,
        // an @, something with a dot" rejects addresses that exist.
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) || email.Length > 254)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["email"] = ["email is required and must be an email address."],
            });
        }

        return email.Trim();
    }
}
