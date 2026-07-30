using System.Diagnostics;
using System.Net;
using MageRide.FleetHealth.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.FleetHealth.Tests.Integration;

/// <summary>
/// <c>GET /v1/fleets/{fleetId}/health</c> — US-3.13's dashboard, its scoping, and this component's
/// third definition of done: "the rollup query answers in under 200 ms p95 for a 1000-vehicle fleet".
/// </summary>
[Collection<FleetHealthCollection>]
public sealed class FleetHealthEndpointTests(PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx)
{
    [Fact]
    public async Task The_rollup_counts_all_four_states_and_their_percentages()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        // Seven online, one stale, one offline, one decommissioned — a fleet of ten, so every percentage
        // is a whole number and a rounding bug cannot hide behind arithmetic.
        for (var i = 0; i < 7; i++)
        {
            await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now);
        }

        await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-6));
        await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-31));
        await harness.CreateTrackerAsync(fleet.FleetId, "REVOKED", lastPingAt: now);

        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        Assert.Equal(fleet.FleetId, rollup.FleetId);
        Assert.Equal(10, rollup.Counts.Total);
        Assert.Equal(7, rollup.Counts.Online);
        Assert.Equal(1, rollup.Counts.Stale);
        Assert.Equal(1, rollup.Counts.Offline);
        Assert.Equal(1, rollup.Counts.Decommissioned);

        Assert.Equal(70, rollup.Percentages.Online);
        Assert.Equal(10, rollup.Percentages.Stale);
        Assert.Equal(10, rollup.Percentages.Offline);
        Assert.Equal(10, rollup.Percentages.Decommissioned);

        // The pre-C044 pair. `vehiclesOffline` excludes the decommissioned one: a retired tracker is not
        // a tracker that went down.
        Assert.Equal(7, rollup.VehiclesOnline);
        Assert.Equal(2, rollup.VehiclesOffline);

        // The thresholds the answer was classified against, so a portal need not hardcode US-3.13's
        // five and thirty minutes.
        Assert.Equal(300, rollup.Thresholds.StaleAfterSeconds);
        Assert.Equal(1800, rollup.Thresholds.OfflineAfterSeconds);

        Assert.Equal(10, rollup.Items.Count);
        Assert.False(rollup.ItemsTruncated);
        Assert.Equal(now, rollup.AsOf);
    }

    [Fact]
    public async Task The_worst_states_come_first_so_a_truncated_list_still_shows_what_is_wrong()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(
            postgres, redpanda, emqx, new Dictionary<string, string?> { ["Health:MaxItems"] = "2" });

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now);
        await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-6));
        await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-31));

        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        Assert.True(rollup.ItemsTruncated);
        Assert.Equal(2, rollup.Items.Count);
        Assert.Equal(["offline", "stale"], rollup.Items.Select(item => item.State));

        // The cap bounds the list and never the counts, so a truncated answer cannot read as a smaller
        // fleet.
        Assert.Equal(3, rollup.Counts.Total);
    }

    [Fact]
    public async Task A_fleet_sees_only_its_own_devices()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        var myTracker = await harness.CreateTrackerAsync(mine.FleetId, lastPingAt: now);
        await harness.CreateTrackerAsync(theirs.FleetId, lastPingAt: now);
        await harness.CreateTrackerAsync(theirs.FleetId, lastPingAt: now);

        // The filter is the app.fleet_id GUC and the telemetry.device_health_fleet security-barrier view,
        // so it is the database that scopes this and not a WHERE clause the endpoint could forget
        // (ADD §9.5 item 8, ADD §7.7.7).
        var rollup = await harness.ReadHealthAsync(mine.FleetId, mine.Bearer);

        Assert.Equal(1, rollup.Counts.Total);
        Assert.Equal(myTracker.VehicleId, Assert.Single(rollup.Items).VehicleId);
    }

    [Fact]
    public async Task Another_organisations_fleet_is_refused_and_an_unknown_one_is_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        using (var response = await harness.GetHealthAsync(theirs.FleetId, mine.Bearer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // A fleet operator's own organisation id is in their token, so the only path they can construct
        // is their own — a fleet that does not exist is refused as "not yours" before anything looks it
        // up. Reaching the 404 at all takes one of AL-06's two platform roles.
        using (var response = await harness.GetHealthAsync(Guid.NewGuid(), mine.Bearer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await harness.GetHealthAsync(Guid.NewGuid(), harness.Tokens.Admin(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // A fleet_owner token with no org scope: either the sign-in predates the org or the claim was
        // dropped. Answering would mean choosing a fleet on the caller's behalf.
        var unscoped = harness.Tokens.UnscopedFleetUser(Guid.NewGuid());

        using (var response = await harness.GetHealthAsync(mine.FleetId, unscoped))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Every_fleet_sub_role_may_read_it_and_a_driver_may_not()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();

        // D3' marks the route "any sub-role" — a health dashboard is the least privileged thing in the
        // Fleet Portal, and the people who watch it are not the people who onboard vehicles.
        foreach (var role in new[] { "owner", "manager", "viewer" })
        {
            var bearer = harness.Tokens.FleetUser(Guid.NewGuid(), fleet.FleetId, role);

            using var response = await harness.GetHealthAsync(fleet.FleetId, bearer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // AL-06 gives the two platform roles blanket authority; a driver is refused by the role gate.
        using (var response = await harness.GetHealthAsync(fleet.FleetId, harness.Tokens.Admin(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await harness.GetHealthAsync(fleet.FleetId, harness.Tokens.Driver(Guid.NewGuid())))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/fleets/{fleet.FleetId}/health"))
        using (var response = await harness.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_rollup_answers_a_thousand_vehicle_fleet_inside_the_two_hundred_millisecond_budget()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        // A thousand devices in a spread of states, seeded in one statement — building them through
        // CreateTrackerAsync would be a thousand round trips of harness setup rather than of the thing
        // under test.
        await SeedFleetAsync(harness, fleet.FleetId, count: 1_000, now);

        // One warm-up: the first request pays for JIT, the connection pool and the query plan, none of
        // which is what the budget is about.
        var warm = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);
        Assert.Equal(1_000, warm.Counts.Total);

        const int samples = 20;
        var elapsed = new List<double>(samples);

        for (var i = 0; i < samples; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);
            stopwatch.Stop();

            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        elapsed.Sort();

        // p95 over twenty samples is the 19th, which is the largest but one.
        var p95 = elapsed[(int)Math.Ceiling(0.95 * samples) - 1];

        Assert.True(
            p95 < 200,
            $"p95 was {p95:F1} ms over {samples} reads of a 1000-vehicle fleet (budget 200 ms). " +
            $"Samples: {string.Join(", ", elapsed.Select(static value => value.ToString("F1")))}");
    }

    /// <summary>
    /// Seeds <paramref name="count"/> devices on one fleet directly, in the states the ladder classifies.
    /// </summary>
    /// <remarks>
    /// In bulk rather than through <c>CreateTrackerAsync</c>: a thousand round trips of harness setup is
    /// a thousand round trips of something that is not under test, and the point of this fixture is the
    /// shape of the fleet rather than how it was onboarded. A session temp table carries the generated
    /// ids across the four inserts, so the vehicle, the roster row, the binding and the health row all
    /// agree.
    /// </remarks>
    private static async Task SeedFleetAsync(
        FleetHealthHarness harness, Guid fleetId, int count, DateTimeOffset now)
    {
        // A run-scoped IMEI base and plate suffix. `ux_vehicles_regno_active` is unique and the harness
        // deliberately does not truncate registry.vehicles, so a fixed plate would collide the second
        // time this test ran against the same container.
        var seed = Random.Shared.NextInt64(100_000_000_000_000, 800_000_000_000_000);

        await using var connection = await harness.OpenAsync();

        await Dapper.SqlMapper.ExecuteAsync(
            connection,
            """
            CREATE TEMP TABLE perf_seed AS
              SELECT gen_random_uuid() AS vehicle_id,
                     (@Seed + n)::text AS imei,
                     n
                FROM generate_series(1, @Count) AS n;

            INSERT INTO registry.vehicles
                  (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            SELECT s.vehicle_id, f.owner_id, 'PERF-' || s.imei, 'bus', 'A', 'APPROVED', 'Perf Driver'
              FROM perf_seed s CROSS JOIN registry.fleets f
             WHERE f.id = @FleetId;

            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            SELECT @FleetId, s.vehicle_id, 'A' FROM perf_seed s;

            INSERT INTO prov.tracker_bindings
                  (imei, vehicle_id, fleet_id, credential_serial, credential_type, state, rotates_at, source)
            SELECT s.imei, s.vehicle_id, @FleetId, 'perf-' || s.imei, 'psk',
                   CASE WHEN s.n % 50 = 0 THEN 'REVOKED' ELSE 'ACTIVE' END,
                   now() + interval '90 days', 'hardware'
              FROM perf_seed s;

            INSERT INTO telemetry.device_health
                  (vehicle_id, fleet_id, imei, binding_state, decommissioned_at,
                   last_ping_at, last_sample_ts, signal_strength, battery_mv, sat_count,
                   observed_state, state_changed_at)
            SELECT s.vehicle_id, @FleetId, s.imei,
                   CASE WHEN s.n % 50 = 0 THEN 'REVOKED' ELSE 'ACTIVE' END,
                   CASE WHEN s.n % 50 = 0 THEN @Now ELSE NULL END,
                   -- A spread across the ladder: most online, some stale, some long silent.
                   CASE WHEN s.n % 10 = 0 THEN @Now - interval '40 minutes'
                        WHEN s.n % 7  = 0 THEN @Now - interval '8 minutes'
                        ELSE @Now END,
                   @Now, 24, 4020, 11, 'ONLINE', @Now
              FROM perf_seed s;

            DROP TABLE perf_seed;
            """,
            new { FleetId = fleetId, Count = count, Now = now, Seed = seed });
    }
}
