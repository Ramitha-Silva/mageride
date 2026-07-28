using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Shared.Mqtt;

/// <summary>
/// A minted MQTT session credential: the username EMQX authorises against and the JWT it takes as
/// the MQTT password.
/// </summary>
/// <param name="Username">MQTT username. The broker refuses the CONNECT unless the token's
/// <c>vehicleId</c> claim equals this, and <c>acl.conf</c> writes every device rule in terms of
/// <c>${username}</c> — so this string, verified by the broker, is the whole identity.</param>
/// <param name="Jwt">The MQTT password.</param>
/// <param name="ExpiresAt">When EMQX will disconnect the session
/// (<c>disconnect_after_expire = true</c>).</param>
public sealed record MqttSessionToken(string Username, string Jwt, DateTimeOffset ExpiresAt)
{
    /// <summary>Seconds of life left, as <c>iam.yaml</c>'s <c>expiresIn</c> reports it.</summary>
    public int ExpiresInSeconds => (int)Math.Max(0, (ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
}

/// <summary>
/// Mints the MQTT session JWT EMQX validates at CONNECT (D6' §3.2, E-02, D-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>The MQTT session JWT is not the API access token (E-02).</b> The API token lives 30 minutes;
/// this one lives <c>max(active-ride + 2 h, 4 h)</c> and is bound to
/// <c>(vehicleId, deviceId, rideId?)</c>, so an API refresh that fails in low coverage does not
/// silently stop a ride's position stream. The two are separate credentials with separate
/// lifetimes and they are never interchangeable — <c>/hubs/live</c> takes the API token and only
/// the API token.
/// </para>
/// <para>
/// <b>The username is the principal.</b> A device connects as its <c>vehicleId</c>; a platform
/// component connects as <c>svc-&lt;name&gt;</c>, which is the prefix <c>acl.conf</c> grants the
/// wildcard and <c>$share/#</c> subscriptions E-08 needs. <c>emqx.conf</c>'s
/// <c>verify_claims = { vehicleId = "${username}" }</c> is what makes the claim a verified fact
/// rather than a self-asserted string, so both forms put the same value in both places.
/// </para>
/// <para>
/// <b>Dev-form HMAC, and it stops at the replica.</b> See <see cref="MqttOptions.SessionTokenSecret"/>:
/// production mints RS256 in provisioning-svc (C030) and EMQX validates against its JWKS. Only the
/// signature changes — the claim set below is already the one D6' §3.2 specifies, so C030 replaces
/// the key material and nothing else.
/// </para>
/// </remarks>
public sealed class MqttSessionTokenIssuer
{
    /// <summary>The username prefix <c>acl.conf</c> grants platform-component privileges to.</summary>
    public const string ServiceUsernamePrefix = "svc-";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly MqttOptions _options;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _credentials;

    public MqttSessionTokenIssuer(IOptions<MqttOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.SessionTokenSecret))
        {
            throw new InvalidOperationException(
                "Mqtt:SessionTokenSecret is not configured. It must equal EMQX's " +
                "EMQX_AUTHENTICATION__1__SECRET or the broker refuses every CONNECT.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SessionTokenSecret));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>
    /// Mints a device's session token, bound to <c>(vehicleId, deviceId, rideId?)</c>.
    /// </summary>
    /// <param name="vehicleId">The vehicle whose <c>veh/{vehicleId}/*</c> topics the token opens.</param>
    /// <param name="deviceId">The bound handset or tracker (AL-08).</param>
    /// <param name="rideId">The active ride, when there is one.</param>
    /// <param name="rideEndsAt">When that ride is expected to end. The token outlives it by
    /// <see cref="MqttOptions.SessionTokenRideGrace"/>; without it the four-hour floor applies.</param>
    public MqttSessionToken IssueForVehicle(
        Guid vehicleId, string deviceId, Guid? rideId = null, DateTimeOffset? rideEndsAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (vehicleId == Guid.Empty)
        {
            throw new ArgumentException("A session token must be bound to a vehicle.", nameof(vehicleId));
        }

        var now = _clock.GetUtcNow();
        var floor = now + _options.SessionTokenMinimumTtl;
        var withRide = rideEndsAt is { } ends ? ends + _options.SessionTokenRideGrace : floor;
        var expiresAt = withRide > floor ? withRide : floor;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["vehicleId"] = vehicleId.ToString(),
            ["deviceId"] = deviceId,
        };

        if (rideId is { } ride)
        {
            claims["rideId"] = ride.ToString();
        }

        return Issue(vehicleId.ToString(), claims, now, expiresAt);
    }

    /// <summary>
    /// Mints a platform component's credential — mqtt-bridge, the TCP adapters.
    /// </summary>
    /// <param name="serviceName">Bare name, e.g. <c>mqtt-bridge</c>. The <c>svc-</c> prefix is added
    /// here so a caller cannot accidentally mint a service token under a vehicle-shaped username,
    /// which <c>acl.conf</c> would then grant the whole tree to.</param>
    public MqttSessionToken IssueForService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var bare = serviceName.StartsWith(ServiceUsernamePrefix, StringComparison.Ordinal)
            ? serviceName[ServiceUsernamePrefix.Length..]
            : serviceName;

        if (bare.Length == 0 || Guid.TryParse(bare, out _))
        {
            throw new ArgumentException(
                "A service username must be a name, not a vehicle id.", nameof(serviceName));
        }

        var username = ServiceUsernamePrefix + bare;
        var now = _clock.GetUtcNow();

        // A component reconnects on its own supervision loop and re-mints as it goes, so it takes
        // the same four-hour floor rather than a longer life of its own.
        return Issue(
            username,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["vehicleId"] = username },
            now,
            now + _options.SessionTokenMinimumTtl);
    }

    private MqttSessionToken Issue(
        string username, Dictionary<string, object> claims, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        claims[JwtRegisteredClaimNames.Sub] = username;
        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString();

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _credentials,
        };

        return new MqttSessionToken(username, Handler.CreateToken(descriptor), expiresAt);
    }
}
