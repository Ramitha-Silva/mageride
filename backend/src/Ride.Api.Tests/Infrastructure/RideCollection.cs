using MageRide.TestKit;

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// One Postgres and one Redpanda shared by every integration test in this assembly.
/// </summary>
/// <remarks>
/// The TestKit ships a collection per container and a test class can only join one. ride-svc needs
/// both: the aggregate and its outbox are in Postgres, and the DoD's fourth item — "ride.* events
/// reach Redpanda through the outbox, never by direct publish" — cannot be shown without a broker.
/// The collection is declared here over the TestKit's fixtures rather than by hand-rolling
/// containers, following <c>IamCollection</c> (C020).
/// </remarks>
[CollectionDefinition(Name)]
public sealed class RideCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-ride";
}
