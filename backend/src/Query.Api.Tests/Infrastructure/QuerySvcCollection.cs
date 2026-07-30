using MageRide.TestKit;

namespace MageRide.Query.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redis shared by every integration test here.
/// </summary>
/// <remarks>
/// No Redpanda and no EMQX, unlike fanout-svc's collection: query-svc consumes no topic and connects
/// to no broker. That is not an omission in the suite, it is the service's shape — everything it knows
/// it read from Redis or Postgres, and a broker in this collection would be two containers proving
/// nothing.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class QuerySvcCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-query";
}
