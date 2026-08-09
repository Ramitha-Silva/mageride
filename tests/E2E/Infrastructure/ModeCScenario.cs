using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// What every Mode C scenario shares: the fleet, the skip when Docker is unreachable, and the
/// promise that a failure prints the ride.
/// </summary>
/// <remarks>
/// <para>
/// Derived classes carry <c>[Collection&lt;ModeCCollection&gt;]</c> and
/// <c>[Trait("Category", "ModeC")]</c> themselves rather than inheriting them: xUnit resolves a
/// collection from the concrete test class, and the verify command
/// (<c>--filter Category=ModeC</c>) is not something to leave to attribute inheritance.
/// </para>
/// <para>
/// <b>Every scenario body runs inside <see cref="RideJournal.AroundAsync"/></b>, and every ride it
/// touches is added to the list it is handed. That is the whole of DoD 3 — the ride ids are
/// collected as they are created, so a failure eleven assertions later still knows which rides to
/// print.
/// </para>
/// </remarks>
public abstract class ModeCScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
{
    /// <summary>
    /// Runs a scenario against the fleet, skipping when the containers are not available and
    /// attaching every named ride's history to whatever fails.
    /// </summary>
    private protected async Task RunAsync(Func<ModeCFleet, ScenarioRides, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Before the journal wrapper, so a skip is a skip rather than a failure with a history.
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var fleet = await ModeCFleet.SharedAsync(postgres, redis, redpanda, emqx);

        await fleet.Journal.AroundAsync(rides => body(fleet, new ScenarioRides(rides)));
    }

    /// <summary>
    /// Books a ride and lets the real dispatch loop find it a driver, returning it in
    /// <paramref name="state"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every state on the way is reached the way production reaches it.</b> The driver goes on
    /// standby through dispatch-svc; the passenger books through ride-svc, quoted by fare-svc;
    /// <c>ride.requested</c> crosses Redpanda; dispatch-svc builds candidates, reserves the driver
    /// and calls ride-svc's internal plane; the driver accepts, arrives, starts and completes
    /// through ride-svc's own routes. Nothing here calls <c>/v1/internal/rides/{id}/matching</c> or
    /// <c>/offer</c> directly — those are dispatch-svc's to call, and a scenario that called them
    /// would be the mock the C120 fence forbids.
    /// </para>
    /// <para>
    /// The pickup is a fresh one per ride (<see cref="ModeCFleet.NextPlaces"/>), so this ride's
    /// driver is the only candidate for it.
    /// </para>
    /// </remarks>
    private protected static async Task<LiveRide> DriveToAsync(
        ModeCFleet fleet,
        ScenarioRides rides,
        string state,
        string vehicleType = "three_wheeler",
        string paymentMethod = "cash",
        string? packageSize = null)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(rides);

        var (pickup, dropoff) = ModeCFleet.NextPlaces();

        var passenger = await fleet.CreatePassengerAsync();

        // Matching is where a ride rests when nobody can be offered it: dispatch-svc marks it,
        // finds no candidate, and leaves it there until the US-6A.11 deadline. So the driver for
        // this one is seeded and left off standby — an ordinary situation, and the only
        // deterministic way to hold a ride in Matching against a live dispatcher.
        var driver = state == "Matching"
            ? await fleet.CreateDriverAsync(vehicleType)
            : await fleet.CreateOnlineDriverAsync(Near(pickup), vehicleType);

