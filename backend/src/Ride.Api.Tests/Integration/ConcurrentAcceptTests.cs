using System.Net;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// DoD item 2: "two drivers accepting the same offer concurrently produce exactly one 200 and one
/// 409" — ADD §11.11, the single most failure-prone path in the platform.
/// </summary>
/// <remarks>
/// The point of these tests is that the **database** picks the winner. The conditional UPDATE is
/// the only arbiter: no advisory lock, no pre-flight SELECT, no application-side ordering. If the
/// implementation ever grows a read-then-write on this path, the race reopens and only a test that
/// actually races can see it.
/// </remarks>
[Collection<RideCollection>]
public sealed class ConcurrentAcceptTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_drivers_racing_one_offer_produce_exactly_one_winner()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var first = await harness.CreateDriverAsync();
        var second = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, first, ttlSeconds: 30);

        // Both hold the same offerId and the same version — the exact phantom-offer situation
        // §11.11 is written to resolve. Released together so the two UPDATEs overlap.
        using var start = new SemaphoreSlim(0, 2);

        async Task<(SeededDriver Driver, HttpResponseMessage Response)> AcceptAsync(SeededDriver driver)
        {
            await start.WaitAsync();
            return (driver, await harness.PostAsync(
                $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
                new { offerId = offer.OfferId.ToString(), version = offer.Version },
                driver.Bearer));
        }

        var firstAccept = AcceptAsync(first);
        var secondAccept = AcceptAsync(second);

        start.Release(2);

        var attempts = await Task.WhenAll(firstAccept, secondAccept);
        var responses = attempts.Select(a => a.Response).ToArray();

        var winners = attempts.Where(a => a.Response.StatusCode == HttpStatusCode.OK).ToArray();
        var losers = attempts.Where(a => a.Response.StatusCode == HttpStatusCode.Conflict).ToArray();

        Assert.Single(winners);
        Assert.Single(losers);

        await ProblemDocument.AssertAsync(losers[0].Response, HttpStatusCode.Conflict, "offer-already-accepted");

        // And the database agrees with the HTTP answer: one accepted driver, one Accepted ride.
        await using var connection = await harness.OpenAsync();

        var acceptedDriverId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT accepted_driver_id FROM rides.rides WHERE id = @RideId;", new { RideId = rideId });

        Assert.Equal(winners[0].Driver.DriverId, acceptedDriverId);

        var winnerBody = await RideHarness.ReadJsonAsync(winners[0].Response);
        Assert.Equal(rideId, winnerBody.GetProperty("rideId").GetGuid());
        Assert.Equal("Accepted", winnerBody.GetProperty("state").GetString());

        Assert.Equal("Accepted", await connection.ExecuteScalarAsync<string>(
            "SELECT state FROM rides.rides WHERE id = @RideId;", new { RideId = rideId }));

        // Exactly one ride.accepted reached the outbox. Two would mean two consumers were told a
        // different driver won (R-13).
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'ride.accepted';",
            new { RideId = rideId }));

        // The loser's failed attempt left no audit row behind — the transaction rolled back whole.
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.transitions WHERE ride_id = @RideId AND to_state = 'Accepted';",
            new { RideId = rideId }));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Ten callers, one offer. The two-driver case is the DoD's; this is the one that catches an
    /// implementation whose atomicity only holds for a pair.
    /// </summary>
    [Fact]
    public async Task A_crowd_racing_one_offer_still_produces_exactly_one_winner()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        const int Contenders = 10;

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var drivers = new List<SeededDriver>(Contenders);
        for (var i = 0; i < Contenders; i++)
        {
            drivers.Add(await harness.CreateDriverAsync());
        }

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, drivers[0], ttlSeconds: 30);

        using var gate = new SemaphoreSlim(0, Contenders);

        var attempts = drivers.Select(async driver =>
        {
            await gate.WaitAsync();
            return await harness.PostAsync(
                $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept",
                new { offerId = offer.OfferId.ToString(), version = offer.Version },
                driver.Bearer);
        }).ToArray();

        gate.Release(Contenders);

        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(Contenders - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var connection = await harness.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'ride.accepted';",
            new { RideId = rideId }));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// The same driver accepting twice under two different keys is a repeat, not a loss: the
    /// contract marks the operation idempotent, so the second call answers 200 with where the ride
    /// is rather than 409.
    /// </summary>
    [Fact]
    public async Task The_winner_accepting_again_is_told_they_already_hold_the_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        var body = new { offerId = offer.OfferId.ToString(), version = offer.Version };
        var path = $"/v1/rides/{rideId}/offer/{driver.DriverId}/accept";

        var won = await harness.PostAsync(path, body, driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);

        // A fresh Idempotency-Key, so the kernel's replay does not answer this one — the service
        // does, off the row.
        var again = await harness.PostAsync(path, body, driver.Bearer);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var repeat = await RideHarness.ReadJsonAsync(again);
        Assert.Equal("Accepted", repeat.GetProperty("state").GetString());

        await using var connection = await harness.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM rides.transitions WHERE ride_id = @RideId AND to_state = 'Accepted';",
            new { RideId = rideId }));
    }
}
