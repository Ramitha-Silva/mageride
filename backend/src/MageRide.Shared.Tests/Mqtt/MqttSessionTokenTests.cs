using MageRide.Shared.Mqtt;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MageRide.Shared.Tests.Mqtt;

/// <summary>
/// The MQTT session JWT EMQX validates at CONNECT (D6' §3.2, E-02, D-21).
/// </summary>
/// <remarks>
/// The claim set is a contract with <c>infra/deploy/emqx/emqx.conf</c>, which refuses the CONNECT
/// unless the token's <c>vehicleId</c> equals the MQTT username, and with <c>acl.conf</c>, which
/// writes every device rule in terms of <c>${username}</c>. A token that carries the right claims
/// under the wrong username authorises nothing, so both are asserted together.
/// </remarks>
public sealed class MqttSessionTokenTests
{
    private const string Secret = "mageride-dev-mqtt-jwt-secret-change-me";

    private static readonly Guid Vehicle = Guid.Parse("2f9d6b2c-0f4f-4a1e-9c3a-8f1d5b7e6a40");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    private static MqttSessionTokenIssuer Issuer(Action<MqttOptions>? configure = null)
    {
        var options = new MqttOptions { SessionTokenSecret = Secret };
        configure?.Invoke(options);

        return new MqttSessionTokenIssuer(Options.Create(options), new FakeTimeProvider(Now));
    }

    private static JsonWebToken Read(MqttSessionToken token) => new JsonWebTokenHandler().ReadJsonWebToken(token.Jwt);

    [Fact]
    public void A_device_token_binds_the_vehicle_claim_to_the_username_EMQX_authorises_on()
    {
        var token = Issuer().IssueForVehicle(Vehicle, "device-1");
        var jwt = Read(token);

        // emqx.conf: verify_claims = { vehicleId = "${username}" }. If these two ever disagree the
        // broker refuses the CONNECT, which is the whole point — the claim is verified, not asserted.
        Assert.Equal(Vehicle.ToString(), token.Username);
        Assert.Equal(Vehicle.ToString(), jwt.GetClaim("vehicleId").Value);
        Assert.Equal("device-1", jwt.GetClaim("deviceId").Value);
    }

    [Fact]
    public void A_ride_bound_token_carries_the_ride_it_was_minted_for()
    {
        var ride = Guid.NewGuid();
        var jwt = Read(Issuer().IssueForVehicle(Vehicle, "device-1", ride));

        Assert.Equal(ride.ToString(), jwt.GetClaim("rideId").Value);
    }

    [Fact]
    public void An_idle_token_lives_the_four_hour_floor()
    {
        // E-02's reason for existing: the API access token lives 30 minutes, and a refresh that
        // fails in poor coverage must not stop a vehicle publishing its position.
        var token = Issuer().IssueForVehicle(Vehicle, "device-1");

        Assert.Equal(Now.AddHours(4), token.ExpiresAt);
    }

    [Fact]
    public void A_long_ride_extends_the_token_past_the_floor_by_two_hours()
    {
        // TTL = max(active-ride + 2 h, 4 h). A six-hour intercity run would otherwise go dark at
        // hour four, mid-trip.
        var token = Issuer().IssueForVehicle(Vehicle, "device-1", Guid.NewGuid(), Now.AddHours(6));

        Assert.Equal(Now.AddHours(8), token.ExpiresAt);
    }

    [Fact]
    public void A_short_ride_does_not_shorten_the_token_below_the_floor()
    {
        var token = Issuer().IssueForVehicle(Vehicle, "device-1", Guid.NewGuid(), Now.AddMinutes(20));

        Assert.Equal(Now.AddHours(4), token.ExpiresAt);
    }

    /// <summary>
    /// <c>iam.yaml</c> documents <c>expiresIn</c> as "never less than 14400".
    /// </summary>
    /// <remarks>
    /// Asserted twice, because <see cref="MqttSessionToken.ExpiresInSeconds"/> measures against
    /// the <em>system</em> clock rather than the issuer's: the minted lifetime is checked on the
    /// fake clock, and the reported one on an issuer whose clock is the real one. Reading the
    /// reported value off a token minted at a fixed fake instant would drift as the day went on —
    /// it did, which is how this was found.
    /// </remarks>
    [Fact]
    public void The_reported_lifetime_never_drops_below_iam_yaml_s_documented_14400_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(14_400), Issuer().IssueForVehicle(Vehicle, "device-1").ExpiresAt - Now);

        var live = new MqttSessionTokenIssuer(
            Options.Create(new MqttOptions { SessionTokenSecret = Secret }), TimeProvider.System);

        Assert.True(live.IssueForVehicle(Vehicle, "device-1").ExpiresInSeconds >= 14_400 - 60);
    }

    [Fact]
    public void A_service_credential_takes_the_svc_prefix_acl_conf_grants_the_share_subscription_to()
    {
        // acl.conf: {allow, {username, {re, "^svc-"}}, all, ["veh/#", ..., "$share/#"]}. Without the
        // prefix mqtt-bridge cannot hold `$share/posGroup/veh/+/pos/live` at all.
        var token = Issuer().IssueForService("mqtt-bridge");

        Assert.Equal("svc-mqtt-bridge", token.Username);
        Assert.Equal("svc-mqtt-bridge", Read(token).GetClaim("vehicleId").Value);
    }

    [Fact]
    public void The_prefix_is_not_doubled_when_the_caller_already_supplied_it() =>
        Assert.Equal("svc-tcp-adapter", Issuer().IssueForService("svc-tcp-adapter").Username);

    [Fact]
    public void A_service_username_that_is_a_vehicle_id_is_refused() =>
        // acl.conf grants `svc-*` the whole tree. Minting one under a uuid would hand a device
        // credential platform-wide publish rights.
        Assert.Throws<ArgumentException>(() => Issuer().IssueForService(Vehicle.ToString()));

    [Fact]
    public void A_token_for_no_vehicle_is_refused() =>
        Assert.Throws<ArgumentException>(() => Issuer().IssueForVehicle(Guid.Empty, "device-1"));

    [Fact]
    public void An_unconfigured_secret_fails_loudly_at_construction_rather_than_at_CONNECT() =>
        Assert.Throws<InvalidOperationException>(
            () => new MqttSessionTokenIssuer(Options.Create(new MqttOptions()), TimeProvider.System));

    [Fact]
    public void The_token_is_HS256_which_is_what_emqx_conf_s_hmac_based_algorithm_accepts()
    {
        var jwt = Read(Issuer().IssueForVehicle(Vehicle, "device-1"));

        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal("mageride-provisioning", jwt.Issuer);
    }
}
