using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis, one Redpanda and one EMQX shared by every integration test in this
/// assembly.
/// </summary>
/// <remarks>
/// <para>
/// The TestKit ships a collection per container and a test class can only join one, so
/// dispatch-svc — which needs all four — declares its own over the TestKit's fixtures rather
/// than hand-rolling containers (following <c>RideCollection</c>, C022).
/// </para>
/// <para>
/// Redis is not optional here the way it was for iam/registry/ride: the R-08 candidate index and
/// the R-10 Lua reservation <em>are</em> Redis, and a fake would prove only that the code calls
/// what it calls.
/// </para>
/// <para>
/// EMQX joined the set in C034. R-15's last will is a subscription, and a subscription's failure
/// mode is silence — a refused topic filter leaves the client connected and simply never delivers
/// a driver going offline. The broker also runs the repository's own <c>acl.conf</c>, which is
/// what decides whether <c>svc-dispatch</c> may read <c>veh/+/status</c> at all.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DispatchCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>,
      ICollectionFixture<EmqxFixture>
{
    public const string Name = "mageride-dispatch";
}
