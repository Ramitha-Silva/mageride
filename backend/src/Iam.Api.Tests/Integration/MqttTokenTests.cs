using System.Net;
using MageRide.Iam.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MageRide.Iam.Tests.Integration;

/// <summary>
/// <c>POST /v1/auth/mqtt-token</c> — the MQTT session JWT E-02 decouples from the API access
/// token.
/// </summary>
/// <remarks>
/// E-02 exists because a 30-minute API token expires mid-trip in low coverage, and if position
/// publishing used that token the ride would go dark exactly where it matters. So the assertions
/// here are mostly about lifetime: never under four hours, longer than that while a ride is
/// running, and always far longer than the access token that requested it.
/// </remarks>
[Collection(IamCollection.Name)]
public sealed class MqttTokenTests(PostgresFixture postgres, RedisFixture redis)
{
    private const int FourHours = 14_400;
    private const string Device = "driver-handset-1";

    [Fact]
    public async Task A_driver_gets_a_token_for_their_own_vehicle_with_the_four_hour_floor()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (driver, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token",
            new { vehicleId, deviceId = Device },
            bearer: accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await IamHarness.ReadJsonAsync(response);
        var expiresIn = body.GetProperty("expiresIn").GetInt32();

        // The contract: "never less than 14400 (4 h)".
        Assert.True(expiresIn >= FourHours, $"expiresIn was {expiresIn}");

        var jwt = new JsonWebToken(body.GetProperty("mqttJwt").GetString()!);

        // EMQX's `verify_claims = { vehicleId = "${username}" }` is what makes the claim a
        // verified fact rather than a self-asserted string, so both have to be the vehicle.
        Assert.Equal(vehicleId.ToString(), jwt.GetClaim("vehicleId").Value);
        Assert.Equal(vehicleId.ToString(), jwt.GetClaim(JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(Device, jwt.GetClaim("deviceId").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == "rideId");

        Assert.NotEqual(Guid.Empty, driver);
    }

    /// <summary>
    /// The point of E-02, stated as a comparison: the MQTT credential outlives the API token that
    /// asked for it by hours, so a refresh that fails in a coverage hole cannot stop publishing.
    /// </summary>
    [Fact]
    public async Task The_mqtt_token_outlives_the_api_token_many_times_over()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (_, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var mqtt = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = Device }, bearer: accessToken));

        var apiLifetime = new JsonWebToken(accessToken).ValidTo - new JsonWebToken(accessToken).ValidFrom;

        Assert.Equal(TimeSpan.FromMinutes(30), apiLifetime);
        Assert.True(mqtt.GetProperty("expiresIn").GetInt32() >= (int)apiLifetime.TotalSeconds * 8);
    }

    /// <summary>
    /// A ride that has been running for three hours pushes the token past the floor:
    /// <c>max(active-ride + 2 h, 4 h)</c>.
    /// </summary>
    [Fact]
    public async Task An_active_ride_extends_the_token_past_the_floor()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (driver, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var passenger = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var rideId = await harness.Seed.ActiveRideAsync(
            passenger, driver, vehicleId, startedAt: DateTimeOffset.UtcNow.AddHours(3));

        var body = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = Device, rideId }, bearer: accessToken));

        // Ride started 3 h ago, assumed to run 4 h, plus the 2 h grace: ~3 h left of ride and
        // 2 h beyond it, so comfortably more than the floor.
        var expiresIn = body.GetProperty("expiresIn").GetInt32();
        Assert.True(expiresIn > FourHours, $"an active ride did not extend the TTL: {expiresIn}");

        var jwt = new JsonWebToken(body.GetProperty("mqttJwt").GetString()!);
        Assert.Equal(rideId.ToString(), jwt.GetClaim("rideId").Value);
    }

    /// <summary>
    /// C014's <c>MqttSessionTokenManager</c> documents that omitting the ride id yields the floor
    /// and re-issues when the binding changes. Quietly binding to a ride the client did not name
    /// would make its renewal logic wrong.
    /// </summary>
    [Fact]
    public async Task Omitting_the_ride_id_yields_the_floor_even_during_a_ride()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (driver, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var passenger = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        await harness.Seed.ActiveRideAsync(passenger, driver, vehicleId, startedAt: DateTimeOffset.UtcNow.AddHours(3));

        var body = await IamHarness.ReadJsonAsync(await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = Device }, bearer: accessToken));

        Assert.InRange(body.GetProperty("expiresIn").GetInt32(), FourHours - 60, FourHours);
    }

    [Fact]
    public async Task Another_drivers_vehicle_is_not_ours_to_publish_for()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (_, _, accessToken) = await DriverWithVehicleAsync(harness);

        var otherDriver = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var otherVehicle = await harness.Seed.ApprovedVehicleAsync(otherDriver);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId = otherVehicle, deviceId = Device }, bearer: accessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
    }

    [Fact]
    public async Task A_vehicle_that_does_not_exist_is_404()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (_, _, accessToken) = await DriverWithVehicleAsync(harness);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId = Guid.NewGuid(), deviceId = Device }, bearer: accessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "vehicle-not-found");
    }

    /// <summary>
    /// The MQTT credential inherits the API session's device binding. Without this a stolen access
    /// token could mint a publishing credential for a different handset — the one thing AL-08's
    /// single-active-device rule exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_token_cannot_be_minted_for_a_device_the_session_is_not_bound_to()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (_, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = "someone-elses-handset" }, bearer: accessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// One 403 for "no such ride", "not your ride" and "already finished": distinguishing them
    /// would tell a caller which ride ids exist, and the driver app cannot act on the difference.
    /// </summary>
    [Fact]
    public async Task A_ride_that_is_not_this_drivers_active_ride_is_refused()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (driver, vehicleId, accessToken) = await DriverWithVehicleAsync(harness);

        var unknown = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = Device, rideId = Guid.NewGuid() }, bearer: accessToken);
        await ProblemDocument.AssertAsync(unknown, HttpStatusCode.Forbidden, "not-owner");

        // A ride that has reached a terminal state is no longer active, so it stops extending the
        // credential that was publishing for it.
        var passenger = await harness.Seed.PassengerAsync(IamHarness.NextPhone());
        var finished = await harness.Seed.ActiveRideAsync(passenger, driver, vehicleId, state: "Paid");

        var stale = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId, deviceId = Device, rideId = finished }, bearer: accessToken);
        await ProblemDocument.AssertAsync(stale, HttpStatusCode.Forbidden, "not-owner");
    }

    [Fact]
    public async Task The_endpoint_needs_a_session()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId = Guid.NewGuid(), deviceId = Device });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_vehicle_id_is_a_validation_failure()
    {
        await using var harness = await IamHarness.StartAsync(postgres, redis);
        var (_, _, accessToken) = await DriverWithVehicleAsync(harness);

        var response = await harness.PostAsync(
            "/v1/auth/mqtt-token", new { vehicleId = "not-a-ulid", deviceId = Device }, bearer: accessToken);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// Signs a driver in through the real OTP flow — the access token has to carry the
    /// <c>device_id</c> claim the endpoint checks — and gives them an APPROVED Mode C vehicle.
    /// </summary>
    private static async Task<(Guid Driver, Guid Vehicle, string AccessToken)> DriverWithVehicleAsync(IamHarness harness)
    {
        var signedIn = await harness.SignInAsync(IamHarness.NextPhone(), Device, MageRideApps.Driver);
        var driverId = Guid.Parse(signedIn.UserId);

        return (driverId, await harness.Seed.ApprovedVehicleAsync(driverId), signedIn.AccessToken);
    }
}
