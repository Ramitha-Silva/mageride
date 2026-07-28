using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Ride.Endpoints;
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

namespace MageRide.Ride.Tests.Infrastructure;

/// <summary>
/// A running ride-svc on a real socket, against a real Postgres.
/// </summary>
/// <remarks>
/// Built through <see cref="RideApplication.Build"/>, so the pipeline under test — deny-by-default
/// authorization, the idempotency middleware, the problem+json handler, the outbox writer — is the
/// one the process runs. Kestrel rather than TestServer for the same reason C008's, C020's and
/// C021's harnesses use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class RideHarness : IAsyncDisposable
{
    /// <summary>Long enough to satisfy <c>Fare:EstimateTokenKey</c>'s minimum length.</summary>
    public const string FareTokenKey = "mageride-c022-test-fare-estimate-key";

    /// <summary>Guards <c>/v1/internal/rides/**</c> until C042 lands a mesh.</summary>
    public const string InternalApiKey = "mageride-c022-test-internal-key";

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private RideHarness(WebApplication app, HttpClient client, TestTokenIssuer tokens, PostgresFixture postgres)
    {
        _app = app;
        _postgres = postgres;
        Client = client;
        Tokens = tokens;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The codec ride-svc verifies with — the same key fare-svc would sign with.</summary>
    public FareEstimateTokenCodec FareTokens => Services.GetRequiredService<FareEstimateTokenCodec>();

    /// <summary>Colombo Fort and Dehiwala: ~9 km apart, both inside the fare stub's service box.</summary>
    public static readonly GeoPoint Pickup = new(6.9344, 79.8428);
    public static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    public static async Task<RideHarness> StartAsync(
        PostgresFixture postgres, IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer — so the pooled DSN also serves the
            // LISTEN the outbox dispatcher registers (E-09).
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's
            // auth wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fare:EstimateTokenKey"] = FareTokenKey,
            ["Ride:InternalApiKey"] = InternalApiKey,
            // Bound and validated at start-up even when nothing produces; the outbox test replaces
            // it with a real broker.
            ["Kafka:BootstrapServers"] = "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = "false",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = RideApplication.Build(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the
                // options. Everything else about validation — RS256 only, lifetime, issuer — is
                // left exactly as the kernel configured it, because that is what is under test.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        var baseAddress = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(60) };

        return new RideHarness(app, client, tokens, postgres);
    }

    // -------------------------------------------------------------------------------------------
    // Seeding. ride-svc creates neither accounts nor vehicles — iam-svc and registry-svc do.
    // -------------------------------------------------------------------------------------------

    /// <summary>Creates the <c>iam.users</c> row the ride's foreign keys need.</summary>
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
    /// Creates a driver with one APPROVED Mode C vehicle — what registry-svc's
    /// <c>POST /v1/vehicles</c> plus the dev approve path produce (C021).
    /// </summary>
    public async Task<SeededDriver> CreateDriverAsync(string vehicleType = "three_wheeler", string? driverName = null)
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var name = driverName ?? "Test Driver";
        var plate = NextPlate();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.users (id, phone, role) VALUES (@DriverId, @Phone, 'driver');
            INSERT INTO registry.vehicles (id, owner_id, registration_number, vehicle_type, mode, status, driver_name)
            VALUES (@VehicleId, @DriverId, @Plate, @VehicleType, 'C', 'APPROVED', @DriverName);
            """,
            new
            {
                DriverId = driverId,
                VehicleId = vehicleId,
                Phone = NextPhone(),
                Plate = plate,
                VehicleType = vehicleType,
                DriverName = name,
            });

        return new SeededDriver(driverId, vehicleId, plate, name, Tokens.Driver(driverId));
    }

    // -------------------------------------------------------------------------------------------
    // HTTP
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// POSTs JSON with an <c>Idempotency-Key</c>. A fresh key unless the caller supplies one —
    /// D3' §0 makes the header mandatory, so omitting it by accident would test the 400 path.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    /// <summary>
    /// POSTs to <c>/v1/internal/**</c> with the shared secret dispatch-svc would carry.
    /// <paramref name="bearer"/> is only ever needed to tell "the route is not mapped" (404 from
    /// routing) apart from "no credential at all" — the kernel's fallback policy answers 401 for an
    /// anonymous request whatever the path.
    /// </summary>
    public Task<HttpResponseMessage> PostInternalAsync(
        string path,
        object? body,
        string? apiKey = InternalApiKey,
        string? idempotencyKey = null,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());

        if (apiKey is not null)
        {
            request.Headers.Add(InternalRideEndpoints.ApiKeyHeader, apiKey);
        }

        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    // -------------------------------------------------------------------------------------------
    // Flow helpers — the happy path, so each test states only what it is about.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A signed quote, minted with the key ride-svc verifies against. Equivalent to what fare-svc
    /// returns; <c>FareStubTests</c> is where the real service's token is used instead.
    /// </summary>
    public string IssueFareToken(
        string vehicleType = "three_wheeler",
        string kind = "passenger",
        long amountMinor = 74_000,
        long surchargeMinor = 0) =>
        FareTokens.Issue(vehicleType, kind, amountMinor, surchargeMinor, 9.2, Pickup, Dropoff);

    /// <summary>Books a ride and returns the 202 body, failing the test on anything else.</summary>
    public async Task<JsonElement> RequestRideAsync(
        string passengerBearer,
        string? clientRequestId = null,
        string vehicleType = "three_wheeler",
        string? fareEstimateToken = null,
        string? idempotencyKey = null)
    {
        var response = await PostAsync(
            "/v1/rides/request",
            new
            {
                clientRequestId = clientRequestId ?? Guid.NewGuid().ToString(),
                pickup = new { lat = Pickup.Latitude, lng = Pickup.Longitude, address = "Colombo Fort" },
                dropoff = new { lat = Dropoff.Latitude, lng = Dropoff.Longitude, address = "Dehiwala" },
                vehicleType,
                fareEstimateToken = fareEstimateToken ?? IssueFareToken(vehicleType),
                paymentMethod = "cash",
            },
            passengerBearer,
            idempotencyKey);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>
    /// Drives the two moves dispatch-svc owns — <c>Requested → Matching → Offered</c> — and
    /// returns the offer the driver may now accept.
    /// </summary>
    public async Task<LiveOffer> OfferAsync(Guid rideId, SeededDriver driver, int? ttlSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var matching = await PostInternalAsync($"/v1/internal/rides/{rideId}/matching", new { });
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);

        var offerId = Guid.NewGuid();
        var offered = await PostInternalAsync(
            $"/v1/internal/rides/{rideId}/offer",
            new
            {
                offerId = offerId.ToString(),
                driverId = driver.DriverId.ToString(),
                vehicleId = driver.VehicleId.ToString(),
                ttlSeconds,
            });

        Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        var body = await ReadJsonAsync(offered);

        return new LiveOffer(rideId, offerId, body.GetProperty("version").GetInt64());
    }

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-RD-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string NextPhone() =>
        "+9477" + Random.Shared.NextInt64(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}

/// <summary>A driver plus the one APPROVED vehicle they are live on (US-9.6).</summary>
internal sealed record SeededDriver(Guid DriverId, Guid VehicleId, string Plate, string Name, string Bearer);

/// <summary>An offer that has been placed and not yet answered.</summary>
/// <param name="Version">The ride version the driver must echo on the accept.</param>
internal sealed record LiveOffer(Guid RideId, Guid OfferId, long Version);
