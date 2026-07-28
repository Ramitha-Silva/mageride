using MageRide.Iam.Configuration;
using MageRide.Iam.Domain;
using MageRide.Iam.Persistence;
using MageRide.Iam.Sessions;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Iam.Otp;

/// <param name="Phone">Raw, as the client sent it. Normalised here.</param>
/// <param name="DeviceId">Stable per-install identifier (AL-08).</param>
/// <param name="App"><c>passenger</c> | <c>driver</c>.</param>
/// <param name="Platform"><c>android</c> | <c>ios</c>, from the gateway's <c>X-Platform</c> (D-31).</param>
/// <param name="FcmToken">Optional push token. Held on the attempt until verify identifies the
/// user, because <c>iam.devices</c> has no row to write it to before that (0107).</param>
public sealed record RequestOtpCommand(
    string? Phone, string? DeviceId, string App, string Platform, string? FcmToken = null);

/// <param name="AttemptsRemaining">Sends left in this hour (D-32).</param>
/// <param name="CooldownSeconds">Seconds before the next send is allowed (D-32).</param>
public sealed record OtpDispatched(Guid AuthId, int AttemptsRemaining, int CooldownSeconds);

/// <summary>The outcome of a successful verify.</summary>
public sealed record VerifiedSignIn(IssuedSession Session, IamUser User, SessionPrincipal Principal, bool IsNewUser);

/// <summary>
/// Phone-OTP sign-in for the passenger and driver apps — the only sign-in those surfaces have
/// (AL-07). Portal password/Google/Apple sign-in is C026; there is no MFA (AL-37).
/// </summary>
public interface IOtpService
{
    Task<OtpDispatched> RequestAsync(RequestOtpCommand command, CancellationToken cancellationToken);

    Task<OtpDispatched> ResendAsync(Guid authId, CancellationToken cancellationToken);

