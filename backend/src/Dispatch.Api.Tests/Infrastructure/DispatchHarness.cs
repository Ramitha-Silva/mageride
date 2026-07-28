using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Ride;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Dispatch.Tests.Infrastructure;

/// <summary>
/// A running dispatch-svc <b>and</b> a running ride-svc on real sockets, against a real Postgres
/// and a real Redis.
/// </summary>
/// <remarks>
/// <para>
/// Both are built through their own <c>*Application.Build</c>, so the pipelines under test are the
/// ones the processes run. ride-svc is real rather than faked because everything this component
/// has to prove lives in the seam between them: who may write <c>rides.state</c>, whose clock
/// stamps <c>offer_expires_at</c>, and whether <c>offer.created</c> can precede a commit.
/// </para>
/// <para>
/// Background workers are <b>off by default</b>. A sweep or a consumer running underneath an
/// assertion would make "the offer expired at 15 s" indistinguishable from "something expired it";
/// the tests that need a worker turn exactly that one on.
/// </para>
/// </remarks>
internal sealed class DispatchHarness : IAsyncDisposable
{
    /// <summary>Long enough to satisfy <c>Fare:EstimateTokenKey</c>'s minimum length.</summary>
    public const string FareTokenKey = "mageride-c023-test-fare-estimate-key";

    /// <summary>Guards ride-svc's <c>/v1/internal/rides/**</c> until C042 lands a mesh.</summary>
    public const string InternalApiKey = "mageride-c023-test-internal-key";

    /// <summary>Colombo Fort — every test's pickup unless it says otherwise.</summary>
    public static readonly GeoPoint Pickup = new(6.9344, 79.8428);

    /// <summary>Dehiwala, ~9 km south. Only the pickup matters to dispatch.</summary>
    public static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _rideApp;
    private readonly WebApplication _dispatchApp;
    private readonly PostgresFixture _postgres;
    private readonly string _redisConnectionString;

    private DispatchHarness(
        WebApplication rideApp,
        WebApplication dispatchApp,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        string redisConnectionString)
    {
        _rideApp = rideApp;
        _dispatchApp = dispatchApp;
        _postgres = postgres;
        _redisConnectionString = redisConnectionString;
        Tokens = tokens;

        Client = NewClient(dispatchApp);
        RideClient = NewClient(rideApp);
        Redis = dispatchApp.Services.GetRequiredService<IConnectionMultiplexer>();
    }

    /// <summary>Talks to dispatch-svc.</summary>
    public HttpClient Client { get; }

    /// <summary>Talks to the real ride-svc standing beside it — the passenger half.</summary>
    public HttpClient RideClient { get; }

    public TestTokenIssuer Tokens { get; }

    public IConnectionMultiplexer Redis { get; }

    public IServiceProvider Services => _dispatchApp.Services;

    public IServiceProvider RideServices => _rideApp.Services;

    /// <summary>The codec ride-svc verifies a booking's quote with.</summary>
    public FareEstimateTokenCodec FareTokens => RideServices.GetRequiredService<FareEstimateTokenCodec>();

    public static async Task<DispatchHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? dispatchSettings = null,
        IDictionary<string, string?>? rideSettings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        // The TestKit shares one container per collection and does not reset between tests, so a
        // suite whose subject is "who is in the candidate pool" has to start from an empty pool.
        // Without this, a driver left online by an earlier test is a real candidate for this one's
        // ride, and the tests that assert *nobody* is near enough fail for the right reason at the
        // wrong time.
        await ResetDispatchStateAsync(postgres, redis);

        var tokens = new TestTokenIssuer();

        var rideApp = BuildRideService(postgres, tokens, rideSettings);
        await rideApp.StartAsync();

        var dispatchApp = BuildDispatchService(postgres, redis, tokens, BaseAddressOf(rideApp), dispatchSettings);
        await dispatchApp.StartAsync();

        return new DispatchHarness(rideApp, dispatchApp, tokens, postgres, redis.ConnectionString);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. dispatch-svc creates neither accounts nor vehicles — iam-svc and registry-svc do.
    // -----------------------------------------------------------------------------------------

