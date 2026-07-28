using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// One EMQX, one Redpanda and one Redis shared by every test in this suite.
/// </summary>
/// <remarks>
/// <para>
/// Three containers is the price of the fence: "real EMQX, real Redpanda, real SignalR — no
/// in-memory shortcuts". They start once for the collection, which is what keeps a suite that
/// proves an end-to-end pipeline down to a broker start rather than one per test.
/// </para>
/// <para>
/// The TestKit does <b>not</b> reset them between tests, so every test here works in its own
/// namespace instead: fresh vehicle ids, a Kafka consumer group of its own, and — where a cell
/// stream is asserted on — coordinates far enough apart to land in different H3 cells.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class HotPathCollection
    : ICollectionFixture<EmqxFixture>, ICollectionFixture<RedpandaFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-hotpath";
}
