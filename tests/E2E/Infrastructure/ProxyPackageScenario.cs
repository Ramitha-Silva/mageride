using System.Net;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// What every proxy, package and web scenario shares: the fleet, the skip when Docker is
/// unreachable, and the promise that a failure prints the ride.
/// </summary>
/// <remarks>
/// Derived classes carry <c>[Collection&lt;ProxyPackageCollection&gt;]</c> and
/// <c>[Trait("Category", "ProxyPackage")]</c> themselves, for C120's reason: xUnit resolves a
/// collection from the concrete test class, and the verify command
/// (<c>--filter Category=ProxyPackage</c>) is not something to leave to attribute inheritance.
/// </remarks>
public abstract class ProxyPackageScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private protected async Task RunAsync(Func<ProxyPackageFleet, ScenarioRides, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Before the journal wrapper, so a skip is a skip rather than a failure with a history.
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        var fleet = await ProxyPackageFleet.SharedAsync(postgres, redis, redpanda);

        await fleet.Journal.AroundAsync(rides => body(fleet, new ScenarioRides(rides)));
    }

    /// <summary>
    /// Books a package delivery and lets the real dispatch loop find it a driver, returning it in
    /// <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every state on the way is reached the way production reaches it: the driver goes on standby
    /// through dispatch-svc, the sender books through ride-svc quoted by fare-svc,
    /// <c>ride.requested</c> crosses Redpanda, dispatch-svc builds candidates — applying P-11's
    /// size × type gate on the way — reserves the driver and calls ride-svc's internal plane, and
    /// the driver accepts through ride-svc's own route.
    /// </para>
    /// <para>
    /// It stops at <c>Accepted</c> and never further. The two OTP gates are what these scenarios are
    /// about, and a helper that walked through them would be doing the thing under test.
    /// </para>
    /// </remarks>
    private protected static async Task<LiveRide> AcceptedPackageAsync(
        ProxyPackageFleet fleet,
        ScenarioRides rides,
        string recipientPhone,
        string packageSize = "S",
        string paymentMethod = "cash")
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(rides);

        var (pickup, dropoff) = ModeCFleet.NextPlaces();

        var sender = await fleet.CreatePassengerAsync("Ranjith");
        var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

        var ride = await fleet.BookPackageAsync(
            sender, driver, pickup, dropoff, recipientPhone, packageSize, paymentMethod: paymentMethod);

        rides.Add(ride.RideId);

        Assert.True(
            ride.PickupOtp is { Length: 4 },
            "A package booking answers with the sender's four-digit pickup OTP once, and this one did not (P-07).");

        return await AcceptAsync(fleet, ride, driver);
    }

    /// <summary>
    /// Books a proxy ride and drives it to <c>Accepted</c> — the state at which P-05 becomes
    /// answerable, because a driver only has a counterparty once there is a driver.
    /// </summary>
    private protected static async Task<LiveRide> AcceptedProxyAsync(
        ProxyPackageFleet fleet,
        ScenarioRides rides,
        Passenger booker,
        string riderPhone,
        string riderName = "Kamala",
        string paymentMethod = "cash")
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(rides);

        var (pickup, dropoff) = ModeCFleet.NextPlaces();

        var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));
        var ride = await fleet.BookProxyAsync(
            booker, driver, pickup, dropoff, riderPhone, riderName, paymentMethod);

        rides.Add(ride.RideId);

        return await AcceptAsync(fleet, ride, driver);
    }

    /// <summary>Waits for the real offer and takes it, which is the only way to reach Accepted.</summary>
    private static async Task<LiveRide> AcceptAsync(ProxyPackageFleet fleet, LiveRide ride, Driver driver)
    {
        var offer = await fleet.WaitForOfferAsync(ride.RideId);

        Assert.Equal(driver.DriverId, offer.DriverId);

        // Re-read rather than carry: placing the offer was ride-svc moving the ride on dispatch's
        // instruction, so the version the booking answered with is two behind by now.
        var offered = await fleet.ReadRideAsync(ride.RideId);

        using var accepted = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, offered.Version);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        return ride with
        {
            Version = (await ProxyPackageFleet.ReadJsonAsync(accepted)).GetProperty("version").GetInt64(),
        };
    }

    /// <summary>~70 m from a pickup: the same res-5 cell and well inside the 5 km post-filter.</summary>
    private protected static GeoPoint Near(GeoPoint pickup) => new(pickup.Latitude + 0.0006, pickup.Longitude);
}