    /// <summary>Creates the <c>iam.users</c> row a booking's foreign keys need.</summary>
    public async Task<Guid> CreatePassengerAsync()
    {
        var passengerId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, 'passenger');",
            new { Id = passengerId, Phone = NextPhone() });

        return passengerId;
    }

    /// <summary>
    /// A driver with one vehicle, as registry-svc's <c>POST /v1/vehicles</c> plus the dev approve
    /// path would leave them (C021).
    /// </summary>
    public async Task<SeededDriver> CreateDriverAsync(
        string vehicleType = "three_wheeler", string mode = "C", string status = "APPROVED")
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var plate = NextPlate();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, @Mode, @Status, 'Test Driver');
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = NextPhone(),
                Plate = plate,
                VehicleType = vehicleType,
                Mode = mode,
                Status = status,
            });

        return new SeededDriver(driverId, vehicleId, plate, Tokens.Driver(driverId));
    }

    /// <summary>Seeds a driver and puts them on standby at <paramref name="position"/>.</summary>
    public async Task<SeededDriver> CreateOnlineDriverAsync(
        GeoPoint position, string vehicleType = "three_wheeler")
    {
        var driver = await CreateDriverAsync(vehicleType);
        var response = await GoOnlineAsync(driver, position);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return driver;
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> GoOnlineAsync(
        SeededDriver driver, GeoPoint position, GeoPoint? driverHome = null, Guid? vehicleId = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return PostAsync(
            "/v1/standby/online",
            new
            {
                vehicleId = (vehicleId ?? driver.VehicleId).ToString(),
                position = new { lat = position.Latitude, lng = position.Longitude },
                driverHome = driverHome is { } home ? new { lat = home.Latitude, lng = home.Longitude } : null,
            },
            driver.Bearer);
    }

    public Task<HttpResponseMessage> GoOfflineAsync(SeededDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return PostAsync("/v1/standby/offline", new { }, driver.Bearer);
    }

    /// <summary>POSTs JSON to dispatch-svc with a fresh <c>Idempotency-Key</c> (D3' §0).</summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    // -----------------------------------------------------------------------------------------
    // ride-svc, driven directly — the passenger half of the skeleton
    // -----------------------------------------------------------------------------------------

    /// <summary>Books a ride through the real ride-svc and returns its id.</summary>
    public async Task<Guid> RequestRideAsync(
        Guid passengerId, string vehicleType = "three_wheeler", GeoPoint? pickup = null)
    {
        var from = pickup ?? Pickup;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rides/request")
        {
            Content = JsonContent.Create(new
            {
                clientRequestId = Guid.NewGuid().ToString(),
                pickup = new { lat = from.Latitude, lng = from.Longitude, address = "Colombo Fort" },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude, address = "Dehiwala" },
                vehicleType,
                fareEstimateToken = FareTokens.Issue(
                    vehicleType, "passenger", 74_000, 0, 9.2, from, Dropoff),
                paymentMethod = "cash",
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Tokens.Passenger(passengerId));

        using var response = await RideClient.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        var body = await ReadJsonAsync(response);
        return Guid.Parse(body.GetProperty("rideId").GetString()!);
    }

    /// <summary>
    /// The driver taps Decline, through ride-svc's real route. It is ride-svc that performs
    /// <c>Offered → Matching</c> and emits <c>offer.declined</c>; dispatch only reacts, so a test
    /// that skipped this and released dispatch's own row alone would leave the ride in
    /// <c>Offered</c> and prove nothing about the cascade.
    /// </summary>
    public async Task DeclineOfferAsync(SeededDriver driver, Guid rideId, Guid offerId)
    {
        ArgumentNullException.ThrowIfNull(driver);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/rides/{rideId}/offer/{driver.DriverId}/decline")
        {
            Content = JsonContent.Create(new { offerId = offerId.ToString() }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", driver.Bearer);

        using var response = await RideClient.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// What ride-svc currently holds for a ride, read straight from its table. A read, never a
    /// write: <c>rides.rides</c> is ride-svc's and dispatch-svc must never touch it.
    /// </summary>
    public async Task<RideSnapshot> ReadRideAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleAsync<RideSnapshot>(
            """
            SELECT state AS State,
                   current_offer_id AS CurrentOfferId,
                   offered_driver_id AS OfferedDriverId,
                   offer_expires_at AS OfferExpiresAt,
                   version AS Version
              FROM rides.rides WHERE id = @RideId;
            """,
            new { RideId = rideId });
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>
    /// Destroys the whole Redis keyspace mid-test — what R-04 means by "independent of any Redis
    /// TTL". Needs its own admin connection: the kernel's multiplexer does not open one, and that
    /// is deliberate (a service has no business issuing FLUSHDB or CONFIG SET).
    /// </summary>
    public async Task FlushRedisAsync()
    {
        await using var admin = await ConnectAsAdminAsync(_redisConnectionString);

        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-DP-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        RideClient.Dispose();

        await _dispatchApp.StopAsync();
        await _dispatchApp.DisposeAsync();
        await _rideApp.StopAsync();
        await _rideApp.DisposeAsync();
    }

    // -----------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------

    private static WebApplication BuildRideService(
        PostgresFixture postgres, TestTokenIssuer tokens, IDictionary<string, string?>? settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = FareTokenKey,
            ["Ride:InternalApiKey"] = InternalApiKey,
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Otel:PrometheusEnabled"] = "false",
        };

        Merge(overrides, settings);

        return RideApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, overrides));
    }

    private static WebApplication BuildDispatchService(
        PostgresFixture postgres,
        RedisFixture redis,
        TestTokenIssuer tokens,
        string rideBaseUrl,
        IDictionary<string, string?>? settings)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            ["Otel:PrometheusEnabled"] = "false",
            ["Dispatch:RideServiceBaseUrl"] = rideBaseUrl,
            ["Dispatch:RideServiceInternalKey"] = InternalApiKey,

            // Off unless a test asks: a background sweep or consumer running under an assertion
            // makes "the backstop fired" indistinguishable from "something fired".
            ["Dispatch:ExpiryWorkerEnabled"] = "false",
            ["Dispatch:ConsumerEnabled"] = "false",
            ["Dispatch:KeyspaceNotificationsEnabled"] = "false",
        };

        Merge(overrides, settings);

        return DispatchApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder => Configure(builder, tokens, overrides));
    }

    private static void Configure(
        WebApplicationBuilder builder, TestTokenIssuer tokens, Dictionary<string, string?> overrides)
    {
        // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }

        builder.Configuration.AddInMemoryCollection(overrides);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
        // Everything else about validation — RS256 only, lifetime, issuer — is left exactly as the
        // kernel configured it, because that is what is under test.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                bearer.ConfigurationManager = null;
                bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
            });
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
    /// Empties everything dispatch-svc owns, plus the <c>offer_expiry</c> timers it wrote into
    /// <c>rides.timers</c>. <c>rides.rides</c> and <c>iam.users</c> are left alone — every test
    /// mints fresh ids there, and truncating another bounded context's aggregate from a test
    /// harness is the sort of shortcut that later hides a real foreign-key bug.
    /// </summary>
    /// <remarks>
    /// <c>rides.outbox</c> is drained rather than emptied, and that one is not cosmetic. Most tests
    /// run with ride-svc's dispatcher off, so their <c>ride.requested</c> rows sit undispatched;
    /// the moment a later test turns the dispatcher on, every one of them is published and the
    /// consumer dutifully tries to dispatch a backlog of rides that finished minutes ago —
    /// reserving this test's only driver on the way through. Marking them dispatched says exactly
    /// what is true: as far as this test run is concerned, they have already been delivered.
    /// </remarks>
    private static async Task ResetDispatchStateAsync(PostgresFixture postgres, RedisFixture redis)
    {
        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                TRUNCATE dispatch.candidate_scores, dispatch.offers, dispatch.driver_presence,
                         dispatch.outbox, dispatch.command_log;
                DELETE FROM rides.timers WHERE kind = 'offer_expiry';
                UPDATE rides.outbox SET dispatched_at = now() WHERE dispatched_at IS NULL;
                """);
        }

        await using var admin = await ConnectAsAdminAsync(redis.ConnectionString);

        foreach (var endpoint in admin.GetEndPoints())
        {
            await admin.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    private static async Task<ConnectionMultiplexer> ConnectAsAdminAsync(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AllowAdmin = true;

        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    private static string BaseAddressOf(WebApplication app) =>
        // Fully qualified: StackExchange.Redis has its own IServer, and this file needs both.
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static HttpClient NewClient(WebApplication app) =>
        new() { BaseAddress = new Uri(BaseAddressOf(app)), Timeout = TimeSpan.FromSeconds(60) };

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A driver plus the one vehicle they were seeded with.</summary>
internal sealed record SeededDriver(Guid DriverId, Guid VehicleId, string Plate, string Bearer);

/// <summary>The slice of <c>rides.rides</c> a dispatch assertion cares about.</summary>
internal sealed record RideSnapshot(
    string State, Guid? CurrentOfferId, Guid? OfferedDriverId, DateTimeOffset? OfferExpiresAt, long Version);
