using MageRide.TestKit;

namespace MageRide.TcpAdapter.Tests.Infrastructure;

/// <summary>
/// One EMQX, one Redis and one Postgres shared by every integration test in this suite.
/// </summary>
/// <remarks>
/// <para>
/// Three containers, and each is load-bearing for a different line of the DoD. <b>EMQX</b> because
/// "publishes a retained <c>status=offline</c>" is a claim about what is on the broker afterwards, and
/// the fixture mounts the deployed <c>acl.conf</c> — so a <c>svc-</c> principal that lost its
/// <c>veh/#</c> grant fails here rather than in production. <b>Redis</b> because <c>imei:{imei}</c> is
/// T-03's cache, <c>prov:tracker</c> is T-12's channel and <c>veh:driver:{vehicleId}</c> is what T-11
/// reads. <b>Postgres</b> because the vehicle's mode — the other half of T-11 — is a column
/// registry-svc owns, and the whole point of the gate is that it agrees with that column.
/// </para>
/// <para>
/// Nothing is reset between tests, so every test works in its own namespace: a fresh IMEI, a fresh
/// vehicle id, its own ephemeral listener ports.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AdapterCollection
    : ICollectionFixture<EmqxFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-tcp-adapter";
}
