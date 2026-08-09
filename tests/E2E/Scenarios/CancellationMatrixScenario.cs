using System.Net;
using Dapper;
using MageRide.E2E.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// <b>Every cell of the D5' §7 / ADD §11.12 cancellation and no-show matrix, end to end.</b>
/// </summary>
/// <remarks>
/// <para>
/// One test case per cell, and the cells come from <see cref="RideCancellationMatrix.All"/> rather
/// than from a list written here — so a row added to the matrix is a test case that appears on its
/// own, and <see cref="MatrixCoverage"/> fails if a cell is neither driven here nor recorded as
/// unreachable with a reason.
/// </para>
/// <para>
/// <b>What each case actually does.</b> It books a ride, lets the real dispatch loop carry it to
/// the cell's state, and then makes the thing happen that the trigger names — a passenger tapping
/// Cancel, a driver tapping Cancel, an operator calling <c>system-cancel</c>, a vehicle's EMQX
/// session ending on its last will, or a durable timer coming due. Nothing calls
/// <c>IRideCancellationService</c> and nothing writes a state. What is asserted is the whole of the
/// row: the terminal state, the audit row's reason and actor, the events the "Events emitted"
/// column names, and the money — which is accrued and never collected here (D5' §7.1).
/// </para>
/// </remarks>
[Collection<ModeCCollection>]
[Trait("Category", "ModeC")]
public sealed class CancellationMatrixScenario(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx)
    : ModeCScenario(postgres, redis, redpanda, emqx)
{
    public static TheoryData<string, RideCancellationTrigger> Cells
    {
        get
        {
            var cells = new TheoryData<string, RideCancellationTrigger>();

            foreach (var (state, trigger, _) in MatrixCoverage.Reachable)
            {
                cells.Add(state, trigger);
            }

            return cells;
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public Task The_matrix_decides_what_a_cancellation_means(string state, RideCancellationTrigger trigger) =>
        RunAsync(async (fleet, rides) =>
        {
            Assert.True(
                RideCancellationMatrix.TryResolve(state, trigger, out var expected),
                $"{state} × {trigger} is not a cell of the matrix.");

            var ride = await ReachAndApplyAsync(fleet, rides, state, trigger);

            // ---- the terminal, and the audit row that says how it got there --------------------
            var terminated = await fleet.WaitForStateAsync(ride.RideId, expected.ToState);
            Assert.NotNull(terminated.TerminalAt);

            var last = (await ReadTransitionsAsync(fleet, ride.RideId))[^1];

            Assert.Equal(state, last.From);
            Assert.Equal(expected.ToState, last.To);
            Assert.Equal(expected.ReasonCode, last.Reason);
            Assert.Equal(expected.ActorType, last.Actor);

            // ---- the Events emitted column ----------------------------------------------------
            var events = await fleet.ReadEventsAsync(ride.RideId);
            Assert.Contains(expected.EventType, events);

            // ---- the Penalty column -----------------------------------------------------------
            // Accrued, never collected: ride-svc states that money is owed and to whom, and
            // fare-svc settles it against the passenger's next completed trip.
            if (expected.Penalty is RidePenaltyBasis.None)
            {
                Assert.DoesNotContain("cancellation.penalty.accrued", events);
            }
            else
            {
                var accrued = (await fleet.ReadEventPayloadAsync(ride.RideId, "cancellation.penalty.accrued"))
                    .GetProperty("payload");

                Assert.Equal(BasisName(expected.Penalty), accrued.GetProperty("basis").GetString());
                Assert.Equal("next-trip", accrued.GetProperty("settledOn").GetString());
                Assert.Equal("LKR", accrued.GetProperty("currency").GetString());

                // D-05 is Rs 50 and §11.12's no-show fee is Rs 100, both in minor units. A full-fare
                // cancellation travels as the *quote* with `basis: full_fare`, which is what tells
                // fare-svc to re-meter it — so only its floor is assertable here.
                var amountMinor = accrued.GetProperty("amountMinor").GetInt64();

                switch (expected.Penalty)
                {
                    case RidePenaltyBasis.RiderCancellation:
                        Assert.Equal(5_000, amountMinor);
                        break;
                    case RidePenaltyBasis.RiderNoShow:
                        Assert.Equal(10_000, amountMinor);
                        break;
                    default:
                        Assert.True(amountMinor > 0, "a full-fare cancellation accrued nothing");
                        break;
                }
            }

            // ---- the Reputation column --------------------------------------------------------
            if (expected.ReputationHit)
            {
                Assert.Contains("reputation.driver_cancelled", events);
            }
            else
            {
                Assert.DoesNotContain("reputation.driver_cancelled", events);
            }

            // ---- and every clock ride-svc was holding is retired --------------------------------
            // A terminal state has nothing left to take away, so a no-show or offline grace that
            // survived it would fire against a ride that ended an hour ago.
            //
            // `offer_expiry` is exempt because it is *not one of ride-svc's*: dispatch-svc writes
            // that kind into `rides.timers` (ADD §6 gives it the offer backstop) and settles it from
            // its own sweep, so a ride terminated out of `Offered` legitimately still carries one
            // until dispatch's backstop reaches it and is answered 410.
            var live = await fleet.ReadRideTimersAsync(ride.RideId);

            Assert.DoesNotContain(live, timer => RideTimerKinds.Owned.Contains(timer.Kind));
        });

    /// <summary>
    /// <b>D-05, across two rides and three services.</b> The Rs 50 a passenger owes for cancelling
    /// after acceptance is collected on the fare of their next completed trip.
    /// </summary>
    /// <remarks>
    /// The longest chain in the platform, and none of it is a call this scenario makes: ride-svc
    /// accrues the penalty onto <c>ride.events</c>, dispatch-svc's consumer turns it into a
    /// <c>dispatch.cancellation_penalties</c> row, and when the same passenger's next ride is priced
    /// fare-svc settles the debt over dispatch-svc's internal plane and adds it to the fare. D5'
    /// §7.1, US-6A.9, C035 decision 9.
    /// </remarks>
    [Fact]
    public Task An_accrued_cancellation_fee_is_collected_on_the_passengers_next_trip() =>
        RunAsync(async (fleet, rides) =>
        {
            var abandoned = await DriveToAsync(fleet, rides, "Accepted");

            using (var cancelled = await fleet.CancelAsync(
                abandoned.RideId, abandoned.Passenger.Bearer, "RIDER_CHANGED_MIND"))
            {
                Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

                var penalty = (await ModeCFleet.ReadJsonAsync(cancelled)).GetProperty("penalty");
                Assert.Equal(5_000, penalty.GetProperty("amountMinor").GetInt64());
                Assert.Equal("next-trip", penalty.GetProperty("settledOn").GetString());
            }

            // dispatch-svc records the debt. No ledger entry anywhere — D-09 keeps the money in
            // billing, and this table is the passenger's outstanding balance, nothing more.
            await fleet.UntilAsync(
                abandoned.RideId,
                async () => (await fleet.ReadPenaltiesAsync(abandoned.Passenger.Id)).Count == 1,
                "dispatch-svc never recorded the accrued cancellation fee");

            var outstanding = Assert.Single(await fleet.ReadPenaltiesAsync(abandoned.Passenger.Id));

            Assert.Equal(5_000, outstanding.AmountMinor);
            Assert.Equal("cancellation_fee", outstanding.Basis);
            Assert.Equal("OUTSTANDING", outstanding.Status);
            Assert.Equal(abandoned.RideId, outstanding.OriginalRideId);
            Assert.Null(outstanding.AppliedRideId);

            // The same passenger books again and this time completes the trip.
            var (pickup, dropoff) = ModeCFleet.NextPlaces();
            var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

            var next = await fleet.BookAsync(abandoned.Passenger, driver, pickup, dropoff);
            rides.Add(next.RideId);

            var offer = await fleet.WaitForOfferAsync(next.RideId);

            using (var accepted = await fleet.AcceptAsync(
                next.RideId, driver, offer.Id, (await fleet.ReadRideAsync(next.RideId)).Version))
            {
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                next = next with { Version = (await ModeCFleet.ReadJsonAsync(accepted)).GetProperty("version").GetInt64() };
            }

            next = await fleet.AdvanceAsync(next, "arrive");
            next = await fleet.AdvanceAsync(next, "start");
            next = await fleet.AdvanceAsync(next, "complete");

            // Pricing the completed ride is what settles the debt, and it settles *first* so a
            // retry collects nothing rather than charging the same Rs 50 twice.
            var fare = await fleet.PriceAsync(next.RideId, distanceKm: 9.5);

            var settled = Assert.Single(await fleet.ReadPenaltiesAsync(abandoned.Passenger.Id));
            Assert.Equal("SETTLED", settled.Status);
            Assert.Equal(next.RideId, settled.AppliedRideId);

            // …and the passenger paid it: the charge is the metered trip plus the Rs 50 they owed
            // from a ride they never took.
            Assert.Equal(fare.TripMinor + 5_000, fare.AmountMinor);

            // A retry collects nothing: the settle statement updates only rows still OUTSTANDING, so
            // the same Rs 50 cannot be charged twice — and this is the one call on the whole path a
            // timeout would make a caller repeat.
            var again = await fleet.PriceAsync(next.RideId, distanceKm: 9.5);

            Assert.Equal(fare.PaymentId, again.PaymentId);
            Assert.Equal(fare.AmountMinor, again.AmountMinor);
            Assert.Single(await fleet.ReadPenaltiesAsync(abandoned.Passenger.Id));
        });

    /// <summary>
    /// <b>AL-16 / US-6A.10b.</b> Three consecutive post-acceptance cancellations and the passenger
    /// cannot book again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counter is derived rather than stored (<c>IBookingEligibility</c>): reputation-svc owns
    /// <c>cancellations_continuous</c> and does not compute it yet, so ride-svc counts the run of
    /// <c>CancelledByRiderAfterAccept</c> at the head of the passenger's own history. Counting the
    /// rides is not a second copy of the counter — the rides *are* the facts it would be computed
    /// from.
    /// </para>
    /// <para>
    /// <b>"Consecutive" is load-bearing and is asserted here too.</b> A completed ride in between
    /// resets the run, which is the difference between a rule about a habit and a rule about a
    /// lifetime.
    /// </para>
    /// </remarks>
    [Fact]
    public Task Three_consecutive_post_acceptance_cancellations_disable_booking() =>
        RunAsync(async (fleet, rides) =>
        {
            var passenger = await fleet.CreatePassengerAsync();

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var ride = await CancelAfterAcceptAsync(fleet, rides, passenger);

                Assert.Equal("CancelledByRiderAfterAccept", (await fleet.ReadRideAsync(ride)).State);
            }

            // The fourth booking is refused. 403 rather than 409: the passenger is not allowed to
            // book at all right now, which is a different answer from "this ride cannot be booked".
            var (pickup, dropoff) = ModeCFleet.NextPlaces();
            var quote = await fleet.QuoteAsync(passenger, pickup, dropoff);

            using var refused = await ModeCFleet.PostAsync(
                fleet.RideClient,
                "/v1/rides/request",
                new
                {
                    clientRequestId = Guid.NewGuid().ToString(),
                    pickup = new { lat = pickup.Latitude, lng = pickup.Longitude },
                    dropoff = new { lat = dropoff.Latitude, lng = dropoff.Longitude },
                    vehicleType = "three_wheeler",
                    fareEstimateToken = quote.Token,
                    paymentMethod = "cash",
                },
                passenger.Bearer);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("booking-disabled", await ModeCFleet.ProblemCodeAsync(refused));
        });

    /// <summary>
    /// The other half of AL-16: two cancellations either side of a completed ride are not three in
    /// a row, and the passenger may still book.
    /// </summary>
    [Fact]
    public Task A_completed_ride_resets_the_consecutive_cancellation_run() =>
        RunAsync(async (fleet, rides) =>
        {
            var passenger = await fleet.CreatePassengerAsync();

            await CancelAfterAcceptAsync(fleet, rides, passenger);
            await CancelAfterAcceptAsync(fleet, rides, passenger);

            // A completed trip in between. It settles to a terminal payment state, because a
            // passenger sitting in PaymentPending cannot book again for an entirely different
            // reason (`ux_rides_open_passenger` does not exempt it).
            var completed = await CompleteOneAsync(fleet, rides, passenger);
            await fleet.PriceAsync(completed, distanceKm: 9.5);

            using (var paid = await fleet.PayAsync(completed, passenger))
            {
                Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
            }

            await fleet.WaitForStateAsync(completed, "CashSettled");

            await CancelAfterAcceptAsync(fleet, rides, passenger);
            await CancelAfterAcceptAsync(fleet, rides, passenger);

            // Four cancellations in this passenger's history and the run at the head is two, so
            // booking is still open.
            var (pickup, dropoff) = ModeCFleet.NextPlaces();
            var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

            var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
            rides.Add(ride.RideId);

            Assert.Equal("Requested", (await ModeCFleet.ReadJsonAsync(
                await Get(fleet, $"/v1/rides/{ride.RideId}/state", passenger.Bearer))).GetProperty("state").GetString());
        });

    // -----------------------------------------------------------------------------------------
    // Reaching a cell, and making its trigger happen
    // -----------------------------------------------------------------------------------------

    private static async Task<LiveRide> ReachAndApplyAsync(
        ModeCFleet fleet, ScenarioRides rides, string state, RideCancellationTrigger trigger)
    {
        // `Requested` is not a state the platform rests in: dispatch-svc's consumer moves a booking
        // to `Matching` within milliseconds of the commit, and there is no configuration that would
        // hold it there without switching the dispatcher off — which is the mock this suite is
        // fenced against. So the cell is *raced*, exactly as a passenger who changes their mind the
        // instant after tapping Book races it, and the attempt is repeated until it is won. Both
        // pre-acceptance states resolve to the same row, so a lost race still ends the ride
        // correctly; what the retry buys is that the cell asserted is the cell named.
        if (state == RideStates.Requested)
        {
            return await RaceRequestedAsync(fleet, rides, trigger);
        }

        var packageSize = trigger is RideCancellationTrigger.CodUncollected ? "S" : null;

        var ride = await DriveToAsync(
            fleet,
            rides,
            state,
            vehicleType: packageSize is null ? "three_wheeler" : "motorbike",
            paymentMethod: packageSize is null ? "cash" : "cod",
            packageSize: packageSize);

        await ApplyAsync(fleet, ride, state, trigger);

        return ride;
    }

    private static async Task ApplyAsync(
        ModeCFleet fleet, LiveRide ride, string state, RideCancellationTrigger trigger)
    {
        switch (trigger)
        {
            // The two client taps. Which row applies is decided from the ride and not from the
            // token's role, so both go to the same route with the same body.
            case RideCancellationTrigger.RiderCancel:
                await OkAsync(await fleet.CancelAsync(ride.RideId, ride.Passenger.Bearer, "RIDER_CHANGED_MIND"));
                break;

            case RideCancellationTrigger.DriverCancel:
                await OkAsync(await fleet.CancelAsync(ride.RideId, ride.Driver.Bearer, "OTHER"));
                break;

            case RideCancellationTrigger.FraudLock:
                await OkAsync(await fleet.SystemCancelAsync(ride.RideId, "fraud_lock"));
                break;

            case RideCancellationTrigger.AdminIntervention:
                await OkAsync(await fleet.SystemCancelAsync(ride.RideId, "admin_intervention"));
                break;

            // R-15/R-16: the vehicle's broker session ends and EMQX publishes its will. What that
            // starts is a clock, and only the clock running out reaches the matrix.
            case RideCancellationTrigger.DriverOfflineGraceExpired:
                await GoDarkAsync(fleet, ride, state);
                break;

            // The three durable backstops. Each was armed by the transition into this state, so
            // nothing is armed here — the deadline is only brought forward (see
            // ModeCFleet.PullForwardRideTimerAsync for why that is a clock and not a state fix).
            case RideCancellationTrigger.RiderNoShow:
                await fleet.AssertTimerArmedAsync(ride.RideId, "no_show", TimeSpan.FromMinutes(5));
                await fleet.PullForwardRideTimerAsync(ride.RideId, "no_show");
                break;

            case RideCancellationTrigger.DriverNoShow:
                await fleet.AssertTimerArmedAsync(ride.RideId, "arrival_grace", TimeSpan.FromMinutes(15));
                await fleet.PullForwardRideTimerAsync(ride.RideId, "arrival_grace");
                break;

            case RideCancellationTrigger.CodUncollected:
                // P-14's clock is armed at the *pickup* of the parcel, not at the delivery, and
                // survives every lifecycle move in between — so by PaymentPending it has already
                // been running for the length of the delivery.
                await fleet.AssertTimerArmedAsync(ride.RideId, "cod_uncollected", TimeSpan.FromHours(24));
                await fleet.PullForwardRideTimerAsync(ride.RideId, "cod_uncollected");
                break;

            // US-6A.11's global deadline, which is dispatch-svc's timer and dispatch-svc's call
            // onto `system-cancel`. The ride is in Matching with nobody to offer it to.
            case RideCancellationTrigger.NoDriverFound:
                await fleet.UntilAsync(
                    ride.RideId,
                    async () => await HasDispatchTimerAsync(fleet, ride.RideId, "ride_timeout"),
                    "dispatch-svc never armed the 120 s cascade deadline");

                await AssertGlobalDeadlineAsync(fleet, ride.RideId);
                await fleet.PullForwardDispatchTimerAsync(ride.RideId, "ride_timeout");
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(trigger), trigger, "No end-to-end path is defined for this trigger.");
        }
    }

    /// <summary>
    /// Ends the vehicle's session on the broker and waits for ride-svc to arm the R-16 window the
    /// ride's state calls for, then brings it due.
    /// </summary>
    private static async Task GoDarkAsync(ModeCFleet fleet, LiveRide ride, string state)
    {
        await fleet.WaitForPresenceSubscriptionAsync();

        await using (var device = await DeviceSession.ConnectAsync(fleet.Broker, ride.Driver.VehicleId))
        {
            await device.DropAsync();
        }

        await fleet.UntilAsync(
            ride.RideId,
            async () => (await fleet.ReadRideTimersAsync(ride.RideId, "offline_grace")).Count == 1,
            $"the R-15 last will never armed an offline grace on a ride in {state}");

        // R-16's four windows, verbatim: 60 s after accept, 120 s after arrive, 5 min in progress,
        // 10 min at payment. They are the reason a last will is not a cancellation — a driver in an
        // underpass has not abandoned anybody — so the window is asserted before it is shortened.
        await fleet.AssertTimerArmedAsync(ride.RideId, "offline_grace", RideGracePolicy.For(state)!.Value);
        await fleet.PullForwardRideTimerAsync(ride.RideId, "offline_grace");
    }

    /// <summary>
    /// Books and cancels in the same breath until the cancel lands while the ride is still
    /// <c>Requested</c>.
    /// </summary>
    private static async Task<LiveRide> RaceRequestedAsync(
        ModeCFleet fleet, ScenarioRides rides, RideCancellationTrigger trigger)
    {
        const int Attempts = 8;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var (pickup, dropoff) = ModeCFleet.NextPlaces();

            var passenger = await fleet.CreatePassengerAsync();

            // No driver on standby: a candidate build this booking could win would be work between
            // the 202 and the cancel, and the cell has nothing to do with whether anybody was near.
            var driver = await fleet.CreateDriverAsync();

            var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
            rides.Add(ride.RideId);

            using var response = trigger switch
            {
                RideCancellationTrigger.RiderCancel =>
                    await fleet.CancelAsync(ride.RideId, passenger.Bearer, "RIDER_CHANGED_MIND", ride.Version),
                RideCancellationTrigger.FraudLock => await fleet.SystemCancelAsync(ride.RideId, "fraud_lock"),
                RideCancellationTrigger.AdminIntervention =>
                    await fleet.SystemCancelAsync(ride.RideId, "admin_intervention"),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(trigger), trigger, "No trigger other than the three client-and-operator ones can "
                    + "reach a ride that dispatch-svc has not begun to match."),
            };

            // A lost race is a 409 (the version moved with the ride into Matching) or a terminal
            // from Matching. Either way this attempt is spent; try again with fresh actors.
            if (response.StatusCode == HttpStatusCode.OK
                && (await ReadTransitionsAsync(fleet, ride.RideId))[^1].From == RideStates.Requested)
            {
                return ride;
            }

            // Leave nothing behind: a ride still in Requested or Matching would sit in the
            // dispatcher's pool for the rest of the run.
            if (!RideStates.IsTerminal((await fleet.ReadRideAsync(ride.RideId)).State))
            {
                using var swept = await fleet.SystemCancelAsync(ride.RideId, "admin_intervention");
            }
        }

        Assert.Fail(
            $"dispatch-svc consumed the booking before the {trigger} could be applied, {Attempts} times running. "
            + "That is not a flake at this rate — either the outbox dispatcher has become synchronous with the "
            + "booking, or something now marks a ride Matching inside the request that created it.");

        return null!;
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>One ride, taken to Accepted and cancelled by the passenger — one AL-16 tally mark.</summary>
    private static async Task<Guid> CancelAfterAcceptAsync(
        ModeCFleet fleet, ScenarioRides rides, Passenger passenger)
    {
        var (pickup, dropoff) = ModeCFleet.NextPlaces();
        var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

        var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
        rides.Add(ride.RideId);

        var offer = await fleet.WaitForOfferAsync(ride.RideId);

        using (var accepted = await fleet.AcceptAsync(
            ride.RideId, driver, offer.Id, (await fleet.ReadRideAsync(ride.RideId)).Version))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await OkAsync(await fleet.CancelAsync(ride.RideId, passenger.Bearer, "RIDER_CHANGED_MIND"));

        return ride.RideId;
    }

    /// <summary>One ride, driven all the way to PaymentPending.</summary>
    private static async Task<Guid> CompleteOneAsync(ModeCFleet fleet, ScenarioRides rides, Passenger passenger)
    {
        var (pickup, dropoff) = ModeCFleet.NextPlaces();
        var driver = await fleet.CreateOnlineDriverAsync(Near(pickup));

        var ride = await fleet.BookAsync(passenger, driver, pickup, dropoff);
        rides.Add(ride.RideId);

        var offer = await fleet.WaitForOfferAsync(ride.RideId);

        using (var accepted = await fleet.AcceptAsync(
            ride.RideId, driver, offer.Id, (await fleet.ReadRideAsync(ride.RideId)).Version))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            ride = ride with { Version = (await ModeCFleet.ReadJsonAsync(accepted)).GetProperty("version").GetInt64() };
        }

        ride = await fleet.AdvanceAsync(ride, "arrive");
        ride = await fleet.AdvanceAsync(ride, "start");
        await fleet.AdvanceAsync(ride, "complete");

        return ride.RideId;
    }

    private static async Task AssertGlobalDeadlineAsync(ModeCFleet fleet, Guid rideId)
    {
        await using var connection = await fleet.OpenAsync();

        var (fireAt, requestedAt) = await connection.QuerySingleAsync<(DateTimeOffset, DateTimeOffset)>(
            """
            SELECT t.fire_at, r.created_at
              FROM dispatch.timers t JOIN rides.rides r ON r.id = t.ride_id
             WHERE t.ride_id = @RideId AND t.kind = 'ride_timeout' AND t.fired_at IS NULL;
            """,
            new { RideId = rideId });

        // US-6A.11: "no driver found within 120 s". ADD §11.12 says 60 s for the same thing and the
        // C034 handoff records the conflict; D5' §3.5 and the URD both say 120 and dispatch-svc
        // runs 120.
        Assert.Equal(
            ModeCFleet.GlobalDispatchTimeout.TotalSeconds, (fireAt - requestedAt).TotalSeconds, tolerance: 10);
    }

    private static async Task<bool> HasDispatchTimerAsync(ModeCFleet fleet, Guid rideId, string kind)
    {
        await using var connection = await fleet.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM dispatch.timers
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = kind }) == 1;
    }

    private static async Task OkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Assert.Fail($"applying the trigger answered {(int)response.StatusCode}: "
                    + await response.Content.ReadAsStringAsync());
            }
        }
    }

    private static Task<HttpResponseMessage> Get(ModeCFleet fleet, string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        return fleet.RideClient.SendAsync(request);
    }

    private static string BasisName(RidePenaltyBasis basis) => basis switch
    {
        RidePenaltyBasis.RiderCancellation => "cancellation_fee",
        RidePenaltyBasis.RiderNoShow => "no_show_fee",
        RidePenaltyBasis.FullFare => "full_fare",
        _ => "none",
    };

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
