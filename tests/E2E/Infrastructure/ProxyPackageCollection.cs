using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// One Postgres, one Redis and one Redpanda, shared by every proxy/package/web scenario.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three, not C120's four.</b> Nothing on the proxy round-trip, the package handover or the six
/// SCR-WT pages touches a broker: the P-13 answer travels <c>rides.outbox</c> → Redpanda →
/// fanout-svc → a WebSocket, the AL-45 and AL-21 links travel Redpanda → notification-svc → an SMS,
/// and public-bff reads Postgres and Redis. So R-15's last will, ride-svc's <c>veh/+/status</c>
/// subscription and fanout-svc's presence plane are all off in <see cref="ProxyPackageFleet"/>, and
/// an EMQX this suite never spoke to would be a container it could fail to start for.
/// </para>
/// <para>
/// The containers are the same ones C120 and C121 use — a collection fixture is per-assembly — and
/// this fleet <b>resets none of them</b>. See <see cref="ProxyPackageFleet"/> for why that is a
/// decision rather than an omission.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ProxyPackageCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-e2e-proxy-package";
}
