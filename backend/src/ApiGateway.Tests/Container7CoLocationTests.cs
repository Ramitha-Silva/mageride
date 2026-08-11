using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.AppServices;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// The replica's Container 7 and the gateway's route table have to agree, and nothing but this
/// asserts it.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppServices</c> starts 22 services on loopback ports and tells the gateway where each one is,
/// keyed by the service's own name. That key is a cluster id in <c>gateway-routes.json</c>. If the
/// two ever disagree — a service renamed, a cluster added, a service moved to its own container —
/// the gateway keeps the shipped address for that cluster, which inside the replica is a host that
/// does not exist. The symptom is a 502 on one family of routes, in a container that reports healthy
/// because the gateway itself is fine.
/// </para>
/// <para>
/// This lives in <c>ApiGateway.Tests</c> rather than in a suite of its own because the route table is
/// the thing being agreed with, and <see cref="GatewayHarness.ClusterIds"/> already reads it from the
/// shipped file rather than from a copy.
/// </para>
/// </remarks>
public sealed class Container7CoLocationTests
{
    [Fact]
    public void Every_co_located_service_is_a_cluster_the_gateway_declares()
    {
        var clusters = GatewayHarness.ClusterIds.ToHashSet(StringComparer.Ordinal);

        // ocr-svc is co-located but has no cluster: it is queue-driven and fleet-svc reaches it
        // directly through Fleet:OcrBaseUrl. Asserted as an exception so that a cluster appearing for
        // it later is a decision somebody makes here, not a silent change of shape.
        var expectedWithoutCluster = new[] { "ocr-svc" };

        var unroutable = Container7.Services
            .Select(service => service.Name)
            .Except(clusters, StringComparer.Ordinal)
            .Except(expectedWithoutCluster, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unroutable.Count == 0,
            "these services run inside Container 7 but the gateway declares no cluster for them, so "
            + "nothing routes to them: " + string.Join(", ", unroutable));
    }

    [Fact]
    public void Every_cluster_is_either_co_located_or_declared_to_be_elsewhere()
    {
        var coLocated = Container7.Services
            .Select(service => service.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = GatewayHarness.ClusterIds
            .Where(cluster => !coLocated.Contains(cluster))
            .Where(cluster => !Container7.ClustersElsewhere.Contains(cluster))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            "the gateway declares these clusters and Container 7 neither hosts them nor lists them in "
            + "ClustersElsewhere, so the replica has no address for them: "
            + string.Join(", ", unaccounted));
    }

    [Fact]
    public void Nothing_is_claimed_to_be_elsewhere_and_co_located_at_the_same_time()
    {
        var both = Container7.Services
            .Select(service => service.Name)
            .Intersect(Container7.ClustersElsewhere, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            both.Count == 0,
            "these are listed as running elsewhere but are started inside Container 7: "
            + string.Join(", ", both));
    }

    /// <summary>
    /// The loopback ports are distinct and none of them collides with the published gateway port.
    /// </summary>
    /// <remarks>
    /// A collision would surface as whichever service started second failing to bind, which
    /// <c>CoLocatedHost</c> reports — but it reports it at container start-up on the replica, and this
    /// reports it on a developer's machine.
    /// </remarks>
    [Fact]
    public void The_assigned_ports_are_distinct_and_avoid_the_gateway()
    {
        var assigned = MageRide.HotPath.Host.CoLocatedHost.Addresses(
            Container7.Services, Container7.FirstServicePort, "127.0.0.1");

        Assert.Equal(Container7.Services.Count, assigned.Values.Distinct(StringComparer.Ordinal).Count());

        Assert.DoesNotContain(
            $":{Container7.GatewayPort}",
            string.Join(' ', assigned.Values));
    }
}
