using System.Net;
using MageRide.Fare.Endpoints;
using MageRide.Fare.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fare.Tests.Integration;

/// <summary>
/// <c>POST /v1/fare/calculate</c> — the final fare a completed ride produces, and the one payment
/// row it may produce.
/// </summary>
[Collection<FareCollection>]
public sealed class FareCalculationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_completed_ride_is_priced_and_gets_one_initiated_payment()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 6.0), "calculate");

        // three_wheeler: Rs 100 + 5 km × Rs 80 = Rs 500, at 14:30 Colombo with no surcharge.
        Assert.Equal(50_000, fare.AmountMinor);
        Assert.Equal("LKR", fare.Currency);
        Assert.Equal(6.0, fare.Breakdown.DistanceKm);

        var payments = await harness.PaymentsAsync(ride.RideId);
        var payment = Assert.Single(payments);

        Assert.Equal("Initiated", payment.State);
        Assert.Equal(50_000, payment.AmountMinor);
        Assert.Equal("cash", payment.Method);
        Assert.Equal(fare.PaymentId, payment.Id);
    }

    /// <summary>
    /// ride-svc's <c>complete</c> is at-least-once, and what must be single-shot is the <b>ride</b>,
    /// not the request: two calls with two different idempotency keys still leave one fare.
    /// </summary>
    [Fact]
    public async Task Pricing_a_ride_twice_leaves_one_payment()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        var first = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 6.0), "first calculate");

        var second = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 9.0), "second calculate");

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal(first.AmountMinor, second.AmountMinor);

        // The second call carried a longer distance and did not re-price: a ride the passenger has
        // been quoted cannot move underneath them.
        Assert.Single(await harness.PaymentsAsync(ride.RideId));
    }

    /// <summary>
    /// Six concurrent completions — the shape a retrying caller and two replicas actually produce.
    /// The <c>FOR UPDATE</c> inside the writing transaction is what makes this one row.
    /// </summary>
    [Fact]
    public async Task Concurrent_completions_leave_one_payment()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => harness.CalculateAsync(ride.RideId, distanceKm: 6.0)));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        Assert.Single(await harness.PaymentsAsync(ride.RideId));
    }

    /// <summary>
    /// The tariff is resolved at the ride's <b>request</b> instant: a rate published while somebody
    /// is in the car must not change what they are charged.
    /// </summary>
    [Fact]
    public async Task A_rate_published_mid_journey_does_not_reprice_the_journey()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(
            vehicleType: "sedan", requestedAt: FareHarness.DefaultNow.AddMinutes(-40));

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                INSERT INTO fares.tariffs
                  (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct, night_surcharge_pct, effective_from)
                VALUES ('sedan', 90000, 90000, 20, 15, @EffectiveFrom);
                """,
                new { EffectiveFrom = FareHarness.DefaultNow.AddMinutes(-20) });
        }

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 3.0), "calculate");

        // The rate in force when the ride was requested: Rs 150 + 2 km × Rs 100 = Rs 350.
        Assert.Equal(35_000, fare.AmountMinor);
        Assert.Equal(15_000, fare.Breakdown.FirstKmMinor);
    }

    /// <summary>
    /// E-04 end to end: with no distance supplied, the fare is measured from the vehicle's track
    /// over the ride's <c>InProgress</c> window.
    /// </summary>
    [Fact]
    public async Task With_no_distance_supplied_the_fare_is_measured_from_the_track()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var startedAt = FareHarness.DefaultNow.AddMinutes(-25);

        var ride = await harness.Seed.RideAsync(startedAt: startedAt, endedAt: startedAt.AddMinutes(10));

        // 600 seconds at 10 m/s = 6 km.
        await harness.Seed.TrackAsync(ride.VehicleId, startedAt, seconds: 600, speedMps: 10);

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId), "calculate");

        Assert.InRange(fare.Breakdown.DistanceKm, 5.7, 6.3);

        // Rs 100 + ~5 km × Rs 80, so somewhere near Rs 500 — the point is that it came from the
        // track and not from the Rs 400 estimate.
        Assert.InRange(fare.AmountMinor, 47_000, 53_000);
    }

    /// <summary>
    /// Positions before the ride started are the driver approaching the pickup, and the passenger
    /// did not travel them.
    /// </summary>
    [Fact]
    public async Task The_drive_to_the_pickup_is_not_charged()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var approachAt = FareHarness.DefaultNow.AddMinutes(-40);
        var startedAt = FareHarness.DefaultNow.AddMinutes(-25);

        var ride = await harness.Seed.RideAsync(startedAt: startedAt, endedAt: startedAt.AddMinutes(10));

        // Fifteen minutes of approach, then ten minutes of ride, on the same vehicle.
        await harness.Seed.TrackAsync(ride.VehicleId, approachAt, seconds: 600, speedMps: 10);
        await harness.Seed.TrackAsync(ride.VehicleId, startedAt, seconds: 600, speedMps: 10);

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId), "calculate");

        // Only the second leg is inside the window: ~6 km, not ~12.
        Assert.InRange(fare.Breakdown.DistanceKm, 5.7, 6.3);
    }

    /// <summary>
    /// D5' §1.2's <c>distance_calculation_failed</c>: a ride whose tracker was silent is charged the
    /// number the passenger was shown, not a first-km charge for a journey across the city.
    /// </summary>
    [Fact]
    public async Task A_ride_with_no_track_falls_back_to_the_estimate()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var startedAt = FareHarness.DefaultNow.AddMinutes(-25);

        var ride = await harness.Seed.RideAsync(
            startedAt: startedAt, endedAt: startedAt.AddMinutes(10), fareEstimateMinor: 43_500);

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId), "calculate");

        Assert.Equal(43_500, fare.AmountMinor);
    }

    /// <summary>P-04: cash is paid by the rider; LankaQR and OnePay are charged to the booker.</summary>
    [Theory]
    [InlineData("cash", "rider")]
    [InlineData("lankaqr", "booker")]
    [InlineData("onepay", "booker")]
    public async Task The_payer_role_follows_the_booking_time_method(string method, string expectedRole)
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var booker = await harness.Seed.UserAsync("passenger");
        var ride = await harness.Seed.RideAsync(paymentMethod: method, bookerId: booker);

        await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 4.0), "calculate");

        var payment = Assert.Single(await harness.PaymentsAsync(ride.RideId));

        Assert.Equal(method, payment.Method);
        Assert.Equal(expectedRole, payment.PayerRole);
    }

    /// <summary>A ride that never completed has no fare to compute.</summary>
    [Theory]
    [InlineData("InProgress")]
    [InlineData("CancelledByRiderAfterAccept")]
    public async Task A_ride_that_did_not_complete_is_not_priced(string state)
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(state: state);

        using var response = await harness.CalculateAsync(ride.RideId, distanceKm: 4.0);
        var (code, _) = await FareHarness.ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("conflict", code);
        Assert.Empty(await harness.PaymentsAsync(ride.RideId));
    }

    [Fact]
    public async Task An_unknown_ride_is_not_found()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        using var response = await harness.CalculateAsync(Guid.NewGuid(), distanceKm: 4.0);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The internal plane is guarded. A caller without the key gets the same 404 the gateway returns
    /// for the prefix — a caller not entitled to it should not be able to map it.
    /// </summary>
    [Fact]
    public async Task The_internal_route_refuses_a_caller_without_the_key()
    {
        await using var harness = await FareHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/fare/calculate")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { rideId = ride.RideId.ToString() }),
        };

        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await harness.PaymentsAsync(ride.RideId));
    }

    /// <summary>
    /// With no key configured the route is not mapped at all, rather than open. Every completed ride
    /// goes through it.
    /// </summary>
    /// <remarks>
    /// The assertion is against what a path that was <em>never</em> mapped answers, rather than
    /// against a hard-coded status: an unmatched route on an authenticated pipeline is refused
    /// before routing can call it missing, and which of the two codes comes out is the kernel's
    /// business. What this test is about is that the two are indistinguishable — the route is gone.
    /// </remarks>
    [Fact]
    public async Task With_no_internal_key_the_route_is_not_mapped()
    {
        await using var harness = await FareHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Fare:InternalApiKey"] = null });

        var ride = await harness.Seed.RideAsync();

        using var response = await harness.CalculateAsync(ride.RideId, distanceKm: 4.0);
        using var neverMapped = await harness.CalculateAtAsync("/v1/fare/definitely-not-a-route", ride.RideId);

        Assert.Equal(neverMapped.StatusCode, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await harness.PaymentsAsync(ride.RideId));
    }
}
