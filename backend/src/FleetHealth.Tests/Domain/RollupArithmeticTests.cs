using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Rollups;

namespace MageRide.FleetHealth.Tests.Domain;

/// <summary>
/// The three pieces of arithmetic US-3.13 and US-3.16 turn on. Pure — no container.
/// </summary>
public sealed class RollupArithmeticTests
{
    private static readonly DateTimeOffset Bucket = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Percentages_are_of_the_whole_fleet_and_add_up()
    {
        var counts = new TrackerStateCounts(Online: 70, Stale: 10, Offline: 15, Decommissioned: 5);

        Assert.Equal(100, counts.Total);
        Assert.Equal(70, counts.PercentOf(counts.Online));
        Assert.Equal(10, counts.PercentOf(counts.Stale));
        Assert.Equal(15, counts.PercentOf(counts.Offline));
        Assert.Equal(5, counts.PercentOf(counts.Decommissioned));
    }

    [Fact]
    public void An_empty_fleet_reports_zero_rather_than_dividing_by_it()
    {
        // An operator who has onboarded no trackers should see an empty dashboard, not a broken one.
        Assert.Equal(0, TrackerStateCounts.Empty.Total);
        Assert.Equal(0, TrackerStateCounts.Empty.PercentOf(0));
    }

    [Fact]
    public void A_ten_percent_outage_reaches_the_ten_percent_threshold()
    {
        // The DoD's sentence, arithmetically: "a simulated 10 % fleet outage raises exactly one alert
        // per window". With a strict `>` comparison this is silent, which is why the comparison is `>=`.
        var window = new FleetWindowRollup(Guid.NewGuid(), Bucket, Bucket.AddMinutes(5), Expected: 100, Reporting: 90);

        Assert.Equal(10, window.Offline);
        Assert.Equal(10, window.OfflinePct);
        Assert.True(window.Breaches(10));
    }

    [Fact]
    public void One_vehicle_short_of_the_threshold_does_not_breach()
    {
        var window = new FleetWindowRollup(Guid.NewGuid(), Bucket, Bucket.AddMinutes(5), Expected: 100, Reporting: 91);

        Assert.Equal(9, window.OfflinePct);
        Assert.False(window.Breaches(10));
    }

    [Fact]
    public void More_vehicles_reporting_than_the_roster_holds_is_not_a_surplus()
    {
        // `active_vehicles` counts every vehicle carrying the fleet's id in telemetry.positions, which
        // includes one publishing from a phone (US-3.6) and one whose binding was revoked mid-window.
        // Without the floor the offline count goes negative and an outage reads as a surplus.
        var window = new FleetWindowRollup(Guid.NewGuid(), Bucket, Bucket.AddMinutes(5), Expected: 10, Reporting: 12);

        Assert.Equal(0, window.Offline);
        Assert.Equal(0, window.OfflinePct);
        Assert.False(window.Breaches(10));
    }

    [Fact]
    public void A_fleet_with_no_trackers_never_breaches()
    {
        var window = new FleetWindowRollup(Guid.NewGuid(), Bucket, Bucket.AddMinutes(5), Expected: 0, Reporting: 0);

        Assert.False(window.Breaches(10));
    }

    [Theory]
    [InlineData("2026-07-30T09:00:00Z", "2026-07-30T09:00:00Z")]
    [InlineData("2026-07-30T09:04:59Z", "2026-07-30T09:00:00Z")]
    [InlineData("2026-07-30T09:05:00Z", "2026-07-30T09:05:00Z")]
    [InlineData("2026-07-30T23:59:59Z", "2026-07-30T23:55:00Z")]
    public void Bucket_starts_floor_to_the_window(string instant, string expected) =>
        Assert.Equal(
            DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            TimeBuckets.Start(
                DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromMinutes(5)));

    [Fact]
    public void The_last_closed_window_is_the_one_before_the_one_being_written()
    {
        // Evaluating the bucket that contains `now` would read a fraction of a window's samples and
        // report most of the fleet as offline.
        var now = new DateTimeOffset(2026, 7, 30, 9, 7, 30, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero),
            TimeBuckets.LastClosedStart(now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_window_that_does_not_divide_a_day_is_refused()
    {
        // TimescaleDB's time_bucket origin has moved between versions and the gaps are whole days, so
        // flooring on Unix seconds only agrees with it for widths that divide 86 400.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeBuckets.Start(DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(7)));
    }

    [Fact]
    public void A_ping_merge_takes_the_newer_clock_and_keeps_the_richer_fields()
    {
        var vehicle = Guid.NewGuid();
        var fleet = Guid.NewGuid();

        var first = new DeviceHealthPing(vehicle, fleet, Bucket, Bucket, Source: 1, SatCount: 11);
        var second = new DeviceHealthPing(vehicle, null, Bucket.AddSeconds(30), Bucket.AddSeconds(30), null, null);

        var merged = first.Merge(second);

        Assert.Equal(Bucket.AddSeconds(30), merged.PingAt);
        Assert.Equal(fleet, merged.FleetId);
        Assert.Equal((short)1, merged.Source);
        Assert.Equal((short)11, merged.SatCount);
    }

    [Fact]
    public void A_ping_merge_cannot_move_a_clock_backwards()
    {
        // Per-vehicle ordering lapses for seconds during a consumer-group rebalance, so an overtaken
        // sample must not make a device look staler than it is.
        var vehicle = Guid.NewGuid();

        var newer = new DeviceHealthPing(vehicle, null, Bucket.AddSeconds(30), Bucket.AddSeconds(30), 1, 11);
        var older = new DeviceHealthPing(vehicle, null, Bucket, Bucket, 1, 9);

        Assert.Equal(Bucket.AddSeconds(30), newer.Merge(older).PingAt);
    }
}
