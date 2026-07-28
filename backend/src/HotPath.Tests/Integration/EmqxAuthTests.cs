using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// DoD: "a client publishing to another vehicle's topic is rejected by the EMQX ACL."
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is against the <b>deployed</b> broker policy: <c>EmqxFixture</c> bind-mounts
/// <c>infra/deploy/emqx/emqx.conf</c> and <c>infra/deploy/emqx/acl.conf</c>, the same two files
/// <c>infra/docker-compose.dev.slim.yml</c> mounts. A test against a hand-written copy would prove
/// the copy right and say nothing about the stack.
/// </para>
/// <para>
/// The two halves are separate mechanisms and both matter. <c>emqx.conf</c>'s
/// <c>verify_claims = { vehicleId = "${username}" }</c> stops a device <i>authenticating</i> as
/// another vehicle; <c>acl.conf</c>'s <c>veh/${username}/*</c> rules stop an authenticated device
/// <i>publishing</i> under another vehicle's topic. Passing one and failing the other would leave
/// impersonation possible, so each is asserted on its own.
/// </para>
/// </remarks>
[Collection<HotPathCollection>]
public sealed class EmqxAuthTests(EmqxFixture emqx)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_device_connects_with_a_token_minted_for_its_own_vehicle()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        Assert.True(device.IsConnected);
    }

    [Fact]
    public async Task A_device_presenting_another_vehicles_token_is_refused_at_CONNECT()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        // Username = my vehicle, token minted for theirs. emqx.conf's verify_claims compares the
        // two and refuses; without it, a stolen token would authorise whatever username was typed
        // beside it.
        var refused = await Assert.ThrowsAsync<MqttConnectRefusedException>(
            () => DeviceClient.ConnectAsync(emqx, mine, DeviceClient.CredentialFor(theirs)));

        Assert.Contains("refused", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_secret_is_refused()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();

        var forged = new MqttSessionTokenIssuer(
                Options.Create(new MqttOptions { SessionTokenSecret = "a-different-secret-of-sufficient-length-x" }),
                TimeProvider.System)
            .IssueForVehicle(vehicleId, "test-device");

        await Assert.ThrowsAsync<MqttConnectRefusedException>(
            () => DeviceClient.ConnectAsync(emqx, vehicleId, forged));
    }

    [Fact]
    public async Task A_device_may_publish_to_its_own_live_topic()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        var result = await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort));

        Assert.True(result.IsSuccess);
        Assert.True(device.IsConnected);
    }

    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task A_device_publishing_to_another_vehicles_topic_is_disconnected_by_the_ACL()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await using var device = await DeviceClient.ConnectAsync(emqx, mine);

        // acl.conf allows publish only to veh/${username}/*, and emqx.conf sets
        // `deny_action = disconnect` rather than `ignore` — an MQTT 3.1.1 device gets no error code
        // for a refused publish, and silently dropping it would leave a misprovisioned tracker
        // publishing into a void for its whole 90-day credential.
        try
        {
            await device.PublishAsync(
                MqttTopics.PositionLive(theirs), MageRide.Shared.Telemetry.PositionSampleCodec.Encode(
                    Samples.At(theirs, Samples.ColomboFort)));
        }
        catch (Exception)
        {
            // The broker may drop the socket before the PUBACK, which is the same rejection.
        }

        var disconnected = await Task.WhenAny(device.Disconnected, Task.Delay(Timeout));

        Assert.True(
            disconnected == (Task)device.Disconnected,
            "EMQX should have disconnected a device that published under another vehicle's topic.");
    }

    [Fact]
    public async Task A_device_may_not_publish_outside_the_vehicle_tree_at_all()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        // `no_match = deny` plus acl.conf's final `{deny, all}`. Without both, a topic nobody wrote
        // a rule for would be allowed — EMQX's shipped default — and every rule in the file would
        // be advisory.
        try
        {
            await device.PublishAsync("telemetry/anything", "x"u8.ToArray());
        }
        catch (Exception)
        {
            // As above.
        }

        var disconnected = await Task.WhenAny(device.Disconnected, Task.Delay(Timeout));

        Assert.True(disconnected == (Task)device.Disconnected, "An unlisted topic must be denied, not allowed.");
    }

    [Fact]
    public async Task A_device_may_not_hold_the_bridges_shared_subscription()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        // `$share/#` is granted to the `svc-` prefix alone (acl.conf). A device that could join
        // posGroup would not merely read every vehicle's position — it would *take* messages out of
        // the shared subscription, and the bridge would silently stop seeing them.
        try
        {
            await device.SubscribeAsync(MqttTopics.SharedPositionLive());
        }
        catch (Exception)
        {
            // A refused subscribe with deny_action = disconnect drops the socket.
        }

        var disconnected = await Task.WhenAny(device.Disconnected, Task.Delay(Timeout));

        Assert.True(
            disconnected == (Task)device.Disconnected,
            "Only svc-* principals may hold the E-08 shared subscription.");
    }

    [Fact]
    public async Task A_device_may_subscribe_to_its_own_command_topic()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

        // The downlink R-07's cadence hint arrives on. Denying it would silently pin every device
        // to its default publish rate.
        var result = await device.SubscribeAsync(MqttTopics.Command(vehicleId));

        Assert.All(result.Items, item => Assert.Equal(
            MqttClientSubscribeResultCode.GrantedQoS1, item.ResultCode));
    }
}
