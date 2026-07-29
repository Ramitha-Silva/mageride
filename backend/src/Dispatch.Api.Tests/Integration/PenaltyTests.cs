using Dapper;
using System.Net;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Messaging;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// The Rs 50 cross-trip cancellation penalty — accrued here, collected by fare-svc (D-05, AL-16,
/// D5' §7.1, US-6A.9).
/// </summary>
/// <remarks>
/// The debt is stated by ride-svc's §11.12 matrix and reaches this service on
/// <c>cancellation.penalty.accrued</c>. Every test here drives a <b>real</b> cancellation through
/// ride-svc and feeds this service the envelope ride-svc actually wrote to its outbox, so the two
/// halves of the wire format are checked against each other rather than against a fixture.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class PenaltyTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>D-05: cancelling after a driver accepted accrues Rs 50 against the passenger.</summary>
    [Fact]
    public async Task A_cancellation_after_acceptance_accrues_rs_50_to_the_stood_up_driver()
    {
        await using var harness = await StartAsync();

        var (passengerId, rideId, driver) = await AcceptedRideAsync(harness);

        using (var cancelled = await harness.CancelRideAsync(passengerId, rideId))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        await harness.HandleRideEventAsync(
            await harness.ReadRideEventAsync(rideId, RideEventTypes.PenaltyAccrued));

        var penalties = await harness.ReadPenaltiesAsync(passengerId);
        var penalty = Assert.Single(penalties);

        // Rs 50 in minor units, exactly as `Ride:CancellationPenaltyMinor` states it — the amount
        // travels on the event and is not re-derived here.
        Assert.Equal(5_000, penalty.AmountMinor);
        Assert.Equal(PenaltyBases.CancellationFee, penalty.Basis);
        Assert.Equal(PenaltyStatuses.Outstanding, penalty.Status);
        Assert.Null(penalty.AppliedRideId);

        // The debt is owed to the driver who was stood up, not to the platform (AL-16).
        await using var connection = await harness.OpenAsync();

        var affected = await connection.QuerySingleAsync<Guid>(
            "SELECT affected_driver_id FROM dispatch.cancellation_penalties WHERE id = @Id;",
            new { penalty.Id });

        Assert.Equal(driver.DriverId, affected);
    }

    /// <summary>
    /// D6' §2.3 delivery is at-least-once, so the accrual has to be idempotent by construction —
    /// <c>ux_penalty_accrual(original_ride_id, basis)</c>, migration 0713.
    /// </summary>
    [Fact]
    public async Task A_redelivered_accrual_does_not_double_the_debt()
    {
        await using var harness = await StartAsync();

        var (passengerId, rideId, _) = await AcceptedRideAsync(harness);

        using (var cancelled = await harness.CancelRideAsync(passengerId, rideId))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        var envelope = await harness.ReadRideEventAsync(rideId, RideEventTypes.PenaltyAccrued);

        await harness.HandleRideEventAsync(envelope);
        await harness.HandleRideEventAsync(envelope);
        await harness.HandleRideEventAsync(envelope);

        var penalties = await harness.ReadPenaltiesAsync(passengerId);

        Assert.Single(penalties);
        Assert.Equal(5_000, penalties[0].AmountMinor);
    }

    /// <summary>
    /// <b>Definition of Done.</b> A penalty is applied to exactly one ride and never applied twice:
    /// the settlement is conditional on <c>OUTSTANDING</c>, so a retry, a second completed trip and
    /// two fare-svc replicas racing all reach the same total.
    /// </summary>
    [Fact]
    public async Task An_outstanding_penalty_settles_once_against_one_ride()
    {
        await using var harness = await StartAsync();

        var (passengerId, rideId, _) = await AcceptedRideAsync(harness);

        using (var cancelled = await harness.CancelRideAsync(passengerId, rideId))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        await harness.HandleRideEventAsync(
            await harness.ReadRideEventAsync(rideId, RideEventTypes.PenaltyAccrued));

        // fare-svc reads the debt before it prices the next trip…
        using (var outstanding = await harness.InternalAsync(
                   HttpMethod.Get, $"/v1/internal/passengers/{passengerId}/penalties"))
        {
            Assert.Equal(HttpStatusCode.OK, outstanding.StatusCode);

            var body = await DispatchHarness.ReadJsonAsync(outstanding);
            Assert.Equal(5_000, body.GetProperty("totalMinor").GetInt64());
            Assert.Equal("LKR", body.GetProperty("currency").GetString());
            Assert.Single(body.GetProperty("items").EnumerateArray());
        }

        var nextRideId = Guid.NewGuid();

        // …and settles it after posting the ledger entries.
        using (var settled = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/passengers/{passengerId}/penalties/settle",
                   new { rideId = nextRideId.ToString() }))
        {
            Assert.Equal(HttpStatusCode.OK, settled.StatusCode);

            var body = await DispatchHarness.ReadJsonAsync(settled);
            Assert.Equal(5_000, body.GetProperty("settledMinor").GetInt64());
        }

        var stored = Assert.Single(await harness.ReadPenaltiesAsync(passengerId));
        Assert.Equal(PenaltyStatuses.Settled, stored.Status);
        Assert.Equal(nextRideId, stored.AppliedRideId);

        // A retry of the same settlement collects nothing — the row is no longer OUTSTANDING, and
        // an amount reported twice is an amount charged twice.
        using (var replay = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/passengers/{passengerId}/penalties/settle",
                   new { rideId = nextRideId.ToString() }))
        {
            var body = await DispatchHarness.ReadJsonAsync(replay);
            Assert.Equal(0, body.GetProperty("settledMinor").GetInt64());
            Assert.Empty(body.GetProperty("items").EnumerateArray());
        }

        // And a *different* later trip cannot re-collect it either: `applied_ride_id` still names
        // the first one.
        using (var laterTrip = await harness.InternalAsync(
                   HttpMethod.Post, $"/v1/internal/passengers/{passengerId}/penalties/settle",
                   new { rideId = Guid.NewGuid().ToString() }))
        {
            var body = await DispatchHarness.ReadJsonAsync(laterTrip);
            Assert.Equal(0, body.GetProperty("settledMinor").GetInt64());
        }

        var final = Assert.Single(await harness.ReadPenaltiesAsync(passengerId));
        Assert.Equal(nextRideId, final.AppliedRideId);
    }

    /// <summary>
    /// US-6A.9: cancelling <em>before</em> a driver accepted costs nothing, so there is no row to
    /// settle and the ledger stays empty.
    /// </summary>
    [Fact]
    public async Task A_cancellation_before_acceptance_accrues_nothing()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();
        var rideId = await harness.RequestRideAsync(passengerId);

        using (var cancelled = await harness.CancelRideAsync(passengerId, rideId))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        await using var connection = await harness.OpenAsync();

        var events = await connection.QueryAsync<string>(
            "SELECT event_type FROM rides.outbox WHERE aggregate_id = @RideId;",
            new { RideId = rideId });

        Assert.DoesNotContain(RideEventTypes.PenaltyAccrued, events);
        Assert.Empty(await harness.ReadPenaltiesAsync(passengerId));
    }

    [Fact]
    public async Task The_internal_penalty_routes_are_unreachable_without_the_key()
    {
        await using var harness = await StartAsync();

        var passengerId = await harness.CreatePassengerAsync();

        using var response = await harness.InternalAsync(
            HttpMethod.Get, $"/v1/internal/passengers/{passengerId}/penalties", apiKey: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A real ride, offered by the real dispatch loop and accepted through ride-svc's real route —
    /// the only state §11.12 accrues the Rs 50 from.
    /// </summary>
    private static async Task<(Guid PassengerId, Guid RideId, SeededDriver Driver)> AcceptedRideAsync(
        DispatchHarness harness)
    {
        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var passengerId = await harness.CreatePassengerAsync();
        var rideId = await harness.RequestRideAsync(passengerId);

        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(driver.DriverId, outcome.DriverId);

        await harness.AcceptOfferAsync(driver, rideId, outcome.OfferId!.Value, outcome.Version!.Value);

        return (passengerId, rideId, driver);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}
