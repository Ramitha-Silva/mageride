using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Http;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Integration;

/// <summary>
/// The downlink (§7.7.5) and the sub-second revocation (T-12) — the two things that travel from the
/// platform to a device.
/// </summary>
[Collection(AdapterCollection.Name)]
[Trait("Category", "Downlink")]
public sealed class DownlinkTests(EmqxFixture emqx, RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task A_command_on_the_vehicles_topic_arrives_as_a_protocol_native_frame()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/status");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());

        // §7.7.5: "the adapter subscribes to the same topic, translates the envelope into the protocol's
        // native command frame, and writes it back over the open socket."
        await observer.PublishCommandAsync(
            vehicleId, """{"cmd":"setPosRate","args":{"seconds":30},"expiresAt":"2099-01-01T00:00:00Z"}""");

        var frame = await ReadFrameAsync(device);

        // A GT06 0x80: start bytes, length, protocol number, then the ASCII command inside its envelope.
        Assert.Equal(0x78, frame[0]);
        Assert.Equal(0x78, frame[1]);
        Assert.Equal(Gt06Codec.ProtocolCommand, frame[3]);
        Assert.Contains("TIMER,30#", Encoding.ASCII.GetString(frame), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_command_is_not_delivered()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/status");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());

        // §7.7.5: "commands have an expiresAt; expired commands are not delivered on reconnect." A
        // pingNow that has been in a broker queue since the device lost coverage is not a request
        // anybody still wants answered.
        await observer.PublishCommandAsync(
            vehicleId, """{"cmd":"pingNow","expiresAt":"2020-01-01T00:00:00Z"}""");

        Assert.Empty(await device.ReceiveAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_command_outside_the_five_the_platform_defines_is_refused()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/status");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());

        // GT06's command payload is an opaque ASCII string, so a pass-through would turn anybody able to
        // publish on this topic into a device-configuration channel. The set is closed.
        await observer.PublishCommandAsync(
            vehicleId, """{"cmd":"FACTORY,RESET#","args":{}}""");

        Assert.Empty(await device.ReceiveAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_revokeCredential_command_closes_the_socket_rather_than_writing_a_frame()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var observer = await BrokerObserver.SubscribeAsync(emqx, "veh/+/status");
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());

        // No device frame carries it. The credential is revoked centrally and the only thing the adapter
        // can do about a device holding a revoked one is stop serving it.
        await observer.PublishCommandAsync(vehicleId, """{"cmd":"revokeCredential"}""");

        Assert.True(await device.WaitForCloseAsync(), "revokeCredential must close the device's socket");
    }

    [Fact]
    public async Task A_revocation_signal_closes_a_matching_socket_inside_the_T12_budget()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());

        // Wait for the registration, so the measurement is of the close and not of the connect.
        await WaitForSessionAsync(harness, imei);

        // The message provisioning-svc actually publishes — serialised from its own record with the
        // kernel's options, which is what TrackerCache.PublishAsync does.
        var signal = JsonSerializer.Serialize(
            new TrackerCredentialSignal(
                TrackerEventTypes.TrackerRevoked, imei, vehicleId, ["01:23:45"], "decommissioned", DateTimeOffset.UtcNow),
            MageRideJson.Options);

        var started = Stopwatch.GetTimestamp();

        await harness.PublishRevocationAsync(signal);

        Assert.True(await device.WaitForCloseAsync(TimeSpan.FromSeconds(5)), "a revoked socket must be closed");

        var elapsed = Stopwatch.GetElapsedTime(started);

        // ADD §7.7.3: "force-closes any matching socket within 1 s". Two seconds of headroom for the
        // Redis pub/sub hop and the teardown's retained-offline publish on a containerised broker; the
        // budget itself is asserted at one second on the adapter's own clock by the log line and the
        // mageride.tracker.revocation.latency histogram.
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"the close took {elapsed}");
    }

    [Fact]
    public async Task A_rotation_signal_does_not_close_anything()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.Equal(Captures.Gt06LoginAck, await device.ReceiveAsync());
        await WaitForSessionAsync(harness, imei);

        // "Rotation is not revocation, and conflating them bricks devices" (C030). The replacement is
        // minted fourteen days early and the outgoing credential stays valid to its own expiry, precisely
        // so a tracker parked out of coverage can come back and collect it.
        var signal = JsonSerializer.Serialize(
            new TrackerCredentialSignal(
                TrackerEventTypes.CredentialRotated, imei, vehicleId, ["01:23:45"], null, DateTimeOffset.UtcNow),
            MageRideJson.Options);

        await harness.PublishRevocationAsync(signal);

        Assert.False(
            await device.WaitForCloseAsync(TimeSpan.FromSeconds(3)),
            "a credential rotation must not take a device off the air");
    }

    private static async Task WaitForSessionAsync(AdapterHarness harness, string imei)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (harness.Sessions.ForImei(imei) is not null)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"IMEI {imei} never registered a session");
    }

    /// <summary>
    /// Reads until something that is not the login acknowledgement arrives.
    /// </summary>
    /// <remarks>
    /// A downlink can land in the same read as a heartbeat reply, and on a loopback socket the two
    /// coalesce; the assertion is about the command frame, so the read is repeated until it is present.
    /// </remarks>
    private static async Task<byte[]> ReadFrameAsync(DeviceSocket device)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var frame = await device.ReceiveAsync(TimeSpan.FromSeconds(5));

            if (frame.Length > 0 && frame.Length != Captures.Gt06LoginAck.Length)
            {
                return frame;
            }
        }

        Assert.Fail("no command frame arrived on the device's socket");
        throw new InvalidOperationException("unreachable");
    }

    private static string Imei() =>
        "35" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
}
