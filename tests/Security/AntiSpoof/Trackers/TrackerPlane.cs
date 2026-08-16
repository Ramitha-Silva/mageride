using System.Globalization;
using System.Net;
using Dapper;
using MageRide.Provisioning;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Trackers;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MageRide.Security.Tests.AntiSpoof.Trackers;

/// <summary>
/// provisioning-svc, composed against a real Postgres and Redis, driven through its own domain
/// services.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through <see cref="ITrackerService"/> rather than over HTTP</b>, which is the opposite of the
/// choice `Provisioning.Api.Tests` makes and is deliberate. That suite is proving the endpoints —
/// the ownership checks, the problem documents, the status codes — and needs a bearer and a seeded
/// account for each. What C128 is measuring is the T-08 and T-12 <i>rules</i>: how long a
/// revocation takes to bite, and whether a clone is held. Those are decided below the HTTP layer,
/// and driving them through it would put a token issuer and nine roles between the measurement and
/// the thing being measured.
/// </para>
/// <para>
/// It is still the composed service: <c>ProvisioningApplication.Build</c> is the composition root
/// <c>Program.cs</c> calls, so the repositories, the CA, the cache and the outbox writer are the
/// ones a container runs.
/// </para>
/// </remarks>
internal sealed class TrackerPlane : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly bool _ownsCaDirectory;

    private TrackerPlane(WebApplication app, PostgresFixture postgres, string caDirectory, bool ownsCaDirectory)
    {
        _app = app;
        _postgres = postgres;
        _ownsCaDirectory = ownsCaDirectory;
        CaDirectory = caDirectory;
    }

    public string CaDirectory { get; }

    /// <summary>
    /// Runs one operation against <see cref="ITrackerService"/> in a scope of its own.
    /// </summary>
    /// <remarks>
    /// <b>A scope per call, because a request is a scope.</b> The service is registered scoped —
    /// its unit-of-work factory and its connection are — so resolving it from the root provider is
    /// refused outright, and a fixture that registered it as a singleton to get past that would be
    /// testing a lifetime the deployment does not use. Two binds racing one IMEI is exactly the
    /// case the anti-clone rule exists for, and it is only expressible if each has its own scope.
    /// </remarks>
    public async Task<T> TrackersAsync<T>(Func<ITrackerService, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _app.Services.CreateAsyncScope();

        return await operation(scope.ServiceProvider.GetRequiredService<ITrackerService>());
    }

    /// <summary>The CRL the MQTT broker fetches (T-12). Singleton — its cache is process-wide.</summary>
    public ICrlService Crl => _app.Services.GetRequiredService<ICrlService>();

    public IConnectionMultiplexer Redis => _app.Services.GetRequiredService<IConnectionMultiplexer>();

    public ProvisioningOptions Options =>
        _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProvisioningOptions>>().Value;

    public static async Task<TrackerPlane> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        string? caDirectory = null,
        IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        // Per plane unless the caller supplied one, because a CA is process state: sharing one
        // would hide a bind that quietly minted from a different root than the broker trusts.
        var ca = caDirectory ?? Path.Combine(
            Path.GetTempPath(), "mageride-c128-ca-" + Guid.NewGuid().ToString("N")[..12]);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = "https://iam.mageride.test",
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            // No broker, so the dispatcher stays off; the outbox ROW is still written in the same
            // transaction, and the row is what T-12's durable half is.
            ["Outbox:DispatcherEnabled"] = "false",
            ["Provisioning:InternalApiKey"] = "c128-internal-key-not-a-secret-0123456789",
            ["Provisioning:ErrorReportSigningKey"] = "c128-signing-key-not-a-secret-0123456789",
            // Driven a pass at a time by whatever cares. A ticker would make every other
            // assertion's timing part of the measurement, which for a component that measures
            // timings is worse than usual.
            ["Provisioning:RotationEnabled"] = "false",
            ["Provisioning:BulkMintEnabled"] = "false",
            ["StepCa:RootKeyPath"] = ca,
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

        var app = ProvisioningApplication.Build(
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

        return new TrackerPlane(app, postgres, ca, ownsCaDirectory: caDirectory is null);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. provisioning-svc creates neither accounts nor vehicles — iam-svc and registry-svc do.
    // -----------------------------------------------------------------------------------------

    public async Task<Guid> CreateDriverAsync()
    {
        var userId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'driver');",
            new
            {
                Id = userId,
                Phone = "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture),
            });

        return userId;
    }

    public async Task<Guid> CreateVehicleAsync(Guid ownerId)
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, driver_name)
            VALUES (@Id, @OwnerId, @Plate, 'three_wheeler', 'C', 'APPROVED', 'approved', 'C128 Driver');
            """,
            new { Id = vehicleId, OwnerId = ownerId, Plate = NextPlate() });

        return vehicleId;
    }

    /// <summary>Binds an IMEI to a vehicle exactly as <c>POST /v1/trackers/bind</c> does.</summary>
    public Task<BoundTracker> BindAsync(
        Guid actorId, string imei, Guid vehicleId, IPAddress? from = null, CancellationToken cancellationToken = default) =>
        TrackersAsync(trackers => trackers.BindTrackerAsync(
            new BindTrackerCommand(
                actorId, IsAdmin: false, imei, vehicleId.ToString(), "manual", BindCode: null,
                CredentialTypes.X509, from ?? IPAddress.Parse("203.0.113.7")),
            cancellationToken));

    /// <summary>The adapter's per-connect check (T-01/T-03) and the T-12 credential question.</summary>
    public Task<ValidationVerdict> ValidateAsync(
        string imei, string? serial, IPAddress? from = null, CancellationToken cancellationToken = default) =>
        TrackersAsync(trackers => trackers.ValidateAsync(imei, serial, from, cancellationToken));

    /// <summary>US-3.8 / T-12 — the admin decommission.</summary>
    public Task DecommissionAsync(Guid actorId, string imei, CancellationToken cancellationToken = default) =>
        TrackersAsync<object?>(async trackers =>
        {
            await trackers.DecommissionAsync(actorId, imei, cancellationToken);
            return null;
        });

    /// <summary>T-08's other half — the adapter reporting two sockets under one identity.</summary>
    public Task<TrackerBinding?> QuarantineAsync(
        string imei, string reportedBy, string detail, CancellationToken cancellationToken = default) =>
        TrackersAsync(trackers => trackers.QuarantineAsync(imei, reportedBy, detail, cancellationToken));

    /// <summary>Every binding row for an IMEI, newest first — a clone leaves two.</summary>
    public async Task<IReadOnlyList<(string State, string Reason, Guid VehicleId)>> BindingsAsync(string imei)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string, Guid)>(
            """
            SELECT state, COALESCE(state_reason, '') AS reason, vehicle_id
            FROM prov.tracker_bindings
            WHERE imei = @Imei
            ORDER BY created_at DESC;
            """,
            new { Imei = imei });

        return [.. rows];
    }

    /// <summary>Whether the adapter's <c>imei:{imei}</c> fast path still resolves this device.</summary>
    public async Task<string?> CachedVehicleAsync(string imei) =>
        await Redis.GetDatabase().StringGetAsync(MageRide.Shared.Caching.RedisKeys.Imei(imei));

    /// <summary>
    /// Backdates every sighting of an IMEI, so the anti-clone window can be crossed without waiting
    /// a day.
    /// </summary>
    /// <remarks>
    /// The window is <b>configuration</b> (<c>Provisioning:AntiCloneWindow</c>) and shortening it
    /// would be the easier fixture. It is not used, because "a cloned IMEI quarantines both devices
    /// within the documented window" is a claim about the documented window — 24 h, D6' §4.3 —
    /// and a test that redefined the window to two seconds would prove the mechanism while saying
    /// nothing about the number. Moving the clock the sighting trail is judged against leaves the
    /// deployed 24 h in force.
    /// </remarks>
    public async Task AgeSightingsAsync(string imei, TimeSpan by)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE prov.imei_sightings SET seen_at = seen_at - @By::interval WHERE imei = @Imei;
            UPDATE prov.tracker_bindings SET created_at = created_at - @By::interval,
                   state_changed_at = state_changed_at - @By::interval
            WHERE imei = @Imei;
            """,
            new { Imei = imei, By = by });
    }

    public static string NextImei()
    {
        // Fifteen digits with a valid Luhn check, because Imeis.Require refuses anything else.
        var body = "35" + Random.Shared.NextInt64(1_000_000_000_00, 9_999_999_999_99)
            .ToString(CultureInfo.InvariantCulture)[..12];

        return body + LuhnCheckDigit(body);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        if (_ownsCaDirectory && Directory.Exists(CaDirectory))
        {
            Directory.Delete(CaDirectory, recursive: true);
        }
    }

    private static string NextPlate() =>
        "C128-" + Random.Shared.NextInt64(100_000, 999_999).ToString(CultureInfo.InvariantCulture);

    private static char LuhnCheckDigit(string body)
    {
        var sum = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var digit = body[body.Length - 1 - i] - '0';

            if (i % 2 == 0)
            {
                digit *= 2;

                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
