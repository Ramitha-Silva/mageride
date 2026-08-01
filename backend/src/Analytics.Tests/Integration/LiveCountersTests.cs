using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// The three real-time cards — online drivers, pending verifications, open tickets (D6' §I-28.5).
/// </summary>
[Collection<AnalyticsCollection>]
public sealed class LiveCountersTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = AnalyticsHarness.DefaultNow;

    /// <summary>
    /// The defining property: the live block bypasses the period filter. Filtering the dashboard to
    /// a week in 2020 must not change how many drivers are online right now.
    /// </summary>
    [Fact]
    public async Task The_live_block_is_the_same_whatever_period_is_asked_for()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var driver = await harness.Seed.CreateUserAsync("driver", Now);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);
        await harness.Seed.SetPresenceAsync(driver, vehicle, "AVAILABLE", Now);
        await harness.Seed.CreateTicketAsync(driver);

        var today = await harness.StatsAsync("today");
        var month = await harness.StatsAsync("month");
        var ancient = await harness.StatsAsync("custom", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 7));

        Assert.Equal(new Analytics.Domain.DashboardLive(1, 0, 1), today.Live);
        Assert.Equal(today.Live, month.Live);
        Assert.Equal(today.Live, ancient.Live);

        // ...while the period figures are, of course, all zero for 2020.
        Assert.Equal(0, ancient.Kpis.CompletedTrips);
    }

    /// <summary>
    /// A driver carrying a passenger is online. An <c>OFFLINE</c> row is not.
    /// </summary>
    [Theory]
    [InlineData("AVAILABLE", 1)]
    [InlineData("OFFERED", 1)]
    [InlineData("ON_RIDE", 1)]
    [InlineData("OFFLINE", 0)]
    public async Task Online_drivers_counts_every_state_but_offline(string state, int expected)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var driver = await harness.Seed.CreateUserAsync("driver", Now);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.SetPresenceAsync(driver, vehicle, state, Now);

        Assert.Equal(expected, (await harness.StatsAsync()).Live.OnlineDrivers);
    }

    /// <summary>
    /// A driver whose app died leaves an <c>AVAILABLE</c> row behind for ever. The freshness window
    /// is what stops the card only ever going up.
    /// </summary>
    [Fact]
    public async Task A_driver_past_the_freshness_window_is_not_online()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Analytics:PresenceFreshness"] = "00:02:00" });

        var fresh = await harness.Seed.CreateUserAsync("driver", Now);
        var stale = await harness.Seed.CreateUserAsync("driver", Now);

        await harness.Seed.SetPresenceAsync(fresh, await harness.Seed.CreateVehicleAsync(fresh), "AVAILABLE", Now.AddSeconds(-119));
        await harness.Seed.SetPresenceAsync(stale, await harness.Seed.CreateVehicleAsync(stale), "AVAILABLE", Now.AddSeconds(-121));

        Assert.Equal(1, (await harness.StatsAsync()).Live.OnlineDrivers);

        // The window is measured from the service's clock, not the database's: moving the clock
        // forward three minutes takes the fresh driver out too, without touching a row.
        harness.Clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(0, (await harness.StatsAsync()).Live.OnlineDrivers);
    }

    /// <summary>
    /// Pending verifications is the sum of AL-39's three queues: driving licence, vehicle
    /// registration, fleet-org approval.
    /// </summary>
    [Fact]
    public async Task Pending_verifications_is_the_sum_of_the_three_queues()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var driver = await harness.Seed.CreateUserAsync("driver", Now);
        var owner = await harness.Seed.CreateUserAsync("fleet_owner", Now);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        Assert.Equal(0, (await harness.StatsAsync()).Live.PendingVerifications);

        await harness.Seed.PendingLicenceAsync(driver);
        Assert.Equal(1, (await harness.StatsAsync()).Live.PendingVerifications);

        await harness.Seed.PendingOnboardingStepAsync(vehicle);
        Assert.Equal(2, (await harness.StatsAsync()).Live.PendingVerifications);

        await harness.Seed.CreateFleetAsync(owner);
        Assert.Equal(3, (await harness.StatsAsync()).Live.PendingVerifications);

        // An approved organisation has left the queue.
        await harness.Seed.CreateFleetAsync(owner, status: "APPROVED");
        Assert.Equal(3, (await harness.StatsAsync()).Live.PendingVerifications);
    }

    /// <summary>
    /// A licence with four doubtful fields is one driver to review, not four. The queue is counted by
    /// its subject, which is what SCR-AP-003 lists.
    /// </summary>
    [Fact]
    public async Task A_licence_with_several_doubtful_fields_is_one_item_in_the_queue()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var driver = await harness.Seed.CreateUserAsync("driver", Now);

        await harness.Seed.PendingLicenceAsync(driver, pendingFields: 4);

        Assert.Equal(1, (await harness.StatsAsync()).Live.PendingVerifications);
    }

    /// <summary>Two <c>pending_review</c> steps on one vehicle is one vehicle in the queue.</summary>
    [Fact]
    public async Task A_vehicle_with_two_pending_steps_is_one_item_in_the_queue()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var driver = await harness.Seed.CreateUserAsync("driver", Now);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.PendingOnboardingStepAsync(vehicle, "insurance");
        await harness.Seed.PendingOnboardingStepAsync(vehicle, "revenue");

        Assert.Equal(1, (await harness.StatsAsync()).Live.PendingVerifications);
    }

    /// <summary>
    /// "Open" is the work outstanding: a ticket an agent has picked up is still outstanding, and a
    /// resolved one is not.
    /// </summary>
    [Fact]
    public async Task Open_tickets_counts_everything_that_is_not_resolved()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var user = await harness.Seed.CreateUserAsync("passenger", Now);

        await harness.Seed.CreateTicketAsync(user, "OPEN");
        await harness.Seed.CreateTicketAsync(user, "IN_PROGRESS");
        await harness.Seed.CreateTicketAsync(user, "RESOLVED");

        Assert.Equal(2, (await harness.StatsAsync()).Live.OpenTickets);
    }

    /// <summary>An empty platform answers three zeroes, not three nulls.</summary>
    [Fact]
    public async Task An_empty_platform_answers_zeroes()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        Assert.Equal(Analytics.Domain.DashboardLive.Zero, (await harness.StatsAsync()).Live);
    }
}
