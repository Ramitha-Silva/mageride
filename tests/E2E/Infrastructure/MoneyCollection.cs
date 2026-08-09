using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// One Postgres, one Redis and one Redpanda, shared by every money scenario.
/// </summary>
/// <remarks>
/// <para>
/// Three containers, not four. Every money path is Postgres — the ledger, the payment machine, the
/// fee rows and the Epic 23 subscriptions all live there — Redis carries D-08's
/// <c>wallet:bal:{driverId}</c> write-through and dispatch-svc's candidate index, and Redpanda
/// carries <c>ride.events</c> into dispatch-svc plus <c>wallet.events</c> out of the ledger's outbox.
/// <b>EMQX is out</b>, for C122's reason: nothing a fare, a fee, a top-up or a subscription does
/// touches a broker, so ride-svc's <c>Ride:VehicleStatusEnabled</c> and dispatch-svc's
/// <c>LastWillEnabled</c> are off and there is nothing left to connect to.
/// </para>
/// <para>
/// The containers are started once and never reset. Every scenario mints fresh passengers, drivers,
/// vehicles and organisations, and — unlike C120 — <see cref="MoneyFleet"/> truncates nothing: the
/// four fleets in this assembly are never disposed, so a reset run by whichever collection xUnit
/// happens to start second would pull the floor out from under services that are still running. What
/// this suite needs instead is that its rides never share a candidate pool with anybody else's, and
/// it takes that from the same static grid C120 and C122 walk
/// (<see cref="ModeCFleet.NextPlaces"/>).
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class MoneyCollection
    : ICollectionFixture<PostgresFixture>,
      ICollectionFixture<RedisFixture>,
      ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-e2e-money";
}
