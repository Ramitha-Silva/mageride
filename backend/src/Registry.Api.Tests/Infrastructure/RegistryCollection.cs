using MageRide.TestKit;

namespace MageRide.Registry.Tests.Infrastructure;

/// <summary>
/// One Postgres, one Redis and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// The TestKit ships a collection per container and a test class can only join one; registry-svc
/// needs all three from C028 on. Postgres holds the vehicle, the assignment and the outbox row;
/// Redis holds the <c>lock:driver:{driverId}</c> the go-live selection coordinates on (D-03); and
/// the DoD's "<c>share.revoked</c> is emitted through the outbox" cannot be shown without a
/// broker. Declared here over the TestKit's fixtures rather than by hand-rolling containers,
/// following <c>IamCollection</c> (C020) and <c>RideCollection</c> (C022).
/// <para>
/// The C021 classes stay on the TestKit's <c>PostgresCollection</c>: nothing they assert touches
/// Redis or Redpanda, and moving them would make the walking skeleton's suite wait on two
/// containers it does not use.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class RegistryCollection
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>, ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-registry";
}
