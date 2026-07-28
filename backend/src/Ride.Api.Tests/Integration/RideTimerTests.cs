using Dapper;
using MageRide.Ride.Domain;
using MageRide.Ride.Mqtt;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// <b>DoD 3 — "a Redis flush does not lose an offer expiry — the durable backstop still fires
/// (R-04)"</b>, and the R-15/R-16 grace windows that hang off it.
/// </summary>
/// <remarks>
/// <para>
/// ride-svc holds <b>no Redis at all</b> (<c>UseRedis = false</c>), so its half of R-04 is not
/// "the backstop survives a flush" but "there is nothing to flush": every timer is a
/// <c>rides.timers</c> row and every fire is a Postgres claim. That is asserted directly below,
/// along with the stronger property a flush test is a proxy for —
/// <see cref="A_backstop_outlives_the_process_that_armed_it"/> kills the whole service and starts a
/// new one, which no cache could survive either.
/// </para>
/// <para>
/// The <c>offer_expiry</c> kind is dispatch-svc's (ADD §6, C023) and its Redis-flush test lives in
/// <c>Dispatch.Api.Tests.OfferExpiryTests</c>; the split is argued on <c>RideTimerKinds</c>.
/// </para>
/// </remarks>
[Collection<RideCollection>]
public sealed class RideTimerTests(PostgresFixture postgres)
{
    // -------------------------------------------------------------------------------------------
    // The lifecycle plan: what each state arms, and what it retires
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Accepting arms the arrival grace. Without it a driver could accept a ride and simply drive
    /// away, and nothing would ever notice (§11.12's NoShowDriver row).
    /// </summary>
    [Fact]
    public async Task Accepting_arms_the_arrival_grace()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        var timer = await SingleUnfiredAsync(harness, ride.RideId);

        Assert.Equal(RideTimerKinds.ArrivalGrace, timer.Kind);

