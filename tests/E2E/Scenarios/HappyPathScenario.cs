using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>The Mode C ride, from a passenger opening the app to the driver being paid.</b>
/// </summary>
/// <remarks>
/// <para>
/// C120's first deliverable: request → match → offer → accept → arrive → start → complete → pay,
/// across five running services and every one of the four pieces of infrastructure. The only calls
/// this scenario makes are the ones an app makes — a quote, a booking, an accept, three driver taps
/// and a payment. Everything between them is the platform: the outbox dispatcher publishing to
/// Redpanda, dispatch-svc's consumer building candidates and reserving a driver, its call onto
/// ride-svc's internal plane, the R-04 timers being armed and re-planned, fanout-svc projecting the
/// event stream and pushing it down a WebSocket, and fare-svc closing the loop through
/// <c>payment-settled</c>.
/// </para>
/// <para>
/// D1' §A-3, ADD §11.11, D5' §6, R-05.
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class HappyPathScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    [Fact]
    public Task A_booked_ride_is_dispatched_driven_and_paid() => RunAsync(async (fleet, rides) =>
    {
        var (pickup, dropoff) = ModeCFleet.NextPlaces();

        var passenger = await fleet.CreatePassengerAsync();
        var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

        // ---- request -------------------------------------------------------------------------
        // Quoted by the real fare-svc and booked on the strength of the signed token alone: the two
        // services share a key and nothing else, which is the whole crossing (D5' §1.4).
        var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
        rides.Add(ride.RideId);

        // The passenger's live plane. Joining at all is evidence the booking reached fanout-svc
        // over Redpanda — the participant check reads a projection of `ride.events`, so a stranger
        // is refused and somebody who is not yet in it is refused too.
        await using var live = await LiveConnection.OpenAsync(fleet, passenger.Bearer);
        await live.SubscribeAsync(ride.RideId);

        // ---- match, offer --------------------------------------------------------------------
        var offer = await fleet.WaitForOfferAsync(ride.RideId);

        Assert.Equal(driver.DriverId, offer.DriverId);

        var offered = await fleet.ReadRideAsync(ride.RideId);
        Assert.Equal(offer.Id, offered.CurrentOfferId);
        Assert.Equal(driver.DriverId, offered.OfferedDriverId);

        // **The deadline is ride-svc's, everywhere.** dispatch-svc asks for a window; ride-svc
        // stamps the instant, and dispatch realigns its own row and its Redis PEXPIRE to it. Two
        // clocks that disagreed here would let the R-04 backstop take an offer from a driver who
        // was still inside the window to accept.
        Assert.Equal(offered.OfferExpiresAt, offer.ExpiresAt);
        Assert.Equal(
            ModeCFleet.OfferTtl.TotalSeconds, (offer.ExpiresAt - offer.SentAt).TotalSeconds, precision: 0);

        // R-04's durable backstop exists for this offer before anybody is asked to answer it.
        Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "offer_expiry"));

        // ---- accept --------------------------------------------------------------------------
        using (var accepted = await fleet.AcceptAsync(ride.RideId, driver, offer.Id, offered.Version))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var body = await ModeCFleet.ReadJsonAsync(accepted);
            Assert.Equal("Accepted", body.GetProperty("state").GetString());
            ride = ride with { Version = body.GetProperty("version").GetInt64() };
        }

        // The accept crosses back the other way: `ride.accepted` reaches dispatch-svc, which marks
        // the offer taken and holds the driver (R-10). Until that lands the driver is still OFFERED.
        await fleet.UntilAsync(
            ride.RideId,
            async () => await PresenceOfAsync(fleet, driver.DriverId) == "ON_RIDE",
            "dispatch-svc never put the accepted driver ON_RIDE");

        Assert.Equal("ACCEPTED", (await fleet.ReadOfferAsync(ride.RideId))!.Status);

        // The ride's own backstop moved with it: the offer timer is gone and the driver now has the
        // arrival grace to reach the pickup (§11.12's NoShowDriver row).
        Assert.Empty(await fleet.ReadRideTimersAsync(ride.RideId, "offer_expiry"));
        Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "arrival_grace"));

        // ---- arrive, start, complete ---------------------------------------------------------
        ride = await fleet.AdvanceAsync(ride, "arrive");

        // D5' §7: the rider has five minutes from here, and the clock is durable rather than a
        // process's timer.
        var noShow = Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "no_show"));
        Assert.Equal(5, (noShow.FireAt - DateTimeOffset.UtcNow).TotalMinutes, precision: 0);

        ride = await fleet.AdvanceAsync(ride, "start");
        Assert.Equal("InProgress", (await fleet.ReadRideAsync(ride.RideId)).State);

        ride = await fleet.AdvanceAsync(ride, "complete");

        // `Completed` is transient — `complete` moves through it to `PaymentPending` in the same
        // transaction, so the ride never rests there and the audit shows the pair.
        var completed = await fleet.ReadRideAsync(ride.RideId);
        Assert.Equal("PaymentPending", completed.State);

        var transitions = await ReadTransitionsAsync(fleet, ride.RideId);
        Assert.Contains(("InProgress", "Completed"), transitions);
        Assert.Contains(("Completed", "PaymentPending"), transitions);

        // R-20's stuck-payment watch is armed at ADD §13.3.1's ten minutes.
        var payment = Assert.Single(await fleet.ReadRideTimersAsync(ride.RideId, "payment_pending"));
        Assert.Equal(10, (payment.FireAt - DateTimeOffset.UtcNow).TotalMinutes, precision: 0);

        // dispatch-svc releases the driver on `ride.completed`, so the pool gets them back.
        await fleet.UntilAsync(
            ride.RideId,
            async () => await PresenceOfAsync(fleet, driver.DriverId) == "AVAILABLE",
            "dispatch-svc never returned the completed ride's driver to the pool");

        // ---- pay -----------------------------------------------------------------------------
        var fare = await fleet.PriceAsync(ride.RideId, distanceKm: 9.5);

        Assert.NotEqual(Guid.Empty, fare.PaymentId);
        Assert.True(fare.AmountMinor > 0, "a completed ride was priced at nothing");

        // This passenger owes nothing from an earlier ride, so the charge is the trip and only the
        // trip (D5' §7.1's settlement is asserted in CancellationMatrixScenario).
        Assert.Equal(fare.TripMinor, fare.AmountMinor);

        using (var paid = await fleet.PayAsync(ride.RideId, passenger))
        {
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

            var body = await ModeCFleet.ReadJsonAsync(paid);
            Assert.Equal("FellBackToCash", body.GetProperty("state").GetString());

            // AL-57/AL-59: no ride fare reaches an acquirer, so no surviving rail is surcharged.
            Assert.Equal(0, body.GetProperty("surchargeMinor").GetInt64());
        }

        // R-05's one door: fare-svc reports a terminal payment state and only then does the ride
        // leave PaymentPending — and only then does the driver earn.
        var settled = await fleet.WaitForStateAsync(ride.RideId, "CashSettled");
        Assert.NotNull(settled.TerminalAt);

        Assert.Equal((1, fare.AmountMinor), await fleet.ReadEarningsAsync(driver.DriverId));

        // ---- what the rest of the platform was told ------------------------------------------
        var events = await fleet.ReadEventsAsync(ride.RideId);

        // In order, and every one of them keyed by the ride on one topic — so a penalty can never
        // overtake the cancellation that caused it, and a consumer reading the partition sees the
        // ride's life in the order it happened (D6' §2.1).
        Assert.Equal(
            [
                "ride.requested", "offer.created", "ride.accepted", "ride.driver_arrived",
                "ride.started", "ride.completed", "ride.settled",
            ],
            events);

        // Every one of them was published, not merely written: R-13's claim is that the event
        // exists because the transaction committed, and the dispatcher is what carries it.
        await fleet.UntilAsync(
            ride.RideId,
            async () => await UndispatchedAsync(fleet, ride.RideId) == 0,
            "ride-svc left events in the outbox that were never published");

        // …and the passenger watched the whole thing on the socket rather than by polling.
        Assert.True(
            await live.SawAsync("CashSettled"),
            $"the passenger's /hubs/live connection never saw the ride settle; it saw "
            + $"[{string.Join(", ", live.States.Select(frame => frame.State))}]");

        Assert.Contains(live.States, frame => frame.State == "Accepted");
        Assert.Contains(live.States, frame => frame.State == "InProgress");
    });

    /// <summary>
    /// The R-18 recovery reads both parties reopen the app on, answered from the live ride rather
    /// than from anything either app kept.
    /// </summary>
    [Fact]
    public Task Both_apps_can_recover_the_ride_they_are_on_after_a_cold_start() => RunAsync(async (fleet, rides) =>
    {
        var ride = await DriveToAsync(fleet, rides, "InProgress");

        using (var passengerView = await Get(
            fleet, $"/v1/rides/passenger/{ride.Passenger.Id}/active", ride.Passenger.Bearer))
        {
            Assert.Equal(HttpStatusCode.OK, passengerView.StatusCode);

            var body = await ModeCFleet.ReadJsonAsync(passengerView);
            Assert.Equal(ride.RideId, body.GetProperty("rideId").GetGuid());
            Assert.Equal("InProgress", body.GetProperty("state").GetString());

            // The driver card the app draws — name, tier and the registration number a passenger
            // checks before getting in (US-9.6).
            var card = body.GetProperty("driver");
            Assert.Equal(ride.Driver.DriverId, card.GetProperty("driverId").GetGuid());
            Assert.Equal(ride.Driver.Plate, card.GetProperty("registrationNumber").GetString());

            // AL-48: the passenger is given the driver's real number to dial. There is no masking
            // rail on this platform, which is why the field carries a number at all.
            Assert.False(
                string.IsNullOrWhiteSpace(body.GetProperty("counterpartyPhone").GetString()),
                "the recovery read gave the passenger no number to ring their driver on");
        }

        using var driverView = await Get(
            fleet, $"/v1/rides/driver/{ride.Driver.DriverId}/active", ride.Driver.Bearer);

        Assert.Equal(HttpStatusCode.OK, driverView.StatusCode);
        Assert.Equal(ride.RideId, (await ModeCFleet.ReadJsonAsync(driverView)).GetProperty("rideId").GetGuid());
    });

    // -----------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Get(ModeCFleet fleet, string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        return fleet.RideClient.SendAsync(request);
    }

    private static async Task<string?> PresenceOfAsync(ModeCFleet fleet, Guid driverId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<string?>(
            "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { DriverId = driverId });
    }

    private static async Task<int> UndispatchedAsync(ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.outbox WHERE aggregate_id = @RideId AND dispatched_at IS NULL;",
            new { RideId = rideId });
    }

    private static async Task<IReadOnlyList<(string? From, string To)>> ReadTransitionsAsync(
        ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        return [.. await connection.QueryAsync<(string?, string)>(
            "SELECT from_state, to_state FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts, id;",
            new { RideId = rideId })];
    }
}
