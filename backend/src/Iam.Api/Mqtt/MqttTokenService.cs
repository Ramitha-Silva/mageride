using MageRide.Iam.Configuration;
using MageRide.Iam.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Mqtt;

/// <param name="CallerId">The authenticated <c>sub</c>.</param>
/// <param name="CallerDeviceKey">The <c>device_id</c> claim on the caller's access token (AL-08).</param>
public sealed record MqttTokenCommand(
    Guid CallerId, string? CallerDeviceKey, string? VehicleId, string? DeviceId, string? RideId);

/// <summary><c>POST /v1/auth/mqtt-token</c> — 200.</summary>
public sealed record IssuedMqttToken(string MqttJwt, int ExpiresIn);

/// <summary>Mints the MQTT session JWT E-02 decouples from the API access token.</summary>
public interface IMqttTokenService
{
    Task<IssuedMqttToken> IssueAsync(MqttTokenCommand command, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMqttTokenService"/>
/// <remarks>
/// <para>
/// <b>Why this is a separate credential at all (E-02).</b> The API access token lives 30 minutes.
/// A driver in a coverage hole on the Kandy road cannot refresh it, and if position publishing
/// used the same token the ride would go dark exactly where the passenger's family most wants to
/// see it. So the MQTT credential is issued once, lives <c>max(active ride + 2 h, 4 h)</c>, and
/// survives every API refresh failure inside that window.
/// </para>
/// <para>
/// <b>The ride's expected end is not knowable from the schema.</b> D4' §5 gives
/// <c>rides.rides</c> no ETA, no estimated duration and no expected-end column — the only
/// temporal anchor is <c>created_at</c>. So <c>Mqtt:MaxRideDuration</c> stands in for "how long a
/// Mode C ride is assumed to run", the token covers that plus
/// <c>Mqtt:SessionTokenRideGrace</c>, and the four-hour floor applies when that comes out shorter.
/// Raised as a spec gap in the C026 handoff; the day an ETA lands, this reads it instead and
/// nothing else changes.
/// </para>
/// <para>
/// <b>The <c>rideId</c> is honoured, not inferred.</b> A caller that sends one gets the extended
/// TTL and the <c>rideId</c> claim; a caller that omits one gets the floor, which is what C014's
/// <c>MqttSessionTokenManager</c> documents and re-issues against. Quietly binding a token to a
/// ride the client did not name would make the client's own renewal logic wrong.
/// </para>
/// </remarks>
public sealed class MqttTokenService(
    INpgsqlConnectionFactory connectionFactory,
    IPublisherRepository publishers,
    MqttSessionTokenIssuer issuer,
    IOptions<IamMqttOptions> options,
    TimeProvider timeProvider,
    ILogger<MqttTokenService> logger) : IMqttTokenService
{
    private readonly IamMqttOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IssuedMqttToken> IssueAsync(MqttTokenCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var vehicleId = RequireId(command.VehicleId, "vehicleId");
        var deviceId = RequireDeviceId(command.DeviceId);
        var rideId = command.RideId is null ? (Guid?)null : RequireId(command.RideId, "rideId");

        // The MQTT credential inherits the API session's device binding. Without this a stolen
        // access token could mint a publishing credential for a *different* handset, which is
        // the one thing AL-08's single-active-device rule is there to prevent.
        if (!string.Equals(command.CallerDeviceKey, deviceId, StringComparison.Ordinal))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["deviceId"] = ["deviceId must be the device this session was issued to (AL-08)."],
            });
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var vehicle = await publishers.FindVehicleAsync(connection, vehicleId, cancellationToken)
            ?? throw new MageRideException(MageRideErrors.VehicleNotFound, "No such vehicle.");

        if (vehicle.OwnerId != command.CallerId)
        {
            throw new MageRideException(
                MageRideErrors.NotOwner, "This vehicle belongs to another driver.");
        }

        DateTimeOffset? rideEndsAt = null;

        if (rideId is { } ride)
        {
            var active = await publishers.FindActiveRideForDriverAsync(connection, command.CallerId, cancellationToken);

            // One 403 for "no such ride", "not your ride" and "already finished". Distinguishing
            // them would tell a caller which ride ids exist, and none of the three is a state the
            // driver app can recover from differently.
            if (active is null || active.RideId != ride ||
                (active.VehicleId is { } assigned && assigned != vehicleId))
            {
                throw new MageRideException(
                    MageRideErrors.NotOwner, "That ride is not this driver's active ride on this vehicle.");
            }

            rideEndsAt = active.StartedAt + _options.MaxRideDuration;
        }

        var token = issuer.IssueForVehicle(vehicleId, deviceId, rideId, rideEndsAt);

        // Ceiling, not truncation: the issuer stamps `now + 4 h` a few milliseconds before this
        // line reads the clock, and truncating that would report 14399 against a contract that
        // says `expiresIn` is "never less than 14400".
        var expiresIn = (int)Math.Ceiling(Math.Max(0, (token.ExpiresAt - timeProvider.GetUtcNow()).TotalSeconds));

        logger.LogInformation(
            "Minted an MQTT session token for vehicle {VehicleId} on device {DeviceId} (ride {RideId}), valid {ExpiresIn}s",
            vehicleId,
            deviceId,
            rideId,
            expiresIn);

        return new IssuedMqttToken(token.Jwt, expiresIn);
    }

    private static Guid RequireId(string? value, string field)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be an identifier."],
            });
        }

        return parsed;
    }

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
}