        // Ride:ArrivalGrace, 15 minutes by default. Asserted as a window rather than an instant:
        // the deadline is stamped from the service's clock inside the accept's transaction.
        Assert.InRange(
            timer.FireAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    /// <summary>
    /// Arriving replaces it with the rider's five minutes (D5' §7). The old one is retired in the
    /// same transaction: a ride carrying both would be a no-show for whichever fired first.
    /// </summary>
    [Fact]
    public async Task Arriving_replaces_the_arrival_grace_with_the_rider_no_show_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("DriverArrived");
        var timer = await SingleUnfiredAsync(harness, ride.RideId);

        Assert.Equal(RideTimerKinds.NoShow, timer.Kind);
        Assert.InRange(
            timer.FireAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    /// <summary>The rider got in. Nothing is waiting for them any more.</summary>
    [Fact]
    public async Task Starting_the_trip_retires_the_no_show_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("InProgress");

        Assert.Empty(await UnfiredAsync(harness, ride.RideId));
    }

    /// <summary>
    /// Completing arms the R-20 payment watch, and firing it <b>moves nothing</b>: no row of the
    /// §11.12 matrix takes a ride out of PaymentPending on a timeout, and R-05 reserves that door
    /// for fare-svc. What the timer produces is the ADD §13.3.1 alert with a ride id on it.
    /// </summary>
    [Fact]
    public async Task The_payment_watch_reports_a_stuck_ride_without_moving_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("PaymentPending");
        var timer = await SingleUnfiredAsync(harness, ride.RideId);

        Assert.Equal(RideTimerKinds.PaymentPending, timer.Kind);
        Assert.InRange(
            timer.FireAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        await ForceDueAsync(harness, ride.RideId);
        var sweep = await harness.SweepTimersAsync();

        Assert.Equal(1, sweep.Claimed);
        Assert.Equal(0, sweep.Applied);

        // Still awaiting payment, and no event claiming otherwise.
        Assert.Equal("PaymentPending", (await harness.ReadRideAsync(ride.RideId)).State);
        Assert.DoesNotContain("ride.cancelled", await harness.ReadEventsAsync(ride.RideId));

        // Retired all the same, so it alerts once rather than on every sweep.
        Assert.Empty(await UnfiredAsync(harness, ride.RideId));
    }

    /// <summary>A terminal ride is watched by nothing.</summary>
    [Fact]
    public async Task A_terminal_ride_retires_every_timer_it_had()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("DriverArrived");

        // Two live timers at once: the rider's no-show window and an offline grace.
        await OfflineAsync(harness, ride.Driver.VehicleId);
        Assert.Equal(2, (await UnfiredAsync(harness, ride.RideId)).Count);

        var cancelled = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "RIDER_CHANGED_MIND" },
            ride.PassengerBearer);

        Assert.Equal(System.Net.HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Empty(await UnfiredAsync(harness, ride.RideId));
    }

    // -------------------------------------------------------------------------------------------
    // R-04 durability
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>R-04, as this service can state it.</b> The process that armed the backstop is stopped
    /// and a new one started against the same database; the ride still reaches its terminal. A
    /// cache could not survive that, which is why ride-svc keeps none.
    /// </summary>
    [Fact]
    public async Task A_backstop_outlives_the_process_that_armed_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        Guid rideId;

        await using (var armed = await RideHarness.StartAsync(postgres))
        {
            var ride = await armed.DriveToAsync("DriverArrived");
            rideId = ride.RideId;

            Assert.Equal(RideTimerKinds.NoShow, (await SingleUnfiredAsync(armed, rideId)).Kind);
            await ForceDueAsync(armed, rideId);
        }

        // Everything the first process knew is gone. Only the rides.timers row is left.
        await using var restarted = await RideHarness.StartAsync(postgres);

        await restarted.SweepTimersAsync();

        Assert.Equal("NoShowRider", (await restarted.ReadRideAsync(rideId)).State);
        Assert.Contains("ride.no_show_rider", await restarted.ReadEventsAsync(rideId));
    }

    /// <summary>
    /// There is no Redis in this service to lose a timer to. Stated as an assertion rather than a
    /// comment because <c>UseRedis</c> is one line in the composition root and turning it on would
    /// silently make the claim false.
    /// </summary>
    [Fact]
    public async Task No_ride_timer_can_be_lost_to_a_cache_because_there_is_no_cache()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var multiplexer = Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis");
        Assert.NotNull(multiplexer);
        Assert.Null(harness.Services.GetService(multiplexer));
    }

    /// <summary>
    /// A worker that dies between claiming and firing must not take the ride's only backstop with
    /// it: the claim is a lease, so the row becomes due again rather than being marked run.
    /// </summary>
    [Fact]
    public async Task A_claimed_timer_is_leased_not_consumed()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Ride:TimerLease"] = "00:01:00" });

        var ride = await harness.DriveToAsync("Accepted");
        await ForceDueAsync(harness, ride.RideId);

        await using var connection = await harness.OpenAsync();

        var timers = harness.Services.GetRequiredService<MageRide.Ride.Persistence.IRideTimerRepository>();

        var claimed = await timers.ClaimDueAsync(
            connection, null, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Single(claimed);

        // Unfired and pushed out by the lease — not deleted, not marked run.
        var row = await SingleUnfiredAsync(harness, ride.RideId);
        Assert.True(row.FireAt > DateTimeOffset.UtcNow);

        // And invisible to a second worker while the lease runs, so two replicas cannot both
        // cancel the same ride.
        Assert.Empty(await timers.ClaimDueAsync(
            connection, null, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A timer that fires and finds the thing it was watching for no longer possible is a normal
    /// race, not a fault: the driver arrived before the arrival grace ran out.
    /// </summary>
    [Fact]
    public async Task A_timer_the_ride_has_outgrown_does_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        // Force the arrival grace due, then let the driver arrive before the sweep runs. The row is
        // retired by the arrival itself, which is the whole point of re-planning on transition.
        await ForceDueAsync(harness, ride.RideId);
        await harness.AdvanceAsync(ride, "arrive");

        var sweep = await harness.SweepTimersAsync();

        Assert.Equal(0, sweep.Applied);
        Assert.Equal("DriverArrived", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    // -------------------------------------------------------------------------------------------
    // R-15 / R-16 — the last-will graces
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// R-16's four windows, applied to the state the ride is actually in. The 60 s and the 120 s
    /// are the difference between a driver who has not set off and one already waiting at the door.
    /// </summary>
    [Theory]
    [InlineData("Accepted", 60)]
    [InlineData("DriverArrived", 120)]
    [InlineData("InProgress", 300)]
    [InlineData("PaymentPending", 600)]
    public async Task An_offline_driver_gets_the_R16_window_for_the_rides_state(string state, int seconds)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync(state);
        Assert.True(await OfflineAsync(harness, ride.Driver.VehicleId));

        var grace = (await UnfiredAsync(harness, ride.RideId))
            .Single(timer => timer.Kind == RideTimerKinds.OfflineGrace);

        var window = grace.FireAt - DateTimeOffset.UtcNow;
        Assert.InRange(window, TimeSpan.FromSeconds(seconds - 15), TimeSpan.FromSeconds(seconds + 5));
    }

    /// <summary>
    /// A last will on a vehicle carrying nobody arms nothing. Most last wills are a driver going
    /// off shift, and a grace on every one of them would be a timer per driver per day.
    /// </summary>
    [Fact]
    public async Task A_vehicle_with_no_live_ride_arms_no_grace()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var idle = await harness.CreateDriverAsync();

        Assert.False(await OfflineAsync(harness, idle.VehicleId));
    }

    /// <summary>
    /// Redelivery must not push the deadline forward. EMQX retains the last will and redelivers it
    /// to every replica and again on reconnect; a broker retrying must not forgive a driver who
    /// never came back.
    /// </summary>
    [Fact]
    public async Task A_redelivered_last_will_does_not_restart_the_clock()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        Assert.True(await OfflineAsync(harness, ride.Driver.VehicleId));
        var first = (await UnfiredAsync(harness, ride.RideId)).Single(t => t.Kind == RideTimerKinds.OfflineGrace);

        // Two more deliveries of the same retained message.
        Assert.False(await OfflineAsync(harness, ride.Driver.VehicleId));
        Assert.False(await OfflineAsync(harness, ride.Driver.VehicleId));

        var after = (await UnfiredAsync(harness, ride.RideId)).Where(t => t.Kind == RideTimerKinds.OfflineGrace);

        Assert.Equal(first.FireAt, Assert.Single(after).FireAt);
    }

    /// <summary>The driver came back inside the window. Nothing is taken away.</summary>
    [Fact]
    public async Task A_vehicle_that_comes_back_keeps_its_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");

        await OfflineAsync(harness, ride.Driver.VehicleId);
        Assert.True(await OnlineAsync(harness, ride.Driver.VehicleId));

        // Even a sweep that runs after the deadline would have passed finds nothing armed.
        await harness.SweepTimersAsync();

        Assert.Equal("Accepted", (await harness.ReadRideAsync(ride.RideId)).State);
        Assert.DoesNotContain(await UnfiredAsync(harness, ride.RideId), t => t.Kind == RideTimerKinds.OfflineGrace);
    }

    /// <summary>
    /// The driver stayed away. §11.12: <c>Accepted | Driver MQTT LWT → offline &gt; 60 s |
    /// CancelledByDriver (system)</c>.
    /// </summary>
    [Fact]
    public async Task An_offline_driver_past_the_window_loses_the_ride()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        await OfflineAsync(harness, ride.Driver.VehicleId);

        // Only the grace: the 15-minute arrival window is still fourteen minutes away, which is
        // exactly the situation R-16's 60 seconds exists to resolve first.
        await ForceDueAsync(harness, ride.RideId, RideTimerKinds.OfflineGrace);
        var sweep = await harness.SweepTimersAsync();

        Assert.Equal(1, sweep.Applied);
        Assert.Equal("CancelledByDriver", (await harness.ReadRideAsync(ride.RideId)).State);
        Assert.Contains("reputation.driver_cancelled", await harness.ReadEventsAsync(ride.RideId));
    }

    /// <summary>
    /// A ride that moved while its driver was off the air gets the new state's window, computed
    /// from the <em>same</em> instant the vehicle went away — so moving the ride along is not a way
    /// to earn a fresh grace, and the re-plan cannot loop.
    /// </summary>
    [Fact]
    public async Task A_grace_is_re_planned_when_the_ride_moves_beneath_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres);

        var ride = await harness.DriveToAsync("Accepted");
        await OfflineAsync(harness, ride.Driver.VehicleId);

        // The driver's app reaches the server even though the broker session is dead — a phone on
        // a flaky mobile data connection does exactly this.
        var version = await harness.AdvanceAsync(ride, "arrive");
        Assert.True(version > ride.Version);

        await ForceDueAsync(harness, ride.RideId, RideTimerKinds.OfflineGrace);
        var sweep = await harness.SweepTimersAsync();

        // The 60-second window did not apply to a DriverArrived ride, so nothing was cancelled…
        Assert.Equal(0, sweep.Applied);
        Assert.Equal("DriverArrived", (await harness.ReadRideAsync(ride.RideId)).State);

        // …and the grace is still running, now against the 120-second window.
        var replanned = (await UnfiredAsync(harness, ride.RideId))
            .Single(timer => timer.Kind == RideTimerKinds.OfflineGrace);

        Assert.InRange(
            replanned.FireAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(125));

        // And the re-planned window does expire: the driver is still gone.
        await ForceDueAsync(harness, ride.RideId, RideTimerKinds.OfflineGrace);
        Assert.Equal(1, (await harness.SweepTimersAsync()).Applied);
        Assert.Equal("CancelledByDriver", (await harness.ReadRideAsync(ride.RideId)).State);
    }

    // -------------------------------------------------------------------------------------------

    private sealed record TimerRow(Guid Id, string Kind, DateTimeOffset FireAt);

    private static Task<bool> OfflineAsync(RideHarness harness, Guid vehicleId) =>
        WithPresenceAsync(harness, presence => presence.WentOfflineAsync(vehicleId, TestContext.Current.CancellationToken));

    private static Task<bool> OnlineAsync(RideHarness harness, Guid vehicleId) =>
        WithPresenceAsync(harness, presence => presence.CameOnlineAsync(vehicleId, TestContext.Current.CancellationToken));

    /// <summary>
    /// Applies a last will the way the broker subscriber does, minus the broker. The MQTT half is
    /// C024's fixture territory; what R-15/R-16 are about is the window, and the window is here.
    /// </summary>
    private static async Task<bool> WithPresenceAsync(RideHarness harness, Func<IVehiclePresence, Task<bool>> apply)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        return await apply(scope.ServiceProvider.GetRequiredService<IVehiclePresence>());
    }

    private static async Task<IReadOnlyList<TimerRow>> UnfiredAsync(RideHarness harness, Guid rideId)
    {
        await using var connection = await harness.OpenAsync();

        return [.. await connection.QueryAsync<TimerRow>(
            "SELECT id, kind, fire_at AS FireAt FROM rides.timers WHERE ride_id = @RideId AND fired_at IS NULL ORDER BY fire_at;",
            new { RideId = rideId })];
    }

    private static async Task<TimerRow> SingleUnfiredAsync(RideHarness harness, Guid rideId) =>
        Assert.Single(await UnfiredAsync(harness, rideId));

    /// <summary>
    /// Pulls the ride's unfired timers into the past, optionally only one kind.
    /// </summary>
    /// <remarks>
    /// The kind matters. An offline <c>Accepted</c> ride legitimately carries two live timers — a
    /// 60-second grace and a 15-minute arrival window — and in production the grace is due first by
    /// fourteen minutes. Forcing both due at once would test a race that cannot happen and would
    /// assert whichever the sweep's <c>ORDER BY fire_at</c> happened to reach.
    /// </remarks>
    private static async Task ForceDueAsync(RideHarness harness, Guid rideId, string? kind = null)
    {
        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE rides.timers
               SET fire_at = now() - interval '1 second'
             WHERE ride_id = @RideId
               AND fired_at IS NULL
               AND (@Kind::text IS NULL OR kind = @Kind);
            """,
            new { RideId = rideId, Kind = kind });
    }
}
