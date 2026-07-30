using MageRide.TestKit;

namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>One Redis, one Redpanda and one EMQX shared by every test in this suite.</summary>
/// <remarks>
/// <para>
/// Three containers, and each one is load-bearing. <b>Redis</b> holds everything this service
/// knows — the cell streams, the <c>share:{userId}</c> SET, the engagement marks and the directed-send
/// channel — so a fake would be a reimplementation of the component under test.
/// <b>Redpanda</b> is where <c>share.revoked</c> and <c>ride.accepted</c> come from, and the D-22
/// budget is measured from a real broker's delivery. <b>EMQX</b> publishes the retained last will
/// US-7.17's <c>offline</c> half depends on, against the deployed ACL.
/// </para>
/// <para>
/// The TestKit does <b>not</b> reset them between tests, so every test works in its own namespace
/// instead: fresh user, vehicle and ride ids, a Kafka consumer group of its own, and — where a cell
/// is asserted on — coordinates far enough apart to land in different H3 cells.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class FanoutCollection
    : ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-fanout";
}
