using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// The TestKit ships a collection per container and a test class can only join one, so
/// dispatch-svc — which needs all three — declares its own over the TestKit's fixtures rather
/// than hand-rolling containers (following <c>RideCollection</c>, C022).
/// <para>
/// Redis is not optional here the way it was for iam/registry/ride: the R-08 candidate index and
/// the R-10 Lua reservation <em>are</em> Redis, and a fake would prove only that the code calls
/// what it calls.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DispatchCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>, ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-dispatch";
}