    Task<VerifiedSignIn> VerifyAsync(Guid authId, string? code, string? deviceId, string platform, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOtpService"/>
public sealed class OtpService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IOtpAttemptRepository attempts,
    IUserRepository users,
    IDeviceRepository devices,
    ISessionService sessionService,
    ITokenBucketRateLimiter rateLimiter,
    IOtpSender sender,
    OtpCodes codes,
    IOptions<OtpOptions> options,
    TimeProvider timeProvider,
    ILogger<OtpService> logger) : IOtpService
{
    private readonly OtpOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// D-32's two rules in one bucket: five sends an hour <em>and</em> not two inside 60 s. Named
    /// <c>otp-send</c> to match <see cref="RateLimitPolicies.OtpSend"/>, so the Redis key space and
    /// the rejection metric stay the kernel's; the numbers come from D7' §4.2 so a deployment can
    /// tune them without a release.
    /// </summary>
    private readonly TokenBucketPolicy _sendPolicy = new(
        RateLimitPolicies.OtpSend.Name,
        capacity: options.Value.MaxPerHour,
        refillTokens: options.Value.MaxPerHour,
        refillPeriod: TimeSpan.FromHours(1),
        minInterval: TimeSpan.FromSeconds(options.Value.ResendCooldownSec));

    public async Task<OtpDispatched> RequestAsync(RequestOtpCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!PhoneNumbers.TryNormalise(command.Phone, out var phone))
        {
            throw new MageRideException(MageRideErrors.InvalidPhone, "Expected a Sri Lankan mobile number in E.164 form, +947XXXXXXXX.");
        }

        var deviceId = RequireDeviceId(command.DeviceId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var existing = await users.FindByPhoneAsync(connection, null, phone, cancellationToken);
        if (existing is { IsBlocked: true })
        {
            throw new MageRideException(MageRideErrors.UserBlocked, "This account is blocked.");
        }

        var allowance = await ConsumeSendAllowanceAsync(phone, cancellationToken);

        var authId = Guid.NewGuid();
        var code = OtpCodes.NewCode();
        var now = timeProvider.GetUtcNow();

        await attempts.InsertAsync(connection, null, new OtpAttempt(
            Id: Guid.NewGuid(),
            Phone: phone,
            AuthId: authId,
            OtpHash: codes.Hash(authId, code),
            Attempts: 0,
            ExpiresAt: now + _options.Ttl,
            Verified: false,
            DeviceId: deviceId,
            App: command.App,
            FcmToken: NormaliseFcmToken(command.FcmToken),
            CreatedAt: now), cancellationToken);

        await DeliverAsync(phone, code, existing?.Language ?? "en", cancellationToken);

        return new OtpDispatched(authId, allowance, _options.ResendCooldownSec);
    }

    public async Task<OtpDispatched> ResendAsync(Guid authId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var attempt = await attempts.FindByAuthIdAsync(connection, null, authId, cancellationToken);
        if (attempt is null || attempt.Verified)
        {
            throw new MageRideException(MageRideErrors.AuthNotFound, "No sign-in is in flight for this authId.");
        }

        var allowance = await ConsumeSendAllowanceAsync(attempt.Phone, cancellationToken);

        var code = OtpCodes.NewCode();
        var expiresAt = timeProvider.GetUtcNow() + _options.Ttl;

        if (!await attempts.ReplaceCodeAsync(connection, null, authId, codes.Hash(authId, code), expiresAt, cancellationToken))
        {
            throw new MageRideException(MageRideErrors.AuthNotFound, "No sign-in is in flight for this authId.");
        }

        var user = await users.FindByPhoneAsync(connection, null, attempt.Phone, cancellationToken);
        await DeliverAsync(attempt.Phone, code, user?.Language ?? "en", cancellationToken);

        return new OtpDispatched(authId, allowance, _options.ResendCooldownSec);
    }

    public async Task<VerifiedSignIn> VerifyAsync(
        Guid authId, string? code, string? deviceId, string platform, CancellationToken cancellationToken)
    {
        var presentedDevice = RequireDeviceId(deviceId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var attempt = await attempts.FindByAuthIdAsync(connection, null, authId, cancellationToken);
        if (attempt is null || attempt.Verified)
        {
            // A spent attempt is gone as far as the caller is concerned. A genuine duplicate of
            // the *same* verify replays through the Idempotency-Key and never reaches here.
            throw new MageRideException(MageRideErrors.AuthNotFound, "No sign-in is in flight for this authId.");
        }

        // Before the expiry and attempt checks: presenting the wrong device is not a failed guess
        // and must not spend the attempt budget of the device that actually asked for the code.
        if (!string.Equals(attempt.DeviceId, presentedDevice, StringComparison.Ordinal))
        {
            throw new MageRideException(MageRideErrors.DeviceMismatch, "This OTP was issued to a different device.");
        }

        if (timeProvider.GetUtcNow() >= attempt.ExpiresAt)
        {
            throw new MageRideException(MageRideErrors.OtpExpired, "The OTP has expired; request a new one.");
        }

        if (attempt.Attempts >= _options.MaxVerifyAttempts)
        {
            throw new MageRideException(MageRideErrors.OtpLocked, "Too many incorrect entries for this authId.");
        }

        if (!codes.Matches(authId, code ?? string.Empty, attempt.OtpHash))
        {
            var used = await attempts.RecordFailureAsync(connection, null, authId, cancellationToken);

            throw used >= _options.MaxVerifyAttempts
                ? new MageRideException(MageRideErrors.OtpLocked, "Too many incorrect entries for this authId.")
                : new MageRideException(MageRideErrors.InvalidOtp, "The OTP is incorrect.");
        }

        var app = attempt.App ?? MageRideApps.Passenger;
        Guid deviceRowId;
        IamUser user;
        SessionPrincipal principal;
        bool isNewUser;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            // Conditional, so two verifies racing on one authId open one session, not two.
            if (!await attempts.MarkVerifiedAsync(unitOfWork.Connection, unitOfWork.Transaction, authId, cancellationToken))
            {
                throw new MageRideException(MageRideErrors.AuthNotFound, "No sign-in is in flight for this authId.");
            }

            var found = await users.FindByPhoneAsync(unitOfWork.Connection, unitOfWork.Transaction, attempt.Phone, cancellationToken);
            isNewUser = found is null;

            if (found is null)
            {
                // First sign-in creates the account with the role of the app it came from. An
                // existing account is never escalated here — holding the driver role is what
                // registry-svc's onboarding grants (C029), not what opening the driver app does.
                var role = app == MageRideApps.Driver ? MageRideRoles.Driver : MageRideRoles.Passenger;
                found = await users.CreateAsync(unitOfWork.Connection, unitOfWork.Transaction, attempt.Phone, role, cancellationToken);
                await users.GrantRoleAsync(unitOfWork.Connection, unitOfWork.Transaction, found.Id, role, cancellationToken);
            }
            else if (found.IsBlocked)
            {
                throw new MageRideException(MageRideErrors.UserBlocked, "This account is blocked.");
            }

            user = found;
            principal = await users.PrincipalAsync(unitOfWork.Connection, unitOfWork.Transaction, user.Id, cancellationToken);

            deviceRowId = await devices.EnsureAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                user.Id,
                presentedDevice,
                platform,
                // The contract's optional fcmToken, carried from otp/request on the attempt row
                // (0107). It cannot be written before this point: iam.devices.fcm_apns_token
                // lives on a row that does not exist until verify identifies the user.
                // notification-svc (C051) is what eventually sends to it.
                attempt.FcmToken,
                cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        var session = await sessionService.IssueAsync(principal, deviceRowId, presentedDevice, app, cancellationToken);

        logger.LogInformation(
            "Issued a {App} session for {UserId} (new user: {IsNewUser})", app, user.Id, isNewUser);

        return new VerifiedSignIn(session, user, principal, isNewUser);
    }

    /// <summary>
    /// Hands the code to the SMS transport and turns a delivery failure into the 503 the caller
    /// can act on.
    /// </summary>
    /// <remarks>
    /// Without this a gateway outage is a 500 — an internal error the client is told nothing
    /// about and its retry policy treats as a bug. It is not a bug: it is Notify.lk being down,
    /// and "try again shortly" is both true and actionable. <see cref="FallbackOtpSender"/> has
    /// already tried the secondary gateway by the time this catches anything (D6' §7.3).
    /// </remarks>
    private async Task DeliverAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(phone, code, language, cancellationToken);
        }
        catch (Exception ex) when (ex is OtpDeliveryException or HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "The SMS gateway ({Provider}) could not deliver an OTP", sender.Provider);
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "The SMS gateway is unavailable; try again shortly.");
        }
    }

    /// <summary>An empty or oversized push token is dropped rather than rejected.</summary>
    /// <remarks>
    /// The contract caps <c>fcmToken</c> at 512 characters and marks it optional. A bad one is
    /// not a reason to refuse a sign-in — the worst case is that a device gets no push until it
    /// registers again, and notification-svc (C051) owns that path anyway.
    /// </remarks>
    private static string? NormaliseFcmToken(string? fcmToken) =>
        string.IsNullOrWhiteSpace(fcmToken) || fcmToken.Length > 512 ? null : fcmToken;

    private static string RequireDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["deviceId"] = ["deviceId is required and must be at most 128 characters."],
            });
        }

        return deviceId;
    }

    /// <summary>
    /// Takes one send from the number's bucket, or refuses (D-32).
    /// </summary>
    /// <remarks>
    /// Fails closed when Redis is unreachable. The bucket is the only thing standing between an
    /// SMS bill and an attacker, so "we could not check" has to mean "no" — unlike the gateway's
    /// coarse edge limiter, which fails open on purpose.
    /// </remarks>
    private async Task<int> ConsumeSendAllowanceAsync(string phone, CancellationToken cancellationToken)
    {
        RateLimitDecision decision;
        try
        {
            decision = await rateLimiter.TryAcquireAsync(_sendPolicy, phone, cancellationToken: cancellationToken);
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "The OTP rate-limit bucket is unreachable; refusing the send (D-32 fails closed)");
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "OTP rate limiting is unavailable; try again shortly.");
        }

        if (!decision.Allowed)
        {
            var retryAfter = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds);
            throw new MageRideException(MageRideErrors.OtpRateLimited,
                    $"At most {_options.MaxPerHour} OTPs per hour and one every {_options.ResendCooldownSec} seconds (D-32).")
                .WithExtension("retryAfterSeconds", retryAfter);
        }

        return decision.Remaining;
    }
}
