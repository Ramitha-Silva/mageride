using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>US-6A.11 — a ride nobody can be found for ends in <c>ExpiredNoDriver</c> at 120 seconds.</b>
/// </summary>
/// <remarks>
/// <para>
/// The deadline is dispatch-svc's, the cancellation is ride-svc's, and neither service does the
/// other's half: dispatch arms a <c>dispatch.timers</c> row when it begins matching and, when the
/// row comes due, calls <c>POST /v1/internal/rides/{id}/system-cancel</c> with
/// <c>no_driver_found</c> — because R-01 makes ride-svc the sole writer of <c>rides.state</c>.
/// </para>
/// <para>
/// <b>An empty round is not the end of the ride.</b> A ride nobody is near stays in
/// <c>Matching</c>, because a driver who comes online inside the remaining window is still a
/// candidate. Only the deadline ends it, which is why the two halves are asserted separately below.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class DispatchTimeoutScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    [Fact]
    public Task A_ride_with_nobody_near_it_rests_in_matching_and_expires_at_the_deadline() =>
        RunAsync(async (fleet, rides) =>
        {
            var (pickup, dropoff) = ModeCFleet.NextPlaces();

            var passenger = await fleet.CreatePassengerAsync();

            // Seeded and left off standby: there is a driver in the world, and none in the pool.
            var driver = await fleet.CreateDriverAsync();

            var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
            rides.Add(ride.RideId);

            // dispatch-svc consumed the booking, began the candidate build and found nobody — and
            // left the ride where a late arrival could still be offered it.
            var matching = await fleet.WaitForStateAsync(ride.RideId, RideStates.Matching);

            var began = (await ReadTransitionsAsync(fleet, ride.RideId))
                .Single(row => row.To == RideStates.Matching);

            Assert.Equal(RideReasonCodes.DispatchCandidateBuild, began.Reason);
            Assert.Null(matching.CurrentOfferId);
            Assert.Null(await fleet.ReadOfferAsync(ride.RideId));

            // …and the 120-second deadline is running, measured from the request rather than from
            // whenever the consumer happened to pick the booking up.
            await fleet.UntilAsync(
                ride.RideId,
                async () => await GlobalDeadlineAsync(fleet, ride.RideId) is not null,
                "dispatch-svc never armed the US-6A.11 cascade deadline");

            var (fireAt, requestedAt) = (await GlobalDeadlineAsync(fleet, ride.RideId))!.Value;

            // US-6A.11 and D5' §3.5 both say 120 s. ADD §11.12 says 60 s for the same thing and the
            // C034 handoff records the conflict; the platform runs 120 and so does this assertion.
            Assert.Equal(
                ModeCFleet.GlobalDispatchTimeout.TotalSeconds, (fireAt - requestedAt).TotalSeconds, tolerance: 10);

            // The ride has been sitting in Matching this whole time rather than being cancelled by
            // an empty round.
            Assert.Equal(RideStates.Matching, (await fleet.ReadRideAsync(ride.RideId)).State);

            await fleet.PullForwardDispatchTimerAsync(ride.RideId, "ride_timeout");

            var expired = await fleet.WaitForStateAsync(ride.RideId, RideStates.ExpiredNoDriver);
            Assert.NotNull(expired.TerminalAt);

            var last = (await ReadTransitionsAsync(fleet, ride.RideId))[^1];

            Assert.Equal(RideStates.Matching, last.From);
            Assert.Equal(RideReasonCodes.NoDriverFound, last.Reason);
            Assert.Equal(RideTransitions.Actors.System, last.Actor);

            // The passenger's app is told, and nobody was charged for a ride that never happened.
            var events = await fleet.ReadEventsAsync(ride.RideId);

            Assert.Contains("ride.expired_no_driver", events);
            Assert.DoesNotContain("cancellation.penalty.accrued", events);
            Assert.Empty(await fleet.ReadPenaltiesAsync(passenger.Id));

            // And the passenger may book again immediately — an expired ride holds nothing open.
            Assert.Empty(await fleet.ReadRideTimersAsync(ride.RideId));
        });

    /// <summary>
    /// <b>A recorded gap.</b> A driver who comes online while a ride is waiting in
    /// <c>Matching</c> is <i>not</i> offered it — nothing re-runs the round for them, and the ride
    /// expires at the deadline with an eligible driver standing next to the pickup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// dispatch-svc's own note says the opposite in as many words: "An empty round is not the end of
    /// the ride. It leaves the ride in <c>Matching</c>, because <b>a driver who comes online inside
    /// the remaining window is still a candidate</b>." They are a candidate in the sense that a
    /// round would now find them — but only three things start a round (<c>ride.requested</c>, a
    /// decline, and an offer expiring), and going on standby is none of them. When the 120 s
    /// deadline fires, <c>RunGlobalTimeoutAsync</c> reschedules only if there is a <em>live
    /// offer</em>; with no offer it goes straight to <c>system-cancel</c>.
    /// </para>
    /// <para>
    /// This test asserts what the platform does, not what the note says, so that the day somebody
    /// wires a re-dispatch onto go-online it fails and is deleted. Recorded in the C120 handoff as a
    /// finding against dispatch-svc, with the two candidate fixes: dispatch a round when a driver
    /// enters the pool with rides waiting nearby, or make the global deadline re-run the cascade
    /// once before it gives up.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_driver_who_comes_online_while_a_ride_waits_is_not_offered_it() =>
        RunAsync(async (fleet, rides) =>
        {
            var (pickup, dropoff) = ModeCFleet.NextPlaces();

            var passenger = await fleet.CreatePassengerAsync();
            var latecomer = await fleet.CreateDriverAsync();

            var ride = await fleet.BookAsync(passenger, latecomer, pickup, dropoff);
            rides.Add(ride.RideId);

            await fleet.WaitForStateAsync(ride.RideId, RideStates.Matching);
            Assert.Null(await fleet.ReadOfferAsync(ride.RideId));

            // The driver taps Go Online, 70 m from the pickup. Nothing about this call mentions the
            // ride, and — the finding — nothing else does either.
            await fleet.GoOnlineAsync(latecomer, Near(pickup));

            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Null(await fleet.ReadOfferAsync(ride.RideId));
            Assert.Equal(RideStates.Matching, (await fleet.ReadRideAsync(ride.RideId)).State);

            // And when the deadline comes, it does not try again — it ends the ride, with an
            // eligible driver in the pool the whole time.
            await fleet.PullForwardDispatchTimerAsync(ride.RideId, "ride_timeout");

            await fleet.WaitForStateAsync(ride.RideId, RideStates.ExpiredNoDriver);

            Assert.Equal(
                "AVAILABLE",
                await PresenceOfAsync(fleet, latecomer.DriverId));
        });

    // -----------------------------------------------------------------------------------------

    private static async Task<(DateTimeOffset FireAt, DateTimeOffset RequestedAt)?> GlobalDeadlineAsync(
        ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync<(DateTimeOffset, DateTimeOffset)?>(
            """
            SELECT t.fire_at, r.created_at
              FROM dispatch.timers t JOIN rides.rides r ON r.id = t.ride_id
             WHERE t.ride_id = @RideId AND t.kind = 'ride_timeout' AND t.fired_at IS NULL;
            """,
            new { RideId = rideId });

        return row;
    }

    private static async Task<string?> PresenceOfAsync(ModeCFleet fleet, Guid driverId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { DriverId = driverId });
    }

    private static async Task<IReadOnlyList<(string? From, string To, string? Reason, string Actor)>>
        ReadTransitionsAsync(ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        return [.. await connection.QueryAsync<(string?, string, string?, string)>(
            """
            SELECT from_state, to_state, reason_code, actor_type
              FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts, id;
            """,
            new { RideId = rideId })];
    }
}
