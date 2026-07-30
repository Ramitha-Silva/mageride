using System.Text.Json;
using MageRide.FleetHealth.Rollups;
using MageRide.FleetHealth.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.FleetHealth.Tests.Integration;

/// <summary>
/// US-3.16's device-down alert, and this component's second definition of done: "a simulated 10 % fleet
/// outage raises exactly one alert per window".
/// </summary>
/// <remarks>
/// The numerator comes from the real <c>telemetry.fleet_health_5m</c> continuous aggregate — rows are
/// written into <c>telemetry.positions</c> as persistence-writer-svc's <c>COPY</c> would (C040) and the
/// service refreshes the aggregate itself before reading it. Nothing here fakes the rollup, because the
/// claim being tested is that a real Timescale bucket and a real tracker roster produce the right
/// percentage.
/// </remarks>
[Collection<FleetHealthCollection>]
public sealed class AlertThresholdTests(PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task A_ten_percent_outage_raises_exactly_one_alert_per_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var trackers = await SeedTrackersAsync(harness, fleet.FleetId, count: 20);

        // The window before: every tracker reporting. Edge-triggering needs a healthy predecessor, and a
        // fleet that was already dark did not "go" offline.
        var previous = harness.Clock.GetUtcNow().AddMinutes(-10);
        var outage = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(fleet.FleetId, trackers, previous, samplesPerVehicle: 2);

        // The window under test: two of twenty stop reporting — 10 % exactly, which is the case the DoD
        // names and the case a strict `>` comparison would be silent for.
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Skip(2).ToArray(), outage, samplesPerVehicle: 2);

        var raised = await harness.EvaluateWindowAsync(outage);

        var alert = Assert.Single(raised);
        Assert.Equal(fleet.FleetId, alert.FleetId);
        Assert.Equal(outage, alert.Bucket);
        Assert.Equal(5, alert.WindowMinutes);
        Assert.Equal(20, alert.Expected);
        Assert.Equal(18, alert.Reporting);
        Assert.Equal(2, alert.Offline);
        Assert.Equal(10, alert.OfflinePct);
        Assert.Equal(10, alert.ThresholdPct);

        // Exactly one, however many times the window is evaluated. Every replica evaluates every window
        // and ux_fleet_health_alert_window is what lets one of them win — so re-evaluating is the same
        // test as running two replicas.
        Assert.Empty(await harness.EvaluateWindowAsync(outage));
        Assert.Empty(await harness.EvaluateWindowAsync(outage));
        Assert.Equal(1, await harness.CountAlertsAsync(fleet.FleetId));
    }

    [Fact]
    public async Task The_alert_and_its_outbox_row_commit_together()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var trackers = await SeedTrackersAsync(harness, fleet.FleetId, count: 10);

        var previous = harness.Clock.GetUtcNow().AddMinutes(-10);
        var outage = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(fleet.FleetId, trackers, previous);
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Skip(3).ToArray(), outage);

        var alert = Assert.Single(await harness.EvaluateWindowAsync(outage));

        // R-13: the alert row and the event commit in one transaction. An alert that committed and then
        // failed to publish would be an outage nobody was told about, behind a unique index that stops it
        // ever being retried.
        var row = Assert.Single(await harness.ReadOutboxAsync(FleetHealthEventTypes.HealthAlert));

        Assert.Equal(fleet.FleetId, row.AggregateId);

        using var payload = JsonDocument.Parse(row.Payload);
        var body = payload.RootElement;

        Assert.Equal(alert.AlertId, body.GetProperty("alertId").GetGuid());
        Assert.Equal(fleet.FleetId, body.GetProperty("fleetId").GetGuid());
        Assert.Equal(10, body.GetProperty("expectedVehicles").GetInt32());
        Assert.Equal(7, body.GetProperty("reportingVehicles").GetInt32());
        Assert.Equal(3, body.GetProperty("offlineVehicles").GetInt32());
        Assert.Equal(30, body.GetProperty("offlinePct").GetDouble());
        Assert.Equal(5, body.GetProperty("window").GetProperty("minutes").GetInt32());

        // A hand-off, not a notification: the trilingual template, the channel and the recipient's
        // preferences are notification-svc's (C051, D-26), so the payload names the type and carries no
        // rendered text.
        Assert.Equal(FleetHealthEvents.NotificationType, body.GetProperty("notificationType").GetString());
    }

    [Fact]
    public async Task A_fleet_that_was_already_dark_does_not_alert_again_every_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var trackers = await SeedTrackersAsync(harness, fleet.FleetId, count: 10);

        var first = harness.Clock.GetUtcNow().AddMinutes(-15);
        var second = harness.Clock.GetUtcNow().AddMinutes(-10);
        var third = harness.Clock.GetUtcNow().AddMinutes(-5);

        // Healthy, then half the fleet drops and stays down for two windows.
        await harness.WritePositionsAsync(fleet.FleetId, trackers, first);
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Take(5).ToArray(), second);
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Take(5).ToArray(), third);

        // US-3.16 is "N % of my fleet GOES offline within a 5-minute window" — a transition. The drop
        // alerts once.
        Assert.Single(await harness.EvaluateWindowAsync(second));

        // Still 50 % down, and no longer news. Level-triggered this would alert every five minutes for
        // ever, which ends with the alert muted — the same outcome as not alerting, but harder to notice.
        Assert.Empty(await harness.EvaluateWindowAsync(third));
        Assert.Equal(1, await harness.CountAlertsAsync(fleet.FleetId));
    }

    [Fact]
    public async Task A_fleet_below_the_threshold_raises_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var trackers = await SeedTrackersAsync(harness, fleet.FleetId, count: 20);

        var previous = harness.Clock.GetUtcNow().AddMinutes(-10);
        var outage = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(fleet.FleetId, trackers, previous);

        // One of twenty — 5 %, half the threshold.
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Skip(1).ToArray(), outage);

        Assert.Empty(await harness.EvaluateWindowAsync(outage));
        Assert.Equal(0, await harness.CountAlertsAsync(fleet.FleetId));
    }

    [Fact]
    public async Task One_fleets_outage_does_not_alert_another_fleet()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var struck = await harness.CreateFleetAsync();
        var healthy = await harness.CreateFleetAsync();

        var struckTrackers = await SeedTrackersAsync(harness, struck.FleetId, count: 10);
        var healthyTrackers = await SeedTrackersAsync(harness, healthy.FleetId, count: 10);

        var previous = harness.Clock.GetUtcNow().AddMinutes(-10);
        var outage = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(struck.FleetId, struckTrackers, previous);
        await harness.WritePositionsAsync(healthy.FleetId, healthyTrackers, previous);

        await harness.WritePositionsAsync(struck.FleetId, struckTrackers.Take(5).ToArray(), outage);
        await harness.WritePositionsAsync(healthy.FleetId, healthyTrackers, outage);

        var raised = await harness.EvaluateWindowAsync(outage);

        Assert.Equal(struck.FleetId, Assert.Single(raised).FleetId);
        Assert.Equal(0, await harness.CountAlertsAsync(healthy.FleetId));
    }

    [Fact]
    public async Task The_dashboard_shows_the_window_and_the_alert_it_raised()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var trackers = await SeedTrackersAsync(harness, fleet.FleetId, count: 10);

        var previous = harness.Clock.GetUtcNow().AddMinutes(-10);
        var outage = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(fleet.FleetId, trackers, previous);
        await harness.WritePositionsAsync(fleet.FleetId, trackers.Take(6).ToArray(), outage);

        // The worker's own clock arithmetic: at 09:00 the window that closed most recently starts at
        // 08:55, which is `outage`.
        Assert.Equal(1, await harness.EvaluateWindowAsync());

        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        Assert.Equal(outage, rollup.Window.Start);
        Assert.Equal(outage.AddMinutes(5), rollup.Window.End);
        Assert.Equal(10, rollup.Window.ExpectedVehicles);
        Assert.Equal(6, rollup.Window.ReportingVehicles);
        Assert.Equal(4, rollup.Window.OfflineVehicles);
        Assert.Equal(40, rollup.Window.OfflinePct);
        Assert.Equal(10, rollup.Window.ThresholdPct);
        Assert.True(rollup.Window.Alerting);

        Assert.NotNull(rollup.Alert);
        Assert.Equal(outage, rollup.Alert.Bucket);
        Assert.Equal(40, rollup.Alert.OfflinePct);

        // And the alert is fleet-scoped like everything else on the response.
        var other = await harness.CreateFleetAsync();
        var otherRollup = await harness.ReadHealthAsync(other.FleetId, other.Bearer);

        Assert.Null(otherRollup.Alert);
    }

    /// <summary>Creates <paramref name="count"/> active trackers on one fleet and returns their vehicles.</summary>
    private static async Task<IReadOnlyList<Guid>> SeedTrackersAsync(
        FleetHealthHarness harness, Guid fleetId, int count)
    {
        var vehicles = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            var tracker = await harness.CreateTrackerAsync(fleetId, lastPingAt: harness.Clock.GetUtcNow());
            vehicles.Add(tracker.VehicleId);
        }

        return vehicles;
    }
}
