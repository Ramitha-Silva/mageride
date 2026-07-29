using MageRide.TestKit;

namespace MageRide.Reputation.Tests.Infrastructure;

/// <summary>
/// One collection sharing a Postgres, a Redis and a Redpanda, so the whole suite pays for each
/// container once.
/// </summary>
/// <remarks>
/// The TestKit's own <c>PostgresCollection</c> / <c>RedisCollection</c> definitions are per-fixture;
/// this component needs all three in one class (the gRPC latency test wants the real cache, and the
/// <c>fraud.suspected</c> test wants the real broker), and xUnit allows a class only one collection.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ReputationCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>, ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-reputation";
}