        var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff, vehicleType, paymentMethod, packageSize);
        rides.Add(ride.RideId);

        if (state == "Matching")
        {
            var matching = await fleet.WaitForStateAsync(ride.RideId, "Matching");
            return ride with { Version = matching.Version };
        }

        var offer = await fleet.WaitForOfferAsync(ride.RideId);

        Assert.Equal(driver.DriverId, offer.DriverId);

        // Re-read rather than carry: placing the offer was ride-svc moving the ride on dispatch's
        // instruction, so the version the booking answered with is two behind by now.
        var offered = await fleet.ReadRideAsync(ride.RideId);
        ride = ride with { Version = offered.Version };

        if (state == "Offered")
        {
            return ride;
        }

        using (var accepted = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, offered.Version))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
            ride = ride with { Version = (await ModeCFleet.ReadJsonAsync(accepted)).GetProperty("version").GetInt64() };
        }

        if (state == "Accepted")
        {
            return ride;
        }

        // A package leaves Accepted through the P-07 pickup gate rather than through `start`, which
        // is exactly the substitution ADD §11.16 makes — one machine, three kinds.
        if (packageSize is not null)
        {
            return await DrivePackageAsync(fleet, ride, state);
        }

        ride = await fleet.AdvanceAsync(ride, "arrive");

        if (state == "DriverArrived")
        {
            return ride;
        }

        ride = await fleet.AdvanceAsync(ride, "start");

        if (state == "InProgress")
        {
            return ride;
        }

        if (state != "PaymentPending")
        {
            throw new ArgumentOutOfRangeException(
                nameof(state), state,
                "DriveToAsync walks the non-terminal states only; a terminal state is what a scenario asserts, "
                + "and Completed is transient — `complete` moves through it to PaymentPending in one transaction.");
        }

        return await fleet.AdvanceAsync(ride, "complete");
    }

    /// <summary>The package half of the walk: the pickup OTP gate, then the delivery OTP gate.</summary>
    /// <remarks>
    /// A delivery has no <c>DriverArrived</c> to stop at — ADD §11.16 substitutes the two OTP gates
    /// for <c>start</c> and <c>complete</c>, and there is no gate between <c>Accepted</c> and the
    /// pickup — so a caller asking for one is asking for a state this kind of ride does not rest in.
    /// </remarks>
    private static async Task<LiveRide> DrivePackageAsync(ModeCFleet fleet, LiveRide ride, string state)
    {
        if (state is not ("InProgress" or "PaymentPending"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state), state, "A package delivery rests in Accepted, InProgress or PaymentPending.");
        }

        using (var picked = await ModeCFleet.PostAsync(
            fleet.RideClient, $"/v1/rides/{ride.RideId}/package/pickup-otp",
            new { otp = ride.PickupOtp }, ride.Driver.Bearer))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, picked.StatusCode);
            ride = ride with { Version = (await ModeCFleet.ReadJsonAsync(picked)).GetProperty("version").GetInt64() };
        }

        if (state == "InProgress")
        {
            return ride;
        }

        // The delivery code is minted at pickup and leaves on `package.picked_up` — the only place
        // anything can read it, and exactly where notification-svc reads it in production.
        var handover = await fleet.ReadEventPayloadAsync(ride.RideId, "package.picked_up");
        var deliveryOtp = handover.GetProperty("payload").GetProperty("deliveryOtp").GetString();

        using var delivered = await ModeCFleet.PostAsync(
            fleet.RideClient, $"/v1/rides/{ride.RideId}/package/delivery-otp",
            new { otp = deliveryOtp }, ride.Driver.Bearer);

        Assert.Equal(System.Net.HttpStatusCode.OK, delivered.StatusCode);

        return ride with { Version = (await ModeCFleet.ReadJsonAsync(delivered)).GetProperty("version").GetInt64() };
    }

    /// <summary>~70 m from a pickup: the same res-5 cell and well inside the 5 km post-filter.</summary>
    private protected static GeoPoint Near(GeoPoint pickup) => new(pickup.Latitude + 0.0006, pickup.Longitude);
}

/// <summary>
/// The rides a scenario has created, so a failure knows whose history to print.
/// </summary>
/// <remarks>
/// A wrapper rather than a bare <c>List&lt;Guid&gt;</c> so the intent reads at the call site:
/// <c>rides.Add(rideId)</c> is "this ride is part of the diagnosis", not "remember this for later".
/// </remarks>
public sealed class ScenarioRides(List<Guid> rides)
{
    public void Add(Guid rideId) => rides.Add(rideId);
}
