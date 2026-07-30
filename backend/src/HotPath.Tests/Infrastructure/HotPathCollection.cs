using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// One EMQX, one Redpanda, one Redis and one Postgres shared by every test in this suite.
/// </summary>
/// <remarks>
/// <para>
/// Four containers is the price of the fence: "real EMQX, real Redpanda, real SignalR — no
/// in-memory shortcuts". They start once for the collection, which is what keeps a suite that
/// proves an end-to-end pipeline down to a broker start rather than one per test.
/// </para>
/// <para>
/// <b>Postgres joined at C040.</b> The first three hot-path services own no table, which is why it
/// was absent until then; persistence-writer-svc owns the write path into the system of record, and
/// every claim it makes — a <c>COPY</c> of a thousand rows clearing 3000 rows/s, a duplicate
/// <c>(vehicle_id, seq)</c> producing no second row, a kill mid-batch losing nothing — is a claim
/// about what a real hypertable did.
/// </para>
/// <para>
/// The TestKit does <b>not</b> reset them between tests, so every test here works in its own
/// namespace instead: fresh vehicle ids, a Kafka consumer group of its own, and — where a cell
/// stream is asserted on — coordinates far enough apart to land in different H3 cells.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class HotPathCollection
    : ICollectionFixture<EmqxFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-hotpath";
}
