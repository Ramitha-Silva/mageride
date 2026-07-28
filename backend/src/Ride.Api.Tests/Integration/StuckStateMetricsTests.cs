using Dapper;
using MageRide.Ride.Domain;
using MageRide.Ride.Observability;
using MageRide.Ride.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Ride.Tests.Integration;

/// <summary>
/// The R-20 stuck-state business SLOs (ADD §13.3.1) and the §13.4 timer-backlog runbook trigger.
/// </summary>
/// <remarks>
/// Every count is asserted as a <em>delta</em> against a baseline taken first: the whole suite
/// shares one database, so a gauge that is global by design will legitimately be counting other
/// tests' rides. What matters is that the rule picks up the ride this test made stuck and ignores
/// the one it made fresh.
/// </remarks>
[Collection<RideCollection>]
public sealed class StuckStateMetricsTests(PostgresFixture postgres)
{
    /// <summary>The gauges are off in every other harness; this suite is what turns them on.</summary>
    private static readonly Dictionary<string, string?> MetricsOn =
        new(StringComparer.OrdinalIgnoreCase) { ["Ride:StuckStateMetricsEnabled"] = "true" };

    /// <summary>ADD §13.3.1, top to bottom — six rows, and the service publishes all six.</summary>
    [Fact]
    public void The_rules_are_the_ADD_stuck_state_table()
    {
        Assert.Equal(
            [
                (RideStates.Matching, TimeSpan.FromSeconds(60)),
                (RideStates.Offered, TimeSpan.FromSeconds(20)),
                (RideStates.Accepted, TimeSpan.FromSeconds(60)),
                (RideStates.DriverArrived, TimeSpan.FromMinutes(10)),
                (RideStates.InProgress, TimeSpan.FromMinutes(5)),
                (RideStates.PaymentPending, TimeSpan.FromMinutes(10)),
            ],
            StuckStateObserver.Rules.Select(rule => (rule.State, rule.Age)).ToArray());
    }

    /// <summary>
    /// A ride that has sat in its state past the window is counted; a ride that has just arrived in
    /// the same state is not. The second half is the point — a gauge that counted every ride would
    /// page on-call for a healthy platform.
    /// </summary>
    [Theory]
    [InlineData("Matching")]
    [InlineData("Offered")]
    [InlineData("Accepted")]
    [InlineData("DriverArrived")]
    [InlineData("InProgress")]
    [InlineData("PaymentPending")]
    public async Task A_ride_past_its_window_is_counted_and_a_fresh_one_is_not(string state)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres, MetricsOn);

        var observer = harness.Services.GetRequiredService<StuckStateObserver>();
        var rule = StuckStateObserver.Rules.Single(candidate => candidate.State == state);

        var baseline = observer.CountStuck(rule);

        // A ride that has only just got here is healthy, however tight the window.
        var fresh = await harness.DriveToAsync(state);
        Assert.Equal(baseline, observer.CountStuck(rule));

        // Age it past the threshold. `updated_at` is when the ride entered the state, which is what
        // ADD §13.3.1 means by "age".
        await AgeAsync(harness, fresh.RideId, rule.Age + TimeSpan.FromSeconds(30));

        Assert.Equal(baseline + 1, observer.CountStuck(rule));
    }

    /// <summary>
    /// ADD §13.4: "<c>rides.timers</c> backlog &gt; 100 fired_at IS NULL AND fire_at &lt;
    /// now()-30s ⇒ scheduler ill". The gauge is what that alert reads, and a timer that is merely
    /// due is not yet a backlog.
    /// </summary>
    [Fact]
    public async Task Overdue_timers_show_up_as_a_backlog()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres, MetricsOn);

        var observer = harness.Services.GetRequiredService<StuckStateObserver>();
        var baseline = observer.CountBacklog();

        var ride = await harness.DriveToAsync("Accepted");
        Assert.Equal(baseline, observer.CountBacklog());

        await using (var connection = await harness.OpenAsync())
        {
            // Due five seconds ago: the sweep is a second apart, so this is a worker that is
            // running normally, not one that has stopped.
            await connection.ExecuteAsync(
                "UPDATE rides.timers SET fire_at = now() - interval '5 seconds' WHERE ride_id = @RideId;",
                new { RideId = ride.RideId });
        }

        Assert.Equal(baseline, observer.CountBacklog());

        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE rides.timers SET fire_at = now() - interval '5 minutes' WHERE ride_id = @RideId;",
                new { RideId = ride.RideId });
        }

        Assert.Equal(baseline + 1, observer.CountBacklog());

        // And a swept timer leaves the backlog, so the alert clears on its own.
        await harness.SweepTimersAsync();
        Assert.Equal(baseline, observer.CountBacklog());
    }

    /// <summary>
    /// A finished ride is never stuck, however long ago it finished. The predicate is on the live
    /// state, so a month of terminal rides cannot silently page on-call.
    /// </summary>
    [Fact]
    public async Task A_terminal_ride_is_never_stuck()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RideHarness.StartAsync(postgres, MetricsOn);

        var observer = harness.Services.GetRequiredService<StuckStateObserver>();
        var rule = StuckStateObserver.Rules.Single(candidate => candidate.State == RideStates.Accepted);
        var baseline = observer.CountStuck(rule);

        var ride = await harness.DriveToAsync("Accepted");

        var cancelled = await harness.PostAsync(
            $"/v1/rides/{ride.RideId}/cancel",
            new { version = ride.Version, reason = "RIDER_CHANGED_MIND" },
            ride.PassengerBearer);

        Assert.Equal(System.Net.HttpStatusCode.OK, cancelled.StatusCode);

        await AgeAsync(harness, ride.RideId, TimeSpan.FromDays(30));

        Assert.Equal(baseline, observer.CountStuck(rule));
    }

    /// <summary>
    /// Pushes a ride's <c>updated_at</c> into the past, which is how long it has been in its state.
    /// </summary>
    /// <remarks>
    /// <c>session_replication_role = replica</c> is what makes this possible: <c>updated_at</c> is
    /// maintained by the <c>trg_rides_updated</c> BEFORE UPDATE trigger (migration 0002), so a
    /// plain <c>UPDATE … SET updated_at = …</c> is overwritten with <c>now()</c> before it lands.
    /// That the trigger wins is the reason the gauge can use the column as "time in state" at all —
    /// nothing can set it to anything but the moment of the last write.
    /// </remarks>
    private static async Task AgeAsync(RideHarness harness, Guid rideId, TimeSpan age)
    {
        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            """
            SET session_replication_role = replica;
            UPDATE rides.rides SET updated_at = now() - make_interval(secs => @Seconds) WHERE id = @RideId;
            SET session_replication_role = DEFAULT;
            """,
            new { RideId = rideId, Seconds = age.TotalSeconds });
    }
}
