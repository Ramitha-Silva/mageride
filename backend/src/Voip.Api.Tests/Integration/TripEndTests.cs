using System.Net;
using MageRide.TestKit;
using MageRide.Voip.Domain;
using MageRide.Voip.Endpoints;
using MageRide.Voip.Tests.Infrastructure;

namespace MageRide.Voip.Tests.Integration;

/// <summary>
/// <b>Definition of done: "a signalling token is rejected after trip end."</b>
/// </summary>
/// <remarks>
/// Two claims, because D6' §6's "expiring at trip end" needs two mechanisms. A LiveKit token is a
/// <em>join</em> credential — its <c>exp</c> is checked at connect and never again — so refusing to
/// mint is only half of it; the other half is the room being closed when the ride ends, which is
/// what makes a token already in a handset's memory worthless.
/// </remarks>
[Collection(VoipCollection.Name)]
public sealed class TripEndTests(PostgresFixture postgres, RedpandaFixture redpanda)
{
    [Theory]
    [InlineData("Paid")]
    [InlineData("CashSettled")]
    [InlineData("CancelledByRiderAfterAccept")]
    [InlineData("CancelledByDriver")]
    [InlineData("NoShowRider")]
    public async Task No_token_is_minted_once_the_ride_has_ended(string terminal)
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        // It works while the ride is running…
        await harness.PostAsync<VoipTokenResponse>(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.PassengerId));

        await harness.SetRideStateAsync(ride.Id, terminal);

        // …and stops the moment it is not.
        using var afterwards = await harness.PostAsync(
            "/v1/voip/token", new { rideId = ride.Id }, harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.Conflict, afterwards.StatusCode);
        Assert.Contains("ride-terminal", await afterwards.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_cannot_be_started_on_a_ride_that_has_ended_either()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(state: "Paid");

        foreach (var callType in CallTypes.All)
        {
            using var response = await harness.PostAsync(
                "/v1/calls/start",
                new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType },
                harness.Tokens.Passenger(ride.PassengerId));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_call_still_running_is_ended_when_the_ride_ends()
    {
        // The half a token cannot express, end to end: a real terminal event on a real broker, and
        // the room is closed and the session row is stamped.
        await using var harness = await VoipHarness.StartAsync(postgres, redpanda);

        var ride = await harness.Seed.RideAsync();

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Null(Assert.Single(await harness.SessionsAsync(ride.Id)).EndedAt);

        await harness.SetRideStateAsync(ride.Id, "CashSettled");
        await harness.PublishRideEventAsync(ride.Id, "ride.settled");

        await WaitUntilAsync(async () => (await harness.SessionsAsync(ride.Id)).All(s => s.EndedAt is not null));

        var session = Assert.Single(await harness.SessionsAsync(ride.Id));

        Assert.NotNull(session.EndedAt);
        Assert.Contains($"ride_{ride.Id:D}", harness.Rooms.Closed);
    }

    [Fact]
    public async Task A_ride_event_that_is_not_a_terminal_leaves_the_call_alone()
    {
        // The trigger is the ride's STATE, not the event name — ride-svc publishes sixteen types and
        // most of them are moves through the machine. A consumer that closed a room on
        // `ride.driver_arrived` would hang up on two people mid-sentence.
        await using var harness = await VoipHarness.StartAsync(postgres, redpanda);

        var ride = await harness.Seed.RideAsync();

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(ride.PassengerId));

        await harness.PublishRideEventAsync(ride.Id, "ride.driver_arrived");

        // Give the consumer the same chance to get it wrong that the previous test gives it to get
        // it right: publish a terminal for a *different* ride and wait for that one instead.
        var other = await harness.Seed.RideAsync();

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = other.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(other.PassengerId));

        await harness.SetRideStateAsync(other.Id, "Paid");
        await harness.PublishRideEventAsync(other.Id, "ride.settled");

        await WaitUntilAsync(async () => (await harness.SessionsAsync(other.Id)).All(s => s.EndedAt is not null));

        Assert.Null(Assert.Single(await harness.SessionsAsync(ride.Id)).EndedAt);
        Assert.DoesNotContain($"ride_{ride.Id:D}", harness.Rooms.Closed);
    }

    [Fact]
    public async Task A_redelivered_terminal_event_closes_nothing_twice()
    {
        // The consumer prefers redelivery to a commit on any failure, which is only safe because
        // ending a ride's sessions is bound to `ended_at IS NULL`.
        await using var harness = await VoipHarness.StartAsync(postgres, redpanda);

        var ride = await harness.Seed.RideAsync();

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(ride.PassengerId));

        await harness.SetRideStateAsync(ride.Id, "Paid");

        await harness.PublishRideEventAsync(ride.Id, "ride.settled");
        await harness.PublishRideEventAsync(ride.Id, "ride.settled");
        await harness.PublishRideEventAsync(ride.Id, "ride.settled");

        await WaitUntilAsync(async () => (await harness.SessionsAsync(ride.Id)).All(s => s.EndedAt is not null));

        // A moment for the two redeliveries to be handled, so "closed once" is a real observation.
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Single(harness.Rooms.Closed, room => room == $"ride_{ride.Id:D}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail("the ride.events consumer did not act within 60 s.");
    }
}
