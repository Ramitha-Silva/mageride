using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Endpoints;
using MageRide.FleetHealth.Ingest;
using MageRide.FleetHealth.Mqtt;
using MageRide.FleetHealth.Rollups;
using MageRide.Shared.Persistence;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.FleetHealth.Tests.Infrastructure;

/// <summary>
/// A running fleet-health-svc on a real socket, against a real Postgres, a real Redpanda and a real
/// EMQX.
/// </summary>
/// <remarks>
/// <para>
/// The service is built through <c>FleetHealthApplication.Build</c>, so the pipeline under test is the
/// one the process runs.
/// </para>
/// <para>
/// <b>Background workers are off by default.</b> A sweep or a consumer ticking underneath an assertion
/// would make "the device flipped to Stale at five minutes" indistinguishable from "something flipped
/// it"; the tests that need a pass call <see cref="SweepAsync"/> or <see cref="EvaluateWindowAsync"/>
/// directly. The device plane is the one exception — it is a subscription, and a subscription cannot be
/// driven a pass at a time.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/>.</b> US-3.13's ladder is thirty minutes wide and no
/// suite can wait for it. The classification itself takes the instant as an argument
/// (<c>telemetry.device_health_state(…, at)</c>), so advancing this clock genuinely advances the
/// service's view of time rather than approximating it.
/// </para>
/// </remarks>
internal sealed class FleetHealthHarness : IAsyncDisposable
{
    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private FleetHealthHarness(
        WebApplication app, PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx,
        TestTokenIssuer tokens, FakeTimeProvider clock)
    {
        _app = app;
        _postgres = postgres;
        Redpanda = redpanda;
        Emqx = emqx;
        Tokens = tokens;
        Clock = clock;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public RedpandaFixture Redpanda { get; }

    public EmqxFixture Emqx { get; }

    public IServiceProvider Services => _app.Services;

    public HealthSweepWorker Sweep => _app.Services.GetRequiredService<HealthSweepWorker>();

    public FleetHealthAlertWorker Alerts => _app.Services.GetRequiredService<FleetHealthAlertWorker>();

    public TelemetryHealthConsumer PingConsumer => _app.Services.GetRequiredService<TelemetryHealthConsumer>();

    public ProvisioningEventConsumer BindingConsumer =>
        _app.Services.GetRequiredService<ProvisioningEventConsumer>();

    public DevicePlaneWorker DevicePlane => _app.Services.GetRequiredService<DevicePlaneWorker>();

    public static async Task<FleetHealthHarness> StartAsync(
        PostgresFixture postgres,
        RedpandaFixture redpanda,
        EmqxFixture emqx,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(emqx);

        postgres.RequireAvailable();
        redpanda.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await redpanda.CreateRegistryTopicsAsync();

        // The TestKit shares one container per collection and does not reset between tests. A suite
        // whose subject is "how many of this fleet's trackers are online" has to start from a known
        // roster, and a device another test left behind is a real member of this test's percentage.
        await ResetAsync(postgres);

        var tokens = new TestTokenIssuer();

        // A whole 5-minute bucket boundary, so a test that reasons about "the window that just closed"
        // is not also reasoning about where inside a bucket the run happened to start.
        //
        // RELATIVE, where it used to be `new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero)`.
        // That literal had THREE properties and only two of them were written down. It was a 5-minute
        // boundary (above), it carried no sub-second component, and it was a fixed date — and the
        // fixed date is what broke the one test in this suite that talks to a real broker:
        //
        //   `MqttSessionTokenIssuer` mints the session JWT's `exp` from the INJECTED TimeProvider, so
        //   a clock frozen on 2026-07-30 produces a token that expired at 13:00 that day. EMQX
        //   validates `exp` against the WALL clock, refuses the CONNECT with `BadUserNameOrPassword`,
        //   and `DevicePlaneWorker` retries for ever — so `IsSubscribed` never turns true and the test
        //   times out with no visible cause. It passed on the day it was written and has failed every
        //   day since. HotPath's mqtt-bridge suite does the same 30 s subscription wait against the
        //   same fixture and passes precisely because it injects no fake clock at all.
        //
        // So: drop the date, keep the alignment. Flooring `UtcNow` to a 5-minute boundary preserves
        // both of the properties the literal was actually relied on for — the bucket edge that the
        // AlertThreshold and AggregateMaintenance suites reason about, and whole-second precision,
        // without which a `>=` wait against a value read back from Postgres can never be satisfied.
        var clock = new FakeTimeProvider(now ?? FloorToBucket(DateTimeOffset.UtcNow));

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
            ["Mqtt:Host"] = emqx.IsAvailable ? emqx.Host : "127.0.0.1",
            ["Mqtt:Port"] = (emqx.IsAvailable ? emqx.Port : 1).ToString(),
            ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
            ["Otel:PrometheusEnabled"] = "false",

            // Off unless a test asks: a background sweep, consumer or evaluation running under an
            // assertion makes "the ladder moved it" indistinguishable from "something moved it".
            ["Health:PingConsumerEnabled"] = "false",
            ["Health:ProvisioningConsumerEnabled"] = "false",
            ["Health:SweepEnabled"] = "false",
            ["Health:AlertsEnabled"] = "false",
            ["Health:DevicePlaneEnabled"] = "false",

            // The dispatcher would publish to `fleet.events` underneath the outbox assertions. The
            // tests that read the topic turn it on.
            ["Outbox:DispatcherEnabled"] = "false",

            // A fresh consumer group per run, so one test's committed offsets are not another's
            // starting point.
            ["Health:ConsumerGroup"] = $"fleet-health-test-{Guid.NewGuid():N}",
            ["Health:ProvisioningConsumerGroup"] = $"fleet-health-prov-test-{Guid.NewGuid():N}",
            ["Health:StartFromEarliest"] = "true",
            ["Health:FlushInterval"] = "00:00:00.100",
        };

        Merge(overrides, settings);

        var app = FleetHealthApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, clock, overrides));

        await app.StartAsync();

        return new FleetHealthHarness(app, postgres, redpanda, emqx, tokens, clock);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. fleet-health-svc creates neither organisations, vehicles nor bindings — registry-svc
    // and provisioning-svc do.
    // -----------------------------------------------------------------------------------------

    /// <summary>Creates an APPROVED fleet organisation and its owner (AL-03).</summary>
    public async Task<SeededFleet> CreateFleetAsync(string status = "APPROVED")
    {
        var ownerId = Guid.NewGuid();
        var fleetId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@OwnerId, @Phone, 'fleet_owner');
            INSERT INTO registry.fleets (id, owner_id, name, status)
            VALUES (@FleetId, @OwnerId, 'Test Fleet', @Status);
            """,
            new { OwnerId = ownerId, FleetId = fleetId, Phone = NextPhone(), Status = status });

        return new SeededFleet(fleetId, ownerId, Tokens.FleetUser(ownerId, fleetId));
    }

    /// <summary>
    /// A Mode A vehicle on the fleet's roster with an <c>ACTIVE</c> tracker binding — what
    /// provisioning-svc's bind leaves behind (C030).
    /// </summary>
    public async Task<SeededTracker> CreateTrackerAsync(
        Guid fleetId, string bindingState = "ACTIVE", DateTimeOffset? lastPingAt = null, bool inDeviceHealth = true)
    {
        var vehicleId = Guid.NewGuid();
        var imei = NextImei();

        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            SELECT @VehicleId, f.owner_id, @Plate, 'bus', 'A', 'APPROVED', 'Test Driver'
              FROM registry.fleets f WHERE f.id = @FleetId;

            INSERT INTO registry.fleet_vehicles (fleet_id, vehicle_id, mode)
            VALUES (@FleetId, @VehicleId, 'A') ON CONFLICT DO NOTHING;

            INSERT INTO prov.tracker_bindings
              (imei, vehicle_id, fleet_id, credential_serial, credential_type, state, rotates_at, source)
            VALUES (@Imei, @VehicleId, @FleetId, @Serial, 'psk', @State, now() + interval '90 days', 'hardware');
            """,
            new
            {
                VehicleId = vehicleId,
                FleetId = fleetId,
                Imei = imei,
                Plate = NextPlate(),
                Serial = $"test-{imei}",
                State = bindingState,
            });

        if (inDeviceHealth)
        {
            // What ProvisioningEventConsumer would have written from `tracker.bound`, plus an optional
            // last ping. Written directly because what most of these tests are about is the state
            // ladder, not the ingest path — IngestTests drives that end to end.
            await connection.ExecuteAsync(
                """
                INSERT INTO telemetry.device_health
                      (vehicle_id, fleet_id, imei, binding_state, decommissioned_at,
                       last_ping_at, last_sample_ts, observed_state, state_changed_at)
                VALUES (@VehicleId, @FleetId, @Imei, @State,
                        CASE WHEN @State = 'REVOKED' THEN now() ELSE NULL END,
                        @LastPingAt::timestamptz, @LastPingAt::timestamptz,
                        CASE WHEN @LastPingAt::timestamptz IS NULL THEN 'OFFLINE' ELSE 'ONLINE' END, now())
                ON CONFLICT (vehicle_id) DO UPDATE
                   SET fleet_id = EXCLUDED.fleet_id, imei = EXCLUDED.imei,
                       binding_state = EXCLUDED.binding_state,
                       decommissioned_at = EXCLUDED.decommissioned_at,
                       last_ping_at = EXCLUDED.last_ping_at;
                """,
                new
                {
                    VehicleId = vehicleId,
                    FleetId = fleetId,
                    Imei = imei,
                    State = bindingState,
                    LastPingAt = lastPingAt,
                });
        }

        return new SeededTracker(vehicleId, imei);
    }

    /// <summary>
    /// Puts raw rows into <c>telemetry.positions</c>, as persistence-writer-svc's <c>COPY</c> would
    /// (C040) — the only thing <c>telemetry.fleet_health_5m</c> counts.
    /// </summary>
    public async Task WritePositionsAsync(
        Guid fleetId, IReadOnlyCollection<Guid> vehicleIds, DateTimeOffset sampleTs, int samplesPerVehicle = 1)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);

        await using var connection = await _postgres.OpenAsync();

        for (var i = 0; i < samplesPerVehicle; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO telemetry.positions
                      (vehicle_id, sample_ts, received_ts, seq, lat, lng, source, fleet_id)
                SELECT v.id, @SampleTs, @SampleTs, @Seq, 6.9271, 79.8612, 1, @FleetId
                  FROM unnest(@VehicleIds::uuid[]) AS v(id)
                ON CONFLICT DO NOTHING;
                """,
                new
                {
                    VehicleIds = vehicleIds.ToList(),
                    SampleTs = sampleTs.AddSeconds(i).ToUniversalTime(),
                    Seq = (long)i,
                    FleetId = fleetId,
                });
        }
    }

    /// <summary>Ages a device's last ping so the state ladder sees it as silent.</summary>
    public async Task SetLastPingAsync(Guid vehicleId, DateTimeOffset? at)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE telemetry.device_health SET last_ping_at = @At, last_sample_ts = @At WHERE vehicle_id = @VehicleId;",
            new { VehicleId = vehicleId, At = at?.ToUniversalTime() });
    }

    /// <summary>Records a retained <c>veh/{vehicleId}/status</c> payload without a broker.</summary>
    public async Task SetLastStatusAsync(Guid vehicleId, string status, DateTimeOffset at)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE telemetry.device_health
               SET last_status = @Status, last_status_at = @At
             WHERE vehicle_id = @VehicleId;
            """,
            new { VehicleId = vehicleId, Status = status, At = at.ToUniversalTime() });
    }

    // -----------------------------------------------------------------------------------------
    // Driving the service
    // -----------------------------------------------------------------------------------------

    /// <summary>Runs one sweep pass and returns what moved.</summary>
    public Task<IReadOnlyList<HealthTransition>> SweepAsync() =>
        Sweep.RunOnceAsync(CancellationToken.None);

    /// <summary>Runs one alert-evaluation pass over the window that closed most recently.</summary>
    public Task<int> EvaluateWindowAsync() => Alerts.RunOnceAsync(CancellationToken.None);

    /// <summary>Evaluates one named window, bypassing the worker's own clock arithmetic.</summary>
    public async Task<IReadOnlyList<FleetHealthAlert>> EvaluateWindowAsync(DateTimeOffset bucketStart)
    {
        await using var scope = Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IFleetHealthAlertService>()
            .EvaluateWindowAsync(bucketStart, CancellationToken.None);
    }

    /// <summary>Reads the rollup as a fleet operator would.</summary>
    public async Task<HttpResponseMessage> GetHealthAsync(Guid fleetId, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/fleets/{fleetId}/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await Client.SendAsync(request);
    }

    /// <summary>Reads the rollup and deserialises it, failing the test on any non-200.</summary>
    public async Task<FleetHealthRollupResponse> ReadHealthAsync(Guid fleetId, string bearer)
    {
        using var response = await GetHealthAsync(fleetId, bearer);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<FleetHealthRollupResponse>())!;
    }

    /// <summary>Every <c>telemetry.outbox</c> row of one event type, newest last.</summary>
    public async Task<IReadOnlyList<OutboxRowView>> ReadOutboxAsync(string eventType)
    {
        await using var connection = await _postgres.OpenAsync();

        return [.. await connection.QueryAsync<OutboxRowView>(
            """
            SELECT aggregate_id AS AggregateId, event_type AS EventType, payload::text AS Payload
              FROM telemetry.outbox
             WHERE event_type = @EventType
             ORDER BY id;
            """,
            new { EventType = eventType })];
    }

    /// <summary>The alert rows one fleet has, newest first.</summary>
    public async Task<int> CountAlertsAsync(Guid fleetId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM telemetry.fleet_health_alerts WHERE fleet_id = @FleetId;",
            new { FleetId = fleetId });
    }

    /// <summary>Opens a connection on the test database — for asserting against the schema directly.</summary>
    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(10));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the harness service: {exception.Message}");
        }
    }

    private static void Configure(
        WebApplicationBuilder builder,
        TestTokenIssuer tokens,
        FakeTimeProvider clock,
        Dictionary<string, string?> overrides)
    {
        // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }

        builder.Configuration.AddInMemoryCollection(overrides);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Ahead of AddMageRideDefaults's TryAddSingleton, so the whole service — the ladder's `at`, the
        // bucket arithmetic, every worker's timer — runs on the test's clock.
        builder.Services.AddSingleton<TimeProvider>(clock);

        // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
            });
    }

    /// <summary>The 5-minute bucket <paramref name="instant"/> falls in, at its start.</summary>
    /// <remarks>
    /// Two properties in one, both of which the old absolute literal had by construction and neither
    /// of which was stated: a rollup bucket edge, and no sub-second component. Timestamps written
    /// through this service come back from Postgres at second precision, and several waits here are
    /// `>=` comparisons against a value captured from this clock — a fractional start makes them
    /// unsatisfiable for ever ("waiting for the ping clock to reach 06:53:53.3675242; the row was
    /// 06:53:53"). Flooring to the bucket gives both.
    /// </remarks>
    private static DateTimeOffset FloorToBucket(DateTimeOffset instant)
    {
        var bucket = TimeSpan.TicksPerMinute * 5;
        return new DateTimeOffset(instant.Ticks - (instant.Ticks % bucket), instant.Offset);
    }

    private static void Merge(Dictionary<string, string?> into, IDictionary<string, string?>? from)
    {
        if (from is null)
        {
            return;
        }

        foreach (var (key, value) in from)
        {
            into[key] = value;
        }
    }

    /// <summary>
    /// Empties everything fleet-health-svc owns, plus the telemetry rows and tracker bindings the
    /// tests seed.
    /// </summary>
    /// <remarks>
    /// <c>registry.fleets</c>, <c>registry.vehicles</c> and <c>iam.users</c> are left alone — every test
    /// mints fresh ids there, and truncating another bounded context's aggregate from a test harness is
    /// the sort of shortcut that later hides a real foreign-key bug. <c>prov.tracker_bindings</c> is the
    /// exception because it is this component's <i>denominator</i>: a binding another test left behind
    /// is a real member of this test's percentage.
    /// </remarks>
    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            TRUNCATE telemetry.device_health, telemetry.fleet_health_alerts, telemetry.outbox;
            DELETE FROM prov.tracker_bindings;
            DELETE FROM telemetry.positions;
            """);
    }

    private static string NextPhone() =>
        $"+9477{Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string NextPlate() =>
        $"FH-{Interlocked.Increment(ref _plateCounter).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string NextImei() =>
        Random.Shared.NextInt64(100_000_000_000_000, 999_999_999_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A seeded fleet organisation and a bearer for its owner.</summary>
internal sealed record SeededFleet(Guid FleetId, Guid OwnerId, string Bearer);

/// <summary>A seeded vehicle with an <c>ACTIVE</c> tracker binding.</summary>
internal sealed record SeededTracker(Guid VehicleId, string Imei);

/// <summary>One <c>telemetry.outbox</c> row, read back.</summary>
internal sealed record OutboxRowView(Guid AggregateId, string EventType, string Payload);

/// <summary>Extras the ingest tests use to build a canonical sample.</summary>
internal static class Samples
{
    /// <summary>Colombo Fort, one tracker fix.</summary>
    public static PositionSample Position(
        Guid vehicleId, Guid? fleetId, DateTimeOffset sampleTs, long seq = 1, int? satCount = 11) =>
        new(
            vehicleId,
            sampleTs,
            seq,
            6.9344,
            79.8428,
            PositionSource.Gt06,
            ReceivedTs: sampleTs,
            SpeedMps: 8.5,
            HeadingDeg: 270,
            AccuracyM: 7.5,
            Hdop: 0.9,
            SatCount: satCount,
            Mode: "A",
            VehicleType: "bus",
            FleetId: fleetId);
}
