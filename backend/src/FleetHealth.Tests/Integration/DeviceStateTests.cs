using Dapper;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.FleetHealth.Tests.Integration;

/// <summary>
/// US-3.13's state ladder — the component's first definition of done: "a device silent past the stale
/// window flips to Stale, then Offline, at the configured thresholds".
/// </summary>
/// <remarks>
/// Every assertion goes through the endpoint or through the sweep, never through a C# classifier,
/// because there is no C# classifier: the ladder is <c>telemetry.device_health_state()</c> (migration
/// 1805), called by both the read and the sweep so the two cannot disagree.
/// </remarks>
[Collection<FleetHealthCollection>]
public sealed class DeviceStateTests(PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task A_device_silent_past_the_stale_window_flips_to_Stale_then_to_Offline()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: harness.Clock.GetUtcNow());

        // Just pinged.
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // Four minutes: inside the 5-minute window, still online. The boundary is what US-3.13 defines,
        // so it is asserted from both sides.
        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // Past five minutes: Stale.
        harness.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal("stale", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // Twenty-nine minutes: still Stale, not yet Offline. A bus in a tunnel is not a device failure.
        harness.Clock.Advance(TimeSpan.FromMinutes(24));
        Assert.Equal("stale", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // Past thirty minutes: Offline.
        harness.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal("offline", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // And a fresh ping brings it straight back, with no `online` message needed — a device that
        // crashed and restarted may never send one.
        await harness.SetLastPingAsync(tracker.VehicleId, harness.Clock.GetUtcNow());
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));
    }

    [Fact]
    public async Task A_tracker_that_has_never_reported_is_Offline_rather_than_Online()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: null);

        // A bound device that has never pinged is exactly the case an operator opens this dashboard to
        // find; defaulting it to Online would hide every failed installation.
        Assert.Equal("offline", await StateOfAsync(harness, fleet, tracker.VehicleId));
    }

    [Fact]
    public async Task A_last_will_takes_a_device_out_of_Online_without_making_it_Offline()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now);

        // R-15/T-04: the broker has said the session is gone. The device cannot be Online however recent
        // its last ping was — but it is not Offline either, because US-3.13 defines Offline as thirty
        // minutes of silence.
        await harness.SetLastStatusAsync(tracker.VehicleId, "offline", now.AddSeconds(1));
        Assert.Equal("stale", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // A fresher ping clears it. This is the C041 rule — the will holds an instant, not a flag — and
        // it matters because a device that crashed and restarted may never publish `online`.
        await harness.SetLastPingAsync(tracker.VehicleId, now.AddSeconds(2));
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // An explicit `online` clears it too.
        await harness.SetLastStatusAsync(tracker.VehicleId, "offline", now.AddSeconds(3));
        Assert.Equal("stale", await StateOfAsync(harness, fleet, tracker.VehicleId));

        await harness.SetLastStatusAsync(tracker.VehicleId, "online", now.AddSeconds(4));
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));
    }

    [Fact]
    public async Task A_revoked_credential_is_Decommissioned_and_a_quarantine_is_not()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        var decommissioned = await harness.CreateTrackerAsync(fleet.FleetId, "REVOKED", lastPingAt: now);
        var quarantined = await harness.CreateTrackerAsync(fleet.FleetId, "QUARANTINED", lastPingAt: now);

        // US-3.8: revoked credentials, no further ingest possible. The state wins over the recent ping —
        // otherwise a decommission performed while a device was still publishing would take half an hour
        // to appear.
        Assert.Equal("decommissioned", await StateOfAsync(harness, fleet, decommissioned.VehicleId));

        // T-08 holds a binding pending the US-3.4 admin decision and it may well come back, so it is
        // not decommissioned. It is publishing right now, so it is online.
        Assert.Equal("online", await StateOfAsync(harness, fleet, quarantined.VehicleId));
    }

    [Fact]
    public async Task The_sweep_records_each_transition_and_the_dashboard_does_not_wait_for_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: harness.Clock.GetUtcNow());

        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        // The read is already right, with no sweep at all: the state is derived, not stored. That is the
        // property that makes a dashboard correct between passes and correct with the sweep switched off.
        Assert.Equal("stale", await StateOfAsync(harness, fleet, tracker.VehicleId));

        var moved = await harness.SweepAsync();

        var transition = Assert.Single(moved, m => m.VehicleId == tracker.VehicleId);
        Assert.Equal(TrackerHealthStates.Online, transition.FromState);
        Assert.Equal(TrackerHealthStates.Stale, transition.ToState);
        Assert.Equal(fleet.FleetId, transition.FleetId);

        // Idempotent: nothing has changed since, so a second pass moves nothing.
        Assert.Empty(await harness.SweepAsync());

        harness.Clock.Advance(TimeSpan.FromMinutes(25));

        var second = Assert.Single(await harness.SweepAsync(), m => m.VehicleId == tracker.VehicleId);
        Assert.Equal(TrackerHealthStates.Stale, second.FromState);
        Assert.Equal(TrackerHealthStates.Offline, second.ToState);
    }

    [Fact]
    public async Task The_sweep_syncs_last_seen_and_diagnostics_onto_the_tracker_binding()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now);

        await using (var connection = await harness.OpenAsync())
        {
            // Before: C030 creates the four columns and nothing writes them, which is why
            // GET /v1/trackers/{imei} answered US-3.12 with a blank panel.
            var before = await connection.ExecuteScalarAsync<DateTimeOffset?>(
                "SELECT last_seen_at FROM prov.tracker_bindings WHERE imei = @Imei;", new { tracker.Imei });

            Assert.Null(before);

            await connection.ExecuteAsync(
                """
                UPDATE telemetry.device_health
                   SET signal_strength = 24, battery_mv = 4020, sat_count = 11, last_diag_at = @Now
                 WHERE vehicle_id = @VehicleId;
                """,
                new { VehicleId = tracker.VehicleId, Now = now });
        }

        await harness.SweepAsync();

        await using (var connection = await harness.OpenAsync())
        {
            var synced = await connection.QuerySingleAsync<BindingDiagnostics>(
                """
                SELECT last_seen_at AS LastSeenAt, signal_strength AS SignalStrength,
                       battery_mv AS BatteryMv, sat_count AS SatCount
                  FROM prov.tracker_bindings WHERE imei = @Imei;
                """,
                new { tracker.Imei });

            Assert.Equal(now, synced.LastSeenAt);
            Assert.Equal((short)24, synced.SignalStrength);
            Assert.Equal(4020, synced.BatteryMv);
            Assert.Equal((short)11, synced.SatCount);
        }
    }

    /// <summary>Reads one device's state out of the fleet rollup.</summary>
    private static async Task<string> StateOfAsync(FleetHealthHarness harness, SeededFleet fleet, Guid vehicleId)
    {
        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);
        var device = Assert.Single(rollup.Items, item => item.VehicleId == vehicleId);

        // The boolean and the state are two spellings of one fact and must never disagree.
        Assert.Equal(device.State == "online", device.Online);

        return device.State;
    }

    private sealed record BindingDiagnostics(
        DateTimeOffset? LastSeenAt, short? SignalStrength, int? BatteryMv, short? SatCount);
}
