using System.Globalization;
using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>ADD §11.11 — atomic single-winner acceptance.</b> N drivers answer one offer at the same
/// instant; exactly one of them gets the ride, a hundred times running.
/// </summary>
/// <remarks>
/// <para>
/// The point is that the <b>database</b> picks the winner. ride-svc's accept is one conditional
/// <c>UPDATE</c> and nothing else — no advisory lock, no pre-flight <c>SELECT</c>, no
/// application-side ordering — so the only way to see the race reopen is to actually race it, and
/// the only way to trust that a hundred times is to run it a hundred times. ride-svc's own suite
/// races two drivers and ten in-process; this one races them against the live platform, with the
/// offer placed by dispatch-svc over Redpanda rather than by the test.
/// </para>
/// <para>
/// <b>Why more than one caller can hold the same offer id at all.</b> The accept has deliberately
/// no <c>offered_driver_id</c> predicate — adding one would turn a concurrent double-accept into
/// two 403s and hide the race rather than resolve it — so the phantom-offer situation §11.11 is
/// written for is reproducible exactly as it happens in production: several drivers holding one
/// offer, one of whom is the driver dispatch-svc actually reserved.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class ConcurrentAcceptScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    /// <summary>Three callers per round: the reserved driver and two who hold the same offer.</summary>
    private const int Contenders = 3;

    /// <summary>
    /// C120's definition of done — "the concurrency scenario produces exactly one winner across 100
    /// runs".
    /// </summary>
    /// <remarks>
    /// <c>MAGERIDE_E2E_RACE_RUNS</c> shortens it while working on something else. It is not a knob
    /// for CI: a hundred is what the DoD asks for and what the default runs.
    /// </remarks>
    private static int Runs =>
        int.TryParse(
            Environment.GetEnvironmentVariable("MAGERIDE_E2E_RACE_RUNS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : 100;

    [Fact]
    public Task One_hundred_races_produce_one_hundred_single_winners() => RunAsync(async (fleet, rides) =>
    {
        var runs = Runs;
        var losses = new List<string>();

        for (var run = 1; run <= runs; run++)
        {
            var (pickup, dropoff) = ModeCFleet.NextPlaces();

            var passenger = await fleet.CreatePassengerAsync();

            // One driver on standby — the one dispatch-svc will reserve — and two more who exist
            // and will answer the same offer. Only presence decides who is offered; anybody with
            // the offer id can attempt the accept, which is the situation being raced.
            var reserved = await fleet.CreateOnlineDriverAsync(Near(pickup));
            var contenders = new List<Driver> { reserved };

            for (var i = 1; i < Contenders; i++)
            {
                contenders.Add(await fleet.CreateDriverAsync());
            }

            var ride = await fleet.BookAsync(passenger, reserved, pickup, dropoff);
            rides.Add(ride.RideId);

            var offer = await fleet.WaitForOfferAsync(ride.RideId);
            Assert.Equal(reserved.DriverId, offer.DriverId);

            var version = (await fleet.ReadRideAsync(ride.RideId)).Version;

            // Released together so the UPDATEs overlap. A gate rather than Task.WhenAll on its own:
            // starting the tasks in order would let the first one finish before the last began.
            using var gate = new SemaphoreSlim(0, Contenders);

            var attempts = contenders.Select(async driver =>
            {
                await gate.WaitAsync(TestContext.Current.CancellationToken);
                return (driver, Response: await fleet.AcceptAsync(ride.RideId, driver, offer.Id, version));
            }).ToArray();

            gate.Release(Contenders);

            var answers = await Task.WhenAll(attempts);

            var winners = answers.Where(answer => answer.Response.StatusCode == HttpStatusCode.OK).ToArray();
            var refused = answers.Where(answer => answer.Response.StatusCode == HttpStatusCode.Conflict).ToArray();

            if (winners.Length != 1 || refused.Length != Contenders - 1)
            {
                losses.Add(
                    $"run {run}: {winners.Length} winner(s) and {refused.Length} conflict(s) out of {Contenders} — "
                    + string.Join(", ", answers.Select(answer => $"{answer.driver.DriverId:N}={(int)answer.Response.StatusCode}")));
            }
            else
            {
                // The database agrees with the HTTP answers, and said so once.
                var accepted = await fleet.ReadRideAsync(ride.RideId);

                if (accepted.State != "Accepted" || accepted.AcceptedDriverId != winners[0].driver.DriverId)
                {
                    losses.Add(
                        $"run {run}: HTTP said {winners[0].driver.DriverId} won, the row says "
                        + $"{accepted.State}/{accepted.AcceptedDriverId}");
                }

                // Exactly one `ride.accepted`. Two would mean two consumers were told a different
                // driver held the ride (R-13), and the loser's rolled-back transaction left nothing.
                if (await CountAsync(fleet, ride.RideId, "ride.accepted") != 1)
                {
                    losses.Add($"run {run}: more than one ride.accepted reached the outbox");
                }

                if (await CountTransitionsAsync(fleet, ride.RideId, "Accepted") != 1)
                {
                    losses.Add($"run {run}: a losing accept left an audit row behind");
                }

                // The loser was told *why*, in a code the driver app branches on to show
                // "Taken" rather than "Expired" (SCR-DA-014's split).
                if (refused.Length > 0
                    && await ModeCFleet.ProblemCodeAsync(refused[0].Response) != "offer-already-accepted")
                {
                    losses.Add($"run {run}: a losing driver was not told the offer was already accepted");
                }
            }

            foreach (var answer in answers)
            {
                answer.Response.Dispose();
            }

            // Release the winner and end the ride, so a hundred rounds do not leave a hundred
            // drivers ON_RIDE and a hundred arrival graces ticking under the rest of the suite.
            using var swept = await fleet.SystemCancelAsync(ride.RideId, "admin_intervention");
            await fleet.GoOfflineAsync(reserved);
        }

        Assert.True(
            losses.Count == 0,
            $"{losses.Count} of {runs} races did not produce exactly one winner:\n  " + string.Join("\n  ", losses));
    });

    /// <summary>
    /// The winner asking again is a repeat, not a loss: the operation is idempotent, so a driver
    /// whose phone retried on a flaky connection is told they hold the ride rather than that they
    /// lost it.
    /// </summary>
    [Fact]
    public Task The_winner_accepting_again_is_told_they_already_hold_the_ride() =>
        RunAsync(async (fleet, rides) =>
        {
            var (pickup, dropoff) = ModeCFleet.NextPlaces();

            var passenger = await fleet.CreatePassengerAsync();
            var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

            var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
            rides.Add(ride.RideId);

            var offer = await fleet.WaitForOfferAsync(ride.RideId);
            var version = (await fleet.ReadRideAsync(ride.RideId)).Version;

            using (var won = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, version))
            {
                Assert.Equal(HttpStatusCode.OK, won.StatusCode);
            }

            // A fresh Idempotency-Key, so this is not the kernel's replay answering — it is the
            // service, off the row.
            using (var again = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, version))
            {
                Assert.Equal(HttpStatusCode.OK, again.StatusCode);
                Assert.Equal("Accepted", (await ModeCFleet.ReadJsonAsync(again)).GetProperty("state").GetString());
            }

            Assert.Equal(1, await CountTransitionsAsync(fleet, ride.RideId, "Accepted"));
            Assert.Equal(1, await CountAsync(fleet, ride.RideId, "ride.accepted"));
        });

    // -----------------------------------------------------------------------------------------

    private static async Task<int> CountAsync(ModeCFleet fleet, Guid rideId, string eventType)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM rides.outbox
             WHERE aggregate_id = @RideId AND event_type = @EventType;
            """,
            new { RideId = rideId, EventType = eventType });
    }

    private static async Task<int> CountTransitionsAsync(ModeCFleet fleet, Guid rideId, string toState)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM rides.transitions
             WHERE ride_id = @RideId AND to_state = @ToState;
            """,
            new { RideId = rideId, ToState = toState });
    }
}
