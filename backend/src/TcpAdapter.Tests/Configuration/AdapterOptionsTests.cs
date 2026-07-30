using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Identity;
using MageRide.TcpAdapter.Protocols;

namespace MageRide.TcpAdapter.Tests.Configuration;

/// <summary>
/// The configuration contract: which port each family listens on, and the sticky-hash the deployment
/// has to agree with.
/// </summary>
[Trait("Category", "Configuration")]
public sealed class AdapterOptionsTests
{
    /// <summary>
    /// The default is D7' §2.1's <c>5023–5026</c> in D6' §4.1's family order, which is what
    /// <c>infra/env/.env.app.example</c> ships and what <c>infra/deploy/haproxy.cfg</c> routes.
    /// </summary>
    [Fact]
    public void The_default_ports_are_the_ones_the_deployment_routes()
    {
        var ports = new AdapterOptions().ResolvePorts();

        Assert.Equal(5023, ports[ProtocolFamily.Gt06]);
        Assert.Equal(5024, ports[ProtocolFamily.Jt808]);
        Assert.Equal(5025, ports[ProtocolFamily.H02]);
        Assert.Equal(5026, ports[ProtocolFamily.NmeaUdp]);
    }

    [Fact]
    public void The_port_list_is_positional_and_its_length_is_checked()
    {
        // A CSV because an env_file is a flat map and Adapter__Ports__0 is the only way to bind an array
        // from one. Positional, so a short list is an error rather than three listeners and a surprise.
        var ports = new AdapterOptions { Ports = "15023, 15024, 15025, 15026" }.ResolvePorts();

        Assert.Equal(15_023, ports[ProtocolFamily.Gt06]);
        Assert.Equal(15_026, ports[ProtocolFamily.NmeaUdp]);

        Assert.Throws<InvalidOperationException>(() => new AdapterOptions { Ports = "5023,5024" }.ResolvePorts());
        Assert.Throws<InvalidOperationException>(() => new AdapterOptions { Ports = "5023,5024,5025,http" }.ResolvePorts());
        Assert.Throws<InvalidOperationException>(() => new AdapterOptions { Ports = "5023,5024,5025,99999" }.ResolvePorts());
    }

    [Fact]
    public void Zero_means_let_the_OS_choose()
    {
        // What the test suite asks for, so it can run beside a dev stack already holding 5023-5026. A
        // deployment never sets it: a device dials a fixed port.
        var ports = new AdapterOptions { Ports = "0,0,0,0" }.ResolvePorts();

        Assert.All(ports.Values, port => Assert.Equal(0, port));
    }

    [Fact]
    public void A_family_can_be_switched_off_without_moving_the_others()
    {
        // ADD §7.7.1's StatefulSet-per-family isolation, as a configuration rather than four binaries.
        var options = new AdapterOptions { Jt808Enabled = false, H02Enabled = false };

        Assert.True(options.IsEnabled(ProtocolFamily.Gt06));
        Assert.False(options.IsEnabled(ProtocolFamily.Jt808));
        Assert.False(options.IsEnabled(ProtocolFamily.H02));
        Assert.True(options.IsEnabled(ProtocolFamily.NmeaUdp));

        // And the ports do not shift: the list is positional.
        Assert.Equal(5026, options.ResolvePorts()[ProtocolFamily.NmeaUdp]);
    }

    /// <summary>
    /// The IMEI shard hash is stable across processes.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>, which .NET randomises per process — every
    /// pod would compute a different shard for the same device and the check would fire constantly. The
    /// values below are the algorithm's, and asserting them is what stops a "harmless" change to the
    /// hash silently making every pod disagree about which devices are its own.
    /// </remarks>
    [Fact]
    public void The_IMEI_shard_hash_is_stable()
    {
        // The literal is FNV-1a over the IMEI's ASCII digits. Pinned rather than recomputed by the test,
        // because the thing that has to hold is that this number is the same in every process and in
        // whatever the load balancer is configured with.
        Assert.Equal(4_043_161_347u, ImeiShards.Hash("356938035643809"));

        var first = ImeiShards.ShardFor("356938035643809", 3);
        var second = ImeiShards.ShardFor("356938035643809", 3);

        Assert.Equal(first, second);
        Assert.InRange(first, 0, 2);

        // One shard means everything is local, which is the single-pod deployment.
        Assert.Equal(0, ImeiShards.ShardFor("356938035643809", 1));
        Assert.Equal(0, ImeiShards.ShardFor("356938035643809", 0));

        // Different devices spread. Not a distribution test — just that the hash is not constant, which
        // is the failure that would put a whole fleet on one pod.
        var shards = Enumerable.Range(0, 64)
            .Select(index => ImeiShards.ShardFor($"3569380356438{index:D2}", 3))
            .Distinct()
            .ToList();

        Assert.True(shards.Count > 1, "the shard hash put 64 devices in one shard");
    }
}
