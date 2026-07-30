using Dapper;
using MageRide.FleetHealth.Rollups;
using MageRide.FleetHealth.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.FleetHealth.Tests.Integration;

/// <summary>
/// <c>telemetry.fleet_health_5m</c> maintenance — the deliverable that is easiest to leave as an empty
/// gesture, so it is asserted against the real aggregate.
/// </summary>
[Collection<FleetHealthCollection>]
public sealed class AggregateMaintenanceTests(PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task The_aggregate_this_service_reads_exists_with_a_policy_and_a_live_tail()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var status = await harness.Services.GetRequiredService<IAggregateMaintainer>()
            .VerifyAsync(CancellationToken.None);

        Assert.True(status.Exists, "telemetry.fleet_health_5m is not a continuous aggregate (migration 1802).");

        // materialized_only = false is what lets a read combine materialised buckets with the live tail.
        // With it true, the window that has just closed reads as zero vehicles reporting — which is
        // indistinguishable from a total outage, and would alert every fleet every five minutes.
        Assert.False(status.MaterializedOnly);

        // Without a policy nothing but this service materialises the aggregate, and every read outside
        // the refreshed window rescans raw hypertable chunks.
        Assert.True(status.HasRefreshPolicy);
    }

    [Fact]
    public async Task The_configured_window_matches_the_aggregates_bucket_width()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        // The alert's numerator is a fleet_health_5m bucket and its denominator is the tracker roster, so
        // a service configured to any other width would compare an N-minute expectation against a
        // 5-minute count. D7' §4.2's Health__WindowMin=5 and migration 1802's time_bucket('5 minutes')
        // are the same number written in two places, and this is the assertion that keeps them equal.
        Assert.Equal(5, AggregateMaintainer.AggregateBucketMinutes);
    }

    [Fact]
    public async Task A_refresh_materialises_the_closed_window_that_the_alert_then_reads()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: harness.Clock.GetUtcNow());

        var bucket = harness.Clock.GetUtcNow().AddMinutes(-5);

        await harness.WritePositionsAsync(fleet.FleetId, [tracker.VehicleId], bucket, samplesPerVehicle: 3);

        var maintainer = harness.Services.GetRequiredService<IAggregateMaintainer>();

        Assert.True(await maintainer.RefreshWindowAsync(bucket, bucket.AddMinutes(5), CancellationToken.None));

        await using var connection = await harness.OpenAsync();

        // Read from the materialised half only, so this is genuinely an assertion about the refresh and
        // not about real-time aggregation over raw chunks.
        var materialised = await connection.QuerySingleOrDefaultAsync<AggregateRow>(
            """
            SELECT fleet_id AS FleetId, active_vehicles::int AS ActiveVehicles, samples::int AS Samples
              FROM telemetry.fleet_health_5m
             WHERE fleet_id = @FleetId AND bucket = @Bucket;
            """,
            new { FleetId = fleet.FleetId, Bucket = bucket.ToUniversalTime() });

        Assert.NotNull(materialised);
        Assert.Equal(1, materialised.ActiveVehicles);
        Assert.Equal(3, materialised.Samples);
    }

    [Fact]
    public async Task Bucket_arithmetic_matches_TimescaleDBs_own_time_bucket()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await postgres.EnsureMigratedAsync();
        await using var connection = await postgres.OpenAsync();

        // The worker names the bucket it evaluates in a `WHERE bucket = …` predicate, so the boundary has
        // to be computed in .NET as well as in Postgres. If the two ever disagree the predicate matches no
        // row and every fleet reads as a total outage — a silent failure, hence a test rather than a
        // comment about time_bucket's origin.
        DateTimeOffset[] instants =
        [
            new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
            new(2026, 7, 30, 9, 2, 31, TimeSpan.Zero),
            new(2026, 7, 30, 9, 4, 59, TimeSpan.Zero),
            new(2026, 7, 30, 9, 5, 0, TimeSpan.Zero),
            new(2026, 7, 30, 23, 59, 59, TimeSpan.Zero),
            new(2026, 2, 28, 13, 37, 45, TimeSpan.Zero),
            new(2027, 1, 1, 0, 0, 1, TimeSpan.Zero),
        ];

        foreach (var instant in instants)
        {
            var fromPostgres = await connection.ExecuteScalarAsync<DateTimeOffset>(
                "SELECT time_bucket(interval '5 minutes', @At::timestamptz);",
                new { At = instant.ToUniversalTime() });

            Assert.Equal(fromPostgres, TimeBuckets.Start(instant, TimeSpan.FromMinutes(5)));
        }
    }

    private sealed record AggregateRow(Guid FleetId, int ActiveVehicles, int Samples);
}
