using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Integration;

/// <summary>
/// The DoD's second line: <b>an unbound or revoked IMEI is refused at connect</b>.
/// </summary>
/// <remarks>
/// Every case here is observed as the adapter closing the device's socket, because that is the only
/// thing a tracker protocol can be told. There is no error frame in GT06 and no status code in H02 — a
/// refused device sees a FIN, which is why the assertion is <see cref="DeviceSocket.WaitForCloseAsync"/>
/// rather than a response.
/// </remarks>
[Collection(AdapterCollection.Name)]
[Trait("Category", "Authentication")]
public sealed class AuthenticationTests(EmqxFixture emqx, RedisFixture redis, PostgresFixture postgres)
{
    [Fact]
    public async Task An_unbound_IMEI_is_refused_at_connect()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);

        // Nothing in the cache and nothing in provisioning-svc: an IMEI nobody bound.
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(Captures.UnboundImei));

        Assert.True(await device.WaitForCloseAsync(), "an unbound IMEI must be refused at connect");

        // And it is refused *before* anything is announced. Asserted on the registry rather than on the
        // broker: presence and samples are published only after a session registers, and a negative
        // assertion against a wildcard subscription cannot be made here — `veh/+/status` is retained, so
        // a new subscriber is replayed every other test's vehicles before this one has done anything.
        Assert.Equal(0, harness.Sessions.Count);

        // The cache missed, so provisioning-svc was asked — and its verdict is what refused the device.
        // An adapter that could not reach it refuses too (C030's "the safe way round"), which is a
        // different reason for the same close and is why this is asserted rather than assumed.
        Assert.True(harness.Provisioning.ValidateCalls > 0, "a cache miss must reach validate");
    }

    [Fact]
    public async Task A_revoked_IMEI_is_refused_at_connect()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");

        // T-12's slow half: the binding is REVOKED and imei:{imei} is absent, because a revoke deletes
        // it. "Present means ACTIVE" is what makes the absence sufficient.
        harness.Provisioning.Bind(imei, vehicleId);
        harness.Provisioning.Revoke(imei);
        await harness.InvalidateImeiCacheAsync(imei);

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));

        Assert.True(await device.WaitForCloseAsync(), "a revoked IMEI must be refused at connect");
        Assert.Equal(0, harness.Sessions.Count);
        Assert.True(harness.Provisioning.ValidateCalls > 0, "a cache miss must reach validate");
    }

    [Fact]
    public async Task A_quarantined_IMEI_is_refused_at_connect()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");

        // T-08: two devices claimed this IMEI and both records are held until an admin resolves it.
        // Neither keeps publishing, which is the whole point of holding both.
        harness.Provisioning.Bind(imei, vehicleId);
        harness.Provisioning.Quarantine(imei);

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));

        Assert.True(await device.WaitForCloseAsync(), "a quarantined IMEI must be refused at connect");
    }

    [Fact]
    public async Task An_identity_that_cannot_be_an_IMEI_is_refused_before_any_lookup()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);

        // A JT/T 808-2013 device: its six-byte BCD terminal number is twelve digits and
        // provisioning.yaml binds a fifteen-digit IMEI, so no binding could ever have existed. Refused
        // without a network call — and named as a malformed identity rather than as "not bound", because
        // the two need different fixes.
        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Jt808));

        await device.SendAsync(Captures.Jt808Position2013);

        Assert.True(await device.WaitForCloseAsync(), "a 12-digit terminal id cannot be an IMEI");
        Assert.Equal(0, harness.Provisioning.ValidateCalls);
    }

    [Fact]
    public async Task A_device_whose_binding_disappears_is_closed_at_its_next_revalidation()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        // ADD §7.7.3's "re-validates every 5 minutes on long-lived sockets", shortened to nothing so the
        // next frame triggers it. This is the backstop for a T-12 pub/sub message that was never
        // delivered — a pod that was restarting when it went out sees nothing on the channel.
        await using var harness = await AdapterHarness.StartAsync(
            emqx, redis, postgres, new Dictionary<string, string?> { ["Adapter:RevalidateInterval"] = "00:00:05" });

        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        harness.Provisioning.Bind(imei, vehicleId);
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        await using var device = await DeviceSocket.ConnectAsync(await harness.PortFor(ProtocolFamily.Gt06));

        await device.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await device.ReceiveAsync());

        // The credential is released while the socket is open, and no signal is published.
        harness.Provisioning.Revoke(imei);
        await harness.InvalidateImeiCacheAsync(imei);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await device.SendAsync(Captures.Gt06IgnitionOn);

        Assert.True(
            await device.WaitForCloseAsync(),
            "the five-minute re-validation must close a socket whose binding was released");
    }

    [Fact]
    public async Task Two_sockets_holding_one_IMEI_are_reported_to_provisioning_svc()
    {
        Skip.IfUnavailable(emqx, redis, postgres);
        await postgres.EnsureMigratedAsync();

        var imei = Imei();
        var vehicleId = Guid.NewGuid();

        await using var harness = await AdapterHarness.StartAsync(emqx, redis, postgres);
        await harness.SeedVehicleAsync(vehicleId, mode: "A");
        await harness.PrimeImeiCacheAsync(imei, vehicleId);

        var port = await harness.PortFor(ProtocolFamily.Gt06);

        await using var first = await DeviceSocket.ConnectAsync(port);
        await first.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await first.ReceiveAsync());

        // T-08's adapter half. C030's fence: at bind a clone arrives with two identities and is
        // decidable there; here it presents a copy of the genuine credential, and what tells them apart
        // is two live sockets holding one identity — which only this service can see.
        await using var second = await DeviceSocket.ConnectAsync(port);
        await second.SendAsync(Frames.Gt06Login(imei));
        Assert.NotEmpty(await second.ReceiveAsync());

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline && !harness.Provisioning.Quarantined.Contains(imei))
        {
            await Task.Delay(100);
        }

        Assert.Contains(imei, harness.Provisioning.Quarantined);

        // Both sockets are still open. Closing one would destroy the evidence and might well leave the
        // clone publishing; provisioning-svc adjudicates, and its answer arrives as a revocation on
        // prov:tracker which closes whichever sockets are left.
        Assert.False(
            await first.WaitForCloseAsync(TimeSpan.FromSeconds(2)),
            "the incumbent socket must not be closed by the adapter's own clone report");
    }

    private static string Imei() =>
        "35" + Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999).ToString();
}
