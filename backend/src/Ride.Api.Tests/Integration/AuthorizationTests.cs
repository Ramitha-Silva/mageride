using System.Net;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// AL-06 deny-by-default at the route, and <c>403 not-ride-participant</c> per ride.
/// </summary>
/// <remarks>
/// Two different questions, and both have to be answered: the role decides which *routes* a caller
/// may reach, and <c>RideRow.IsParticipant</c> decides which *rides*. A service that got the first
/// right and the second wrong would let any passenger read any other passenger's live location.
/// </remarks>
[Collection<RideCollection>]
public sealed class AuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_unauthenticated_booking_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        using var response = await harness.PostAsync("/v1/rides/request", new { }, bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_driver_cannot_book_a_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);
        var driver = await harness.CreateDriverAsync();

        using var response = await harness.PostAsync("/v1/rides/request", new { }, driver.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// C020 decision 4: opening the Driver App does not grant the driver role. This principal —
    /// <c>app=driver, role=passenger</c> — is real, not contrived.
    /// </summary>
    [Fact]
    public async Task A_passenger_signed_into_the_driver_app_cannot_accept_an_offer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passengerId = await harness.CreatePassengerAsync();
        var rideId = (await harness.RequestRideAsync(harness.Tokens.Passenger(passengerId)))
            .GetProperty("rideId").GetGuid();

        using var response = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{passengerId}/accept",
            new { offerId = Guid.NewGuid().ToString(), version = 1 },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The path <c>{driverId}</c> is descriptive, not a way to answer as somebody else.</summary>
    [Fact]
    public async Task A_driver_cannot_accept_an_offer_as_another_driver()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var offered = await harness.CreateDriverAsync();
        var impostor = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        var offer = await harness.OfferAsync(rideId, offered, ttlSeconds: 30);

        using var response = await harness.PostAsync(
            $"/v1/rides/{rideId}/offer/{offered.DriverId}/accept",
            new { offerId = offer.OfferId.ToString(), version = offer.Version },
            impostor.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-ride-participant");
    }

    [Fact]
    public async Task A_stranger_cannot_read_somebody_elses_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var stranger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var idleDriver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();

        using var byStranger = await harness.GetAsync($"/v1/rides/{rideId}", stranger);
        await ProblemDocument.AssertAsync(byStranger, HttpStatusCode.Forbidden, "not-ride-participant");

        // A driver who was never offered this ride is just as much a stranger to it.
        using var byDriver = await harness.GetAsync($"/v1/rides/{rideId}/state", idleDriver.Bearer);
        await ProblemDocument.AssertAsync(byDriver, HttpStatusCode.Forbidden, "not-ride-participant");
    }

    /// <summary>
    /// The offered driver must be able to read the ride before accepting — that read is the offer
    /// card and the 15-second countdown, and it is why <c>offered_driver_id</c> exists (0608).
    /// </summary>
    [Fact]
    public async Task The_offered_driver_can_read_the_ride_before_accepting()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var driver = await harness.CreateDriverAsync();

        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();
        await harness.OfferAsync(rideId, driver, ttlSeconds: 30);

        using var response = await harness.GetAsync($"/v1/rides/{rideId}", driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RideHarness.ReadJsonAsync(response);

        Assert.Equal("Offered", body.GetProperty("state").GetString());
        Assert.Equal(RideHarness.Pickup.Latitude, body.GetProperty("pickup").GetProperty("lat").GetDouble(), 6);
        // No driver block yet: nobody has been assigned.
        Assert.False(body.TryGetProperty("driver", out _));
    }

    [Fact]
    public async Task A_recovery_read_is_only_ever_for_the_callers_own_account()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var victimId = await harness.CreatePassengerAsync();
        var snooper = harness.Tokens.Passenger(await harness.CreatePassengerAsync());

        using var response = await harness.GetAsync($"/v1/rides/passenger/{victimId}/active", snooper);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    /// <summary>
    /// The internal plane answers 404 to an unauthenticated caller, matching what the gateway
    /// returns for the same prefix (C008): it should not be mappable from outside.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("not-the-key")]
    public async Task The_internal_plane_is_invisible_without_the_key(string? apiKey)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();

        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/matching", new { }, apiKey);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    /// <summary>Unset key means the routes are not mapped at all, not that they are open.</summary>
    [Fact]
    public async Task Without_a_configured_key_the_internal_plane_is_not_mapped()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ride:InternalApiKey"] = string.Empty,
        });

        var passenger = harness.Tokens.Passenger(await harness.CreatePassengerAsync());
        var rideId = (await harness.RequestRideAsync(passenger)).GetProperty("rideId").GetGuid();

        // The right key AND a valid bearer: the only thing left that can answer 404 is routing.
        // (The bearer is what stops the kernel's fallback policy answering 401 first — it applies
        // to requests with no endpoint too.)
        using var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/matching", new { }, bearer: passenger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
