using System.Net;
using System.Text.Json;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <b>DoD 2 — "three consecutive post-acceptance rider cancellations disable booking; a completed
/// ride resets the counter (AL-16)".</b>
/// </summary>
/// <remarks>
/// US-6A.10b, clause by clause: only post-acceptance cancels count, three consecutive disable
/// booking, any completed ride resets the counter to zero. Driven through the HTTP surface with one
/// passenger and one driver, cycling the full ride each time — a passenger can only have one
/// non-terminal ride at a time (<c>ux_rides_open_passenger</c>), so each cancellation has to end
/// before the next booking can start, which is exactly the sequence the rule describes.
/// </remarks>
[Collection<RideCollection>]
public sealed class BookingDisableTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Three_post_acceptance_cancellations_disable_booking()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        await CancelAfterAcceptAsync(harness, passenger, driver);
        await CancelAfterAcceptAsync(harness, passenger, driver);

        // Two is still allowed. The rule is three, and a passenger who has had two bad days must
        // still be able to get home.
        await CancelAfterAcceptAsync(harness, passenger, driver);

        // The third has landed; the fourth booking never starts.
        var refused = await harness.PostAsync(
            "/v1/rides/request", BookingBody(harness), passenger);

        await ProblemDocument.AssertAsync(refused, HttpStatusCode.Forbidden, "booking-disabled");
    }

    /// <summary>
    /// "The counter is consecutive — it resets to zero on any successfully completed ride."
    /// </summary>
    [Fact]
    public async Task A_completed_ride_resets_the_counter()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        await CancelAfterAcceptAsync(harness, passenger, driver);
        await CancelAfterAcceptAsync(harness, passenger, driver);

        // Two down. A completed ride now wipes the slate.
        await CompleteARideAsync(harness, passenger, driver);

        // …so the next two cancellations are the first two again, not the third and fourth.
        await CancelAfterAcceptAsync(harness, passenger, driver);
        await CancelAfterAcceptAsync(harness, passenger, driver);

        var allowed = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    /// <summary>
    /// "Only cancellations made after a driver has accepted count; pre-acceptance cancellations
    /// never count" (US-6A.9). Ten free cancels leave booking working.
    /// </summary>
    [Fact]
    public async Task Pre_acceptance_cancellations_never_count()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var booked = await harness.RequestRideAsync(passenger);

            var cancelled = await harness.PostAsync(
                $"/v1/rides/{booked.GetProperty("rideId").GetGuid()}/cancel",
                new { version = booked.GetProperty("version").GetInt64(), reason = "RIDER_CHANGED_MIND" },
                passenger);

            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal(
                "CancelledByRiderBeforeAccept",
                (await RideHarness.ReadJsonAsync(cancelled)).GetProperty("state").GetString());
        }

        var allowed = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    /// <summary>
    /// A ride the <em>driver</em> cancelled is not the passenger's fault, and neither is one no
    /// driver ever took. Counting either would disable a passenger for the platform's failures.
    /// </summary>
    [Fact]
    public async Task Cancellations_the_passenger_did_not_make_do_not_count()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ride = await AcceptedRideAsync(harness, passenger, driver);

            var cancelled = await harness.PostAsync(
                $"/v1/rides/{ride.RideId}/cancel",
                new { version = ride.Version, reason = "OTHER" },
                driver.Bearer);

            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal(
                "CancelledByDriver",
                (await RideHarness.ReadJsonAsync(cancelled)).GetProperty("state").GetString());
        }

        var allowed = await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger);
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    /// <summary>The disable is per passenger; one account's history never blocks another's.</summary>
    [Fact]
    public async Task A_disabled_passenger_does_not_disable_anybody_else()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var driver = await harness.CreateDriverAsync();

        var disabledId = await harness.CreatePassengerAsync();
        var disabled = harness.Tokens.Passenger(disabledId);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await CancelAfterAcceptAsync(harness, disabled, driver);
        }

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/rides/request", BookingBody(harness), disabled),
            HttpStatusCode.Forbidden,
            "booking-disabled");

        var innocent = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await harness.PostAsync("/v1/rides/request", BookingBody(harness), innocent)).StatusCode);
    }

    /// <summary>
    /// The threshold is configuration (AL-16 calls the cooldown configurable and §11.12 the
    /// amounts), so an operator raising it must actually change the behaviour rather than only the
    /// message.
    /// </summary>
    [Fact]
    public async Task The_threshold_is_configurable()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Ride:CancellationDisableThreshold"] = "2" });

        var passengerId = await harness.CreatePassengerAsync();
        var passenger = harness.Tokens.Passenger(passengerId);
        var driver = await harness.CreateDriverAsync();

        await CancelAfterAcceptAsync(harness, passenger, driver);
        await CancelAfterAcceptAsync(harness, passenger, driver);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/rides/request", BookingBody(harness), passenger),
            HttpStatusCode.Forbidden,
            "booking-disabled");
    }

    // -------------------------------------------------------------------------------------------

    private static object BookingBody(RideHarness harness) => new
    {
        clientRequestId = Guid.NewGuid().ToString(),
        pickup = new { lat = RideHarness.Pickup.Latitude, lng = RideHarness.Pickup.Longitude },
        dropoff = new { lat = RideHarness.Dropoff.Latitude, lng = RideHarness.Dropoff.Longitude },
        vehicleType = "three_wheeler",
        fareEstimateToken = harness.IssueFareToken(),
        paymentMethod = "cash",
    };

    /// <summary>Books, offers, accepts — the state a post-acceptance cancel is made from.</summary>
    private static async Task<(Guid RideId, long Version)> AcceptedRideAsync(
        RideHarness harness, string passenger, SeededDriver driver)
    {
        var booked = await harness.RequestRideAsync(passenger);
        var rideId = booked.GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver);

        var accepted = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        return (rideId, (await RideHarness.ReadJsonAsync(accepted)).GetProperty("version").GetInt64());
    }

    private static async Task CancelAfterAcceptAsync(RideHarness harness, string passenger, SeededDriver driver)
    {
        var ride = await AcceptedRideAsync(harness, passenger, driver);

        var cancelled = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "RIDER_CHANGED_MIND" },
            passenger);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var body = await RideHarness.ReadJsonAsync(cancelled);
        Assert.Equal("CancelledByRiderAfterAccept", body.GetProperty("state").GetString());
        Assert.Equal(5_000, body.GetProperty("penalty").GetProperty("amountMinor").GetInt64());
    }

    /// <summary>
    /// A ride carried all the way through <c>PaymentPending</c> to <c>Paid</c>.
    /// </summary>
    /// <remarks>
    /// The settlement is not decoration. <c>ux_rides_open_passenger</c> exempts <c>Completed</c> but
    /// not <c>PaymentPending</c> (C004 note (b)), so a passenger whose last ride is still awaiting
    /// payment cannot book the next one at all — the reset would be untestable and, more to the
    /// point, unreachable for a real passenger until fare-svc settles. So the ride is settled the
    /// way fare-svc settles it, which is also what R-05 requires before any earning posts.
    /// </remarks>
    private static async Task CompleteARideAsync(RideHarness harness, string passenger, SeededDriver driver)
    {
        var ride = await AcceptedRideAsync(harness, passenger, driver);

        var version = ride.Version;
        version = await AdvanceAsync(harness, ride.RideId, "arrive", driver.Bearer, version);
        version = await AdvanceAsync(harness, ride.RideId, "start", driver.Bearer, version);

        var completed = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/complete", new { version }, driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(
            "PaymentPending", (await RideHarness.ReadJsonAsync(completed)).GetProperty("state").GetString());

        var settled = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/payment-settled",
            new { paymentId = Guid.NewGuid().ToString(), paymentState = "Succeeded", settledMinor = 74_000 });

        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
        Assert.Equal("Paid", (await RideHarness.ReadJsonAsync(settled)).GetProperty("state").GetString());
    }

    private static async Task<long> AdvanceAsync(
        RideHarness harness, Guid rideId, string command, string bearer, long version)
    {
        var response = await harness.PostAsync($"/v1/rides/{rideId}/{command}", new { version }, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await RideHarness.ReadJsonAsync(response)).GetProperty("version").GetInt64();
    }
}
