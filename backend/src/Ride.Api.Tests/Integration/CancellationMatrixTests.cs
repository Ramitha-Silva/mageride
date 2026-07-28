using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <b>DoD 1 — "every cell of the D5 §7 cancellation matrix is covered by a test asserting the
/// resulting state and money effect".</b>
/// </summary>
/// <remarks>
/// Each fact below is one printed row, driven through the real HTTP surface against a real
/// Postgres: the ride is walked into the row's From state, the row's trigger is applied the way
/// production applies it, and the assertions are the row's To state, its Penalty column and its
/// Events column. <c>RideCancellationMatrixTests</c> checks the table in isolation; this checks
/// that the service consults it and that the database ends up where the table says.
/// </remarks>
[Collection<RideCollection>]
public sealed class CancellationMatrixTests(PostgresFixture postgres)
{
    /// <summary>D-05's Rs 50, in minor units.</summary>
    private const long Rs50 = 5_000;

    /// <summary>§11.12's Rs 100 rider no-show fee, in minor units.</summary>
    private const long Rs100 = 10_000;

    /// <summary>The quote every seeded ride carries (<c>RideHarness.IssueFareToken</c>).</summary>
    private const long QuotedFare = 74_000;

    // -------------------------------------------------------------------------------------------
    // Rider cancels
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// | Requested / Matching | Rider taps Cancel | CancelledByRiderBeforeAccept | <b>None</b> |
    /// </summary>
    [Theory]
    [InlineData("Requested")]
    [InlineData("Matching")]
    [InlineData("Offered")]
    public async Task A_rider_cancelling_before_acceptance_pays_nothing(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);
        var body = await CancelAsync(harness, ride, ride.PassengerBearer, "RIDER_CHANGED_MIND");

        Assert.Equal("CancelledByRiderBeforeAccept", body.GetProperty("state").GetString());

        // US-6A.9: "without any penalty". Absent, not zero — a `penalty` object saying 0 would be
        // rendered as a charge by any client that checks for the member rather than the amount.
        Assert.False(body.TryGetProperty("penalty", out _));

