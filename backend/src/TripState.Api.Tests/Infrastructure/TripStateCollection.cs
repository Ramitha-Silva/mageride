using MageRide.TestKit;

namespace MageRide.TripState.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis, one Redpanda and one EMQX shared by every integration test here.
/// </summary>
/// <remarks>
/// Postgres holds the session, the mutex index and the outbox row; Redis holds the
/// <c>lock:session:{driverId}</c> fact D-03 publishes; Redpanda is what the outbox dispatcher
/// drains to, so "the end is durable" cannot be shown without a broker; and EMQX is where the
/// R-15/T-04 last will actually comes from.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class TripStateCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-trip-state";
}
