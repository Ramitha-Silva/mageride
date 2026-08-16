using System.Globalization;
using Dapper;
using MageRide.Reputation;
using MageRide.Reputation.Configuration;
using MageRide.Reputation.Detection;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Persistence;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.Security.Tests.AntiSpoof.Collusion;

/// <summary>
/// reputation-svc, composed against a real Postgres, with the E-07 detector's three inputs seeded
/// by hand.
/// </summary>
/// <remarks>
/// <para>
/// The detector is three SQL scans — a self-join over <c>reputation.intake_log</c> for pair
/// frequency, a group-by over <c>iam.devices</c> for shared bindings, and a group-by over
/// <c>reputation.network_observations</c> for address clustering. Measuring its precision means
/// producing a population those scans see, which means writing rows: there is no way to hand a
/// detector that reads the database a synthetic population except by putting it there.
/// </para>
/// <para>
/// <b>Seeded through SQL, judged through the service.</b> The rows are written directly because
/// producing 400 completions through <c>ride.events</c> would be measuring the consumer; the
/// detection is run through the composed <see cref="ICollusionDetector"/> so the thresholds, the
/// window key, the flag rows and the outbox event are all the deployment's.
/// </para>
/// </remarks>
internal sealed class CollusionPlane : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private CollusionPlane(WebApplication app, PostgresFixture postgres)
    {
        _app = app;
        _postgres = postgres;
    }

    public ReputationOptions Options =>
        _app.Services.GetRequiredService<IOptions<ReputationOptions>>().Value;

    public CollusionOptions Collusion => Options.Collusion;

    public static async Task<CollusionPlane> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = "https://iam.mageride.test",
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Reputation:InternalApiKey"] = "c128-reputation-key-not-a-secret-0123456789",
            // Every worker off. The detector is driven a pass at a time by the tests, because a
            // timer firing mid-assertion would make the window key a race.
            ["Reputation:ConsumerEnabled"] = "false",
            ["Reputation:ExpiryWorkerEnabled"] = "false",
            ["Reputation:DetectorEnabled"] = "false",
            // Ephemeral, so several planes can be alive at once.
            ["Reputation:GrpcListenPort"] = "0",
            ["Reputation:HttpListenPort"] = "0",
            ["Otel:PrometheusEnabled"] = "false",
            ["urls"] = "http://127.0.0.1:0",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = ReputationApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
            });

        await app.StartAsync();

        return new CollusionPlane(app, postgres);
    }

    /// <summary>One detection pass, in a scope of its own — the detector is scoped, as a worker is.</summary>
    public async Task<IReadOnlyList<FraudFlagRow>> DetectAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ICollusionDetector>()
            .RunAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------------------------
    // The population.
    // -----------------------------------------------------------------------------------------

    public async Task<Guid> CreateUserAsync(string role)
    {
        var userId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new
            {
                Id = userId,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
                Role = role,
            });

        return userId;
    }

    /// <summary>
    /// Records <paramref name="rides"/> completed rides between one passenger and one driver.
    /// </summary>
    /// <remarks>
    /// Two rows per ride, one per side, sharing a <c>ride_id</c> — which is what the pair detector
    /// self-joins on. A completion produces two facts because D5' §7.2 names no role and both runs
    /// have to reset; that the pair detector can read it at all is a consequence of that shape.
    /// </remarks>
    /// <param name="spread">How long a stretch the rides are spaced evenly over.</param>
    /// <param name="endingAgo">
    /// How long ago the <i>most recent</i> of them was. Zero puts the run right up against now,
    /// which is the ordinary case; a non-zero value places the whole run in the past, which is how
    /// the rolling-window assertion puts a heavy pair entirely outside the window instead of
    /// accidentally leaving its tail inside one.
    /// </param>
    public async Task CompleteRidesAsync(
        Guid passengerId, Guid driverId, int rides, TimeSpan spread, TimeSpan endingAgo = default)
    {
        await using var connection = await _postgres.OpenAsync();

        var now = DateTimeOffset.UtcNow - endingAgo;

        for (var i = 0; i < rides; i++)
        {
            var rideId = Guid.NewGuid();
            var at = now - (spread * (i / (double)Math.Max(rides, 2)));

            await connection.ExecuteAsync(
                """
                INSERT INTO reputation.intake_log
                  (dedupe_key, kind, subject_id, subject_role, ride_id, source, ts)
                VALUES (@PassengerKey, 'completion', @PassengerId, 'passenger', @RideId, 'ride.events', @At),
                       (@DriverKey,    'completion', @DriverId,    'driver',    @RideId, 'ride.events', @At);
                """,
                new
                {
                    PassengerKey = $"ride.events:{rideId}:{passengerId}",
                    DriverKey = $"ride.events:{rideId}:{driverId}",
                    PassengerId = passengerId,
                    DriverId = driverId,
                    RideId = rideId,
                    At = at,
                });
        }
    }

    /// <summary>Binds several accounts to one device install — E-07's device-binding cross-check.</summary>
    public async Task ShareDeviceAsync(string deviceKey, params Guid[] userIds)
    {
        await using var connection = await _postgres.OpenAsync();

        foreach (var userId in userIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO iam.devices (id, user_id, platform, device_key)
                VALUES (gen_random_uuid(), @UserId, 'android', @DeviceKey);
                """,
                new { UserId = userId, DeviceKey = deviceKey });
        }
    }

    /// <summary>Observes several accounts on one address — E-07's IP/ASN clustering input.</summary>
    public async Task ObserveOnAddressAsync(string ip, int? asn, TimeSpan ago, params Guid[] userIds)
    {
        await using var connection = await _postgres.OpenAsync();

        foreach (var userId in userIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO reputation.network_observations (user_id, ip, asn, observed_at)
                VALUES (@UserId, @Ip::inet, @Asn, @At);
                """,
                new { UserId = userId, Ip = ip, Asn = asn, At = DateTimeOffset.UtcNow - ago });
        }
    }

    /// <summary>Every flag raised, whatever the pass.</summary>
    public async Task<IReadOnlyList<(string Kind, Guid? Subject, Guid? Related)>> FlagsAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, Guid?, Guid?)>(
            "SELECT kind, subject_id, related_id FROM reputation.fraud_flags ORDER BY ts;");

        return [.. rows];
    }

    /// <summary>Whether any block state exists at all — the component's second fence.</summary>
    public async Task<int> BlockStateCountAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM reputation.block_states WHERE state <> 'OK';");
    }

    /// <summary>The <c>fraud.suspected</c> events queued for the admin surface.</summary>
    public async Task<int> FraudEventCountAsync()
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM reputation.outbox WHERE event_type = 'fraud.suspected';");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
