using System.Net;
using MageRide.TestKit;
using MageRide.TripState.Tests.Infrastructure;

namespace MageRide.TripState.Tests.Integration;

/// <summary>US-18.1 / US-18.2 / US-8.6: both directions of the journey rating.</summary>
[Collection<TripStateCollection>]
public sealed class RatingTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_passenger_rates_the_journey_and_the_driver_is_the_subject()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/rating",
            new { stars = 5, text = "On time, thank you" },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await TripStateHarness.ReadJsonAsync(response);
        Assert.Equal(5, body.GetProperty("stars").GetInt32());

        // The passenger names nobody: the session is the only thing that knows who was driving,
        // so the ratee comes from it.
        var rating = Assert.Single(await harness.RatingsAsync(Guid.Parse(sessionId!)));
        Assert.Equal(passengerId, rating.RaterId);
        Assert.Equal(driverId, rating.RateeId);
        Assert.Equal("passenger_to_driver", rating.Direction);
    }

    [Fact]
    public async Task A_driver_rates_a_named_passenger()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/driver-rating",
            new { stars = 4, passengerId = passengerId.ToString() },
            harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var rating = Assert.Single(await harness.RatingsAsync(Guid.Parse(sessionId!)));
        Assert.Equal(driverId, rating.RaterId);
        Assert.Equal(passengerId, rating.RateeId);
        Assert.Equal("driver_to_passenger", rating.Direction);
    }

    /// <summary>
    /// The 409 the contract promises, and it is a unique index rather than a prior read — two taps
    /// on a flaky connection both see "no rating yet".
    /// </summary>
    [Fact]
    public async Task A_second_rating_from_the_same_passenger_is_409()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var bearer = harness.Tokens.PassengerOnDriverApp(passengerId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var first = await harness.PostAsync($"/v1/sessions/{sessionId}/rating", new { stars = 5 }, bearer);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await harness.PostAsync($"/v1/sessions/{sessionId}/rating", new { stars = 1 }, bearer);
        await ProblemDocument.AssertAsync(second, HttpStatusCode.Conflict, "conflict");

        Assert.Single(await harness.RatingsAsync(Guid.Parse(sessionId!)));
    }

    /// <summary>Both directions may coexist — the index is keyed on direction as well as rater.</summary>
    [Fact]
    public async Task Both_directions_can_be_recorded_for_one_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        await harness.PostAsync(
            $"/v1/sessions/{sessionId}/rating", new { stars = 5 }, harness.Tokens.PassengerOnDriverApp(passengerId));

        await harness.PostAsync(
            $"/v1/sessions/{sessionId}/driver-rating",
            new { stars = 3, passengerId = passengerId.ToString() },
            harness.Tokens.Driver(driverId));

        Assert.Equal(2, (await harness.RatingsAsync(Guid.Parse(sessionId!))).Count);
    }

    /// <summary>Only the driver of a session may rate its passengers.</summary>
    [Fact]
    public async Task Another_driver_cannot_rate_this_sessions_passengers()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var strangerId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/driver-rating",
            new { stars = 1, passengerId = passengerId.ToString() },
            harness.Tokens.Driver(strangerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Empty(await harness.RatingsAsync(Guid.Parse(sessionId!)));
    }

    /// <summary>Rating yourself would feed the D5' §4.1 reputation counters a fiction.</summary>
    [Fact]
    public async Task A_driver_cannot_rate_themselves()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/driver-rating",
            new { stars = 5, passengerId = driverId.ToString() },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(null)]
    public async Task Stars_outside_one_to_five_are_refused(int? stars)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var passengerId = await harness.CreateUserAsync("passenger");
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString();

        var response = await harness.PostAsync(
            $"/v1/sessions/{sessionId}/rating",
            new { stars },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Rating_a_session_that_does_not_exist_is_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis);

        var passengerId = await harness.CreateUserAsync("passenger");

        var response = await harness.PostAsync(
            $"/v1/sessions/{Guid.NewGuid()}/rating",
            new { stars = 5 },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "not-found");
    }
}