        await AssertTerminalAsync(harness, ride.RideId, "CancelledByRiderBeforeAccept");
        Assert.Contains("ride.cancelled", await harness.ReadEventsAsync(ride.RideId));
        Assert.DoesNotContain("cancellation.penalty.accrued", await harness.ReadEventsAsync(ride.RideId));
    }

    /// <summary>
    /// | Accepted | Rider taps Cancel | CancelledByRiderAfterAccept | <b>Rs 50 (D-05)</b> |
    /// </summary>
    [Theory]
    [InlineData("Accepted")]
    [InlineData("DriverArrived")]
    public async Task A_rider_cancelling_after_acceptance_owes_fifty_rupees(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);
        var body = await CancelAsync(harness, ride, ride.PassengerBearer, "RIDER_CHANGED_MIND");

        Assert.Equal("CancelledByRiderAfterAccept", body.GetProperty("state").GetString());

        var penalty = body.GetProperty("penalty");
        Assert.Equal(Rs50, penalty.GetProperty("amountMinor").GetInt64());
        Assert.Equal("LKR", penalty.GetProperty("currency").GetString());

        // D5' §7.1: accrued, not collected. There is no card on file, so the debt travels to the
        // passenger's next trip and the contract says so in a `const`.
        Assert.Equal("next-trip", penalty.GetProperty("settledOn").GetString());

        await AssertTerminalAsync(harness, ride.RideId, "CancelledByRiderAfterAccept");

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.cancelled", events);
        Assert.Contains("cancellation.penalty.accrued", events);

        // The driver whose accepted ride was cancelled is who the Rs 50 is owed to (D5' §7.1) —
        // fare-svc cannot settle it without knowing that, and it is not on the ride.cancelled row.
        var accrued = (await harness.ReadEventPayloadAsync(ride.RideId, "cancellation.penalty.accrued"))
            .GetProperty("payload");

        Assert.Equal(Rs50, accrued.GetProperty("amountMinor").GetInt64());
        Assert.Equal(ride.Driver.DriverId, accrued.GetProperty("affectedDriverId").GetGuid());
        Assert.Equal(ride.PassengerId, accrued.GetProperty("passengerId").GetGuid());
        Assert.Equal("cancellation_fee", accrued.GetProperty("basis").GetString());
    }

    /// <summary>
    /// | InProgress | Rider taps Cancel | CancelledByRiderAfterAccept | <b>full fare to driver</b> |
    /// </summary>
    [Fact]
    public async Task A_rider_cancelling_mid_trip_owes_the_whole_fare()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("InProgress");
        var body = await CancelAsync(harness, ride, ride.PassengerBearer, "EMERGENCY");

        Assert.Equal("CancelledByRiderAfterAccept", body.GetProperty("state").GetString());

        // Not Rs 50: the trip happened. The amount is the quote, which is the only number ride-svc
        // holds — `basis: full_fare` is what tells fare-svc to bill the metered distance instead.
        Assert.Equal(QuotedFare, body.GetProperty("penalty").GetProperty("amountMinor").GetInt64());

        var accrued = (await harness.ReadEventPayloadAsync(ride.RideId, "cancellation.penalty.accrued"))
            .GetProperty("payload");

        Assert.Equal("full_fare", accrued.GetProperty("basis").GetString());
        Assert.Equal("full_fare", accrued.GetProperty("driverCompensationBasis").GetString());
        Assert.Equal("InProgress", accrued.GetProperty("fromState").GetString());

        await AssertTerminalAsync(harness, ride.RideId, "CancelledByRiderAfterAccept");
    }

    // -------------------------------------------------------------------------------------------
    // Driver cancels
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// | Accepted | Driver taps Cancel | CancelledByDriver | Reputation hit | Released |
    /// <c>ride.cancelled</c>, <c>reputation.driver_cancelled</c> |
    /// </summary>
    [Theory]
    [InlineData("Accepted")]
    [InlineData("DriverArrived")]
    [InlineData("InProgress")]
    public async Task A_driver_cancelling_takes_a_reputation_hit_and_the_passenger_pays_nothing(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);
        var body = await CancelAsync(harness, ride, ride.Driver.Bearer, "OTHER");

        Assert.Equal("CancelledByDriver", body.GetProperty("state").GetString());

        // The passenger did nothing wrong, so no penalty and no AL-16 count.
        Assert.False(body.TryGetProperty("penalty", out _));

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.cancelled", events);
        Assert.Contains("reputation.driver_cancelled", events);
        Assert.DoesNotContain("cancellation.penalty.accrued", events);

        var hit = (await harness.ReadEventPayloadAsync(ride.RideId, "reputation.driver_cancelled"))
            .GetProperty("payload");

        Assert.Equal(ride.Driver.DriverId, hit.GetProperty("driverId").GetGuid());
        Assert.Equal(from, hit.GetProperty("fromState").GetString());

        // The driver tapped it themselves — reputation-svc can tell that from a phone that died.
        Assert.False(hit.GetProperty("systemInitiated").GetBoolean());

        await AssertTerminalAsync(harness, ride.RideId, "CancelledByDriver");
    }

    /// <summary>
    /// | Accepted | Driver MQTT LWT → offline &gt; 60 s | CancelledByDriver (system) | same |
    /// | DriverArrived | Driver MQTT LWT → offline &gt; 120 s | CancelledByDriver (system) | same |
    /// </summary>
    [Theory]
    [InlineData("Accepted")]
    [InlineData("DriverArrived")]
    public async Task An_expired_offline_grace_cancels_as_the_driver(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);
        var body = await SystemCancelAsync(harness, ride.RideId, "driver_offline_grace_expired");

        Assert.Equal("CancelledByDriver", body.GetProperty("state").GetString());

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("reputation.driver_cancelled", events);

        var hit = (await harness.ReadEventPayloadAsync(ride.RideId, "reputation.driver_cancelled"))
            .GetProperty("payload");

        // Same effect as a tap ("same" in the matrix), but reputation-svc is told which it was.
        Assert.True(hit.GetProperty("systemInitiated").GetBoolean());

        await AssertTerminalAsync(harness, ride.RideId, "CancelledByDriver", actor: "system");
        await AssertReasonAsync(harness, ride.RideId, "DRIVER_OFFLINE_GRACE_EXPIRED");
    }

    /// <summary>
    /// | InProgress | Driver MQTT LWT → offline &gt; 5 min, GPS not advancing | <b>Disputed</b> |
    /// manual review |
    /// </summary>
    [Fact]
    public async Task An_expired_offline_grace_mid_trip_is_a_dispute_not_a_cancellation()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("InProgress");
        var body = await SystemCancelAsync(harness, ride.RideId, "driver_offline_grace_expired");

        // A trip that was under way when the driver vanished is a question for a human, not a
        // cancellation: somebody may still be in the vehicle.
        Assert.Equal("Disputed", body.GetProperty("state").GetString());

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.disputed", events);

        // No reputation hit until somebody has looked — the matrix routes this to manual review.
        Assert.DoesNotContain("reputation.driver_cancelled", events);
        Assert.DoesNotContain("cancellation.penalty.accrued", events);

        await AssertTerminalAsync(harness, ride.RideId, "Disputed", actor: "system");
    }

    // -------------------------------------------------------------------------------------------
    // No-shows and the dispatch cascade
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// | DriverArrived | Rider no-show after 5 min + 2 SMS | NoShowRider | <b>Rs 100</b> + driver
    /// compensation = base fare/2 |
    /// </summary>
    [Fact]
    public async Task A_rider_who_never_appears_owes_a_hundred_rupees_and_the_driver_is_compensated()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("DriverArrived");

        // The trigger production uses: the `no_show` timer armed when the driver arrived, forced
        // due. RideTimerTests is where the arming and the five minutes are asserted.
        await ForceTimersDueAsync(harness, ride.RideId);
        await harness.SweepTimersAsync();

        Assert.Equal("NoShowRider", (await harness.ReadRideAsync(ride.RideId)).State);

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.no_show_rider", events);
        Assert.Contains("cancellation.penalty.accrued", events);

        var accrued = (await harness.ReadEventPayloadAsync(ride.RideId, "cancellation.penalty.accrued"))
            .GetProperty("payload");

        Assert.Equal(Rs100, accrued.GetProperty("amountMinor").GetInt64());
        Assert.Equal("no_show_fee", accrued.GetProperty("basis").GetString());

        // "driver compensation = base fare/2". The base fare is per tier and lives in
        // fares.tariffs, so the rule travels and fare-svc resolves it into an amount.
        Assert.Equal("base_fare_half", accrued.GetProperty("driverCompensationBasis").GetString());
        Assert.Equal(ride.Driver.DriverId, accrued.GetProperty("affectedDriverId").GetGuid());

        await AssertTerminalAsync(harness, ride.RideId, "NoShowRider", actor: "system");
    }

    /// <summary>
    /// | Accepted/DriverArrived | Driver accepted but never reaches pickup; rider waits, grace
    /// exceeded | NoShowDriver | reputation hit + rider compensation |
    /// </summary>
    [Fact]
    public async Task A_driver_who_never_arrives_leaves_the_ride_as_a_driver_no_show()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        await ForceTimersDueAsync(harness, ride.RideId);
        await harness.SweepTimersAsync();

        Assert.Equal("NoShowDriver", (await harness.ReadRideAsync(ride.RideId)).State);

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.no_show_driver", events);

        // The passenger pays nothing — they waited.
        Assert.DoesNotContain("cancellation.penalty.accrued", events);

        await AssertTerminalAsync(harness, ride.RideId, "NoShowDriver", actor: "system");
        await AssertReasonAsync(harness, ride.RideId, "DRIVER_NO_SHOW");
    }

    /// <summary>
    /// | Matching | no driver 2-min / N rounds | ExpiredNoDriver | None | <c>ride.expired_no_driver</c> |
    /// </summary>
    [Fact]
    public async Task A_ride_no_driver_took_expires_with_no_penalty()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Matching");
        var body = await SystemCancelAsync(harness, ride.RideId, "no_driver_found");

        Assert.Equal("ExpiredNoDriver", body.GetProperty("state").GetString());

        var events = await harness.ReadEventsAsync(ride.RideId);
        Assert.Contains("ride.expired_no_driver", events);
        Assert.DoesNotContain("cancellation.penalty.accrued", events);

        await AssertTerminalAsync(harness, ride.RideId, "ExpiredNoDriver", actor: "system");
    }

    /// <summary>
    /// The cascade can only have run out while it was running. Refusing it elsewhere is what stops
    /// a retrying dispatch worker from expiring a ride a driver is holding an offer on.
    /// </summary>
    [Theory]
    [InlineData("Requested")]
    [InlineData("Offered")]
    [InlineData("Accepted")]
    public async Task No_driver_found_is_refused_anywhere_but_matching(string from)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(from);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{ride.RideId}/system-cancel", new { reason = "no_driver_found" });

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "illegal-transition");
        Assert.Equal(from, (await harness.ReadRideAsync(ride.RideId)).State);
    }

    // -------------------------------------------------------------------------------------------
    // Who may cancel, and from where
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A driver who is not the accepted driver of *this* ride is not a party to it. §11.12 gives
    /// the driver's cancel a reputation hit, and charging one to a stranger's tap would let any
    /// driver end any ride.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_cancel_somebody_elses_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        var intruder = await harness.CreateDriverAsync();

        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "OTHER" },
            intruder.Bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-ride-participant");
        Assert.Equal("Accepted", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>
    /// The version echo applies to cancel like every other mutation (D3' ride-svc header). A stale
    /// client cancelling is the case that matters: the ride may have been accepted since they
    /// looked, which is the difference between free and Rs 50.
    /// </summary>
    [Fact]
    public async Task A_stale_version_cannot_cancel()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version - 1, reason = "RIDER_CHANGED_MIND" },
            ride.PassengerBearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "version-conflict");
        Assert.Equal("Accepted", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>A terminal ride cannot be cancelled twice.</summary>
    [Fact]
    public async Task Cancelling_a_cancelled_ride_is_answered_ride_terminal()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        var first = await CancelAsync(harness, ride, ride.PassengerBearer, "RIDER_CHANGED_MIND");

        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = first.GetProperty("version").GetInt64(), reason = "RIDER_CHANGED_MIND" },
            ride.PassengerBearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "ride-terminal");

        // And exactly one penalty was accrued, not two.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.outbox WHERE aggregate_id = @RideId AND event_type = 'cancellation.penalty.accrued';",
            new { RideId = ride.RideId }));
    }

    /// <summary>
    /// The client's stated reason is recorded and published; the matrix decides the outcome. A
    /// passenger claiming <c>DRIVER_TOO_FAR</c> after acceptance still owes the Rs 50.
    /// </summary>
    [Fact]
    public async Task The_clients_reason_is_published_but_decides_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        var body = await CancelAsync(harness, ride, ride.PassengerBearer, "DRIVER_TOO_FAR");

        Assert.Equal("CancelledByRiderAfterAccept", body.GetProperty("state").GetString());
        Assert.Equal(Rs50, body.GetProperty("penalty").GetProperty("amountMinor").GetInt64());

        var cancelled = (await harness.ReadEventPayloadAsync(ride.RideId, "ride.cancelled")).GetProperty("payload");

        Assert.Equal("DRIVER_TOO_FAR", cancelled.GetProperty("cancellationReason").GetString());
        Assert.Equal("RIDER_CANCELLED_AFTER_ACCEPT", cancelled.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task An_unknown_reason_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Requested");

        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "I_CHANGED_MY_MIND" },
            ride.PassengerBearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal("Requested", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    /// <summary>
    /// A terminal ride holds no live offer. Leaving <c>current_offer_id</c> set would keep ADD
    /// §11.11's accept predicate satisfiable against a cancelled ride.
    /// </summary>
    [Fact]
    public async Task A_cancelled_ride_holds_no_live_offer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Offered");
        await CancelAsync(harness, ride, ride.PassengerBearer, "RIDER_CHANGED_MIND");

        await using var connection = await harness.OpenAsync();

        var row = await connection.QuerySingleAsync<(Guid? OfferId, DateTimeOffset? ExpiresAt, DateTimeOffset? TerminalAt)>(
            "SELECT current_offer_id, offer_expires_at, terminal_at FROM rides.rides WHERE id = @RideId;",
            new { RideId = ride.RideId });

        Assert.Null(row.OfferId);
        Assert.Null(row.ExpiresAt);
        Assert.NotNull(row.TerminalAt);

        // The offer this driver was holding is dead: accepting it now is not a race they can win.
        var accept = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/offer/{ride.Driver.DriverId}/accept",
            new { offerId = ride.OfferId!.Value.ToString(), version = ride.Version },
            ride.Driver.Bearer);

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
    }

    // -------------------------------------------------------------------------------------------

    private static async Task<JsonElement> CancelAsync(
        RideHarness harness, LiveRide ride, string bearer, string reason)
    {
        var response = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel", new { version = ride.Version, reason }, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RideHarness.ReadJsonAsync(response);
    }

    private static async Task<JsonElement> SystemCancelAsync(RideHarness harness, Guid rideId, string reason)
    {
        var response = await harness.PostInternalAsync(
            $"/v1/internal/rides/{rideId}/system-cancel", new { reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RideHarness.ReadJsonAsync(response);
    }

    /// <summary>The row, its audit trail and its timers, after a terminal transition.</summary>
    private static async Task AssertTerminalAsync(
        RideHarness harness, Guid rideId, string state, string? actor = null)
    {
        await using var connection = await harness.OpenAsync();

        var row = await connection.QuerySingleAsync<(string State, DateTimeOffset? TerminalAt)>(
            "SELECT state, terminal_at FROM rides.rides WHERE id = @RideId;", new { RideId = rideId });

        Assert.Equal(state, row.State);
        Assert.NotNull(row.TerminalAt);

        // ADD Appendix B.2 invariant 4: the move is in the audit.
        var last = await connection.QuerySingleAsync<(string ToState, string ActorType)>(
            "SELECT to_state, actor_type FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts DESC, id DESC LIMIT 1;",
            new { RideId = rideId });

        Assert.Equal(state, last.ToState);

        if (actor is not null)
        {
            Assert.Equal(actor, last.ActorType);
        }

        // Nothing is still watching a finished ride.
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM rides.timers WHERE ride_id = @RideId AND fired_at IS NULL;",
            new { RideId = rideId }));
    }

    private static async Task AssertReasonAsync(RideHarness harness, Guid rideId, string reasonCode)
    {
        await using var connection = await harness.OpenAsync();

        Assert.Equal(reasonCode, await connection.ExecuteScalarAsync<string>(
            "SELECT reason_code FROM rides.transitions WHERE ride_id = @RideId ORDER BY ts DESC, id DESC LIMIT 1;",
            new { RideId = rideId }));
    }

    /// <summary>
    /// Pulls the ride's unfired timers into the past. The windows themselves (15 min, 5 min) are
    /// asserted in <c>RideTimerTests</c>; making a suite wait on them would buy nothing.
    /// </summary>
    private static async Task ForceTimersDueAsync(RideHarness harness, Guid rideId)
    {
        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE rides.timers SET fire_at = now() - interval '1 second' WHERE ride_id = @RideId AND fired_at IS NULL;",
            new { RideId = rideId });
    }
}
