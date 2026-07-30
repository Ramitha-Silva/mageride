using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Query.Tests.Infrastructure;

/// <summary>
/// A running query-svc on a real socket, against a real Postgres and a real Redis.
/// </summary>
/// <remarks>
/// Built through <see cref="QueryApplication.Build"/>, so the pipeline under test — the bearer handler,
/// the problem+json handler, the options validation, the gRPC interceptor — is the one the process runs.
/// Kestrel rather than TestServer, for the reason every harness on this platform uses it: a gRPC channel
/// needs a real HTTP/2 socket.
/// </remarks>
internal sealed class QueryHarness : IAsyncDisposable
{
    /// <summary>The interim shared secret <c>query.v1.Query</c> demands until the mesh lands.</summary>
    public const string InternalApiKey = "c042-query-internal-key-not-a-secret";

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;
    private static int _routeCounter = Random.Shared.Next(1_000, 9_000) * 10;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private QueryHarness(
        WebApplication app,
        HttpClient client,
        TestTokenIssuer tokens,
        PostgresFixture postgres,
        PositionWriter positions,
        string baseAddress,
        string grpcAddress)
    {
        _app = app;
        _postgres = postgres;
        Client = client;
        Tokens = tokens;
        Positions = positions;
        BaseAddress = baseAddress;
        GrpcAddress = grpcAddress;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>Writes the live index through the real position-processor-svc writer.</summary>
    public PositionWriter Positions { get; }

    /// <summary>The HTTP/1.1 listener, where the REST routes are.</summary>
    public string BaseAddress { get; }

    /// <summary>The HTTP/2 listener, where <c>query.v1.Query</c> is.</summary>
    public string GrpcAddress { get; }

    /// <summary>A route number no other test in this run will use.</summary>
    /// <remarks>
    /// The Postgres fixture is shared across the whole collection, so a literal like "138" in two test
    /// classes makes each one's assertion depend on the other's rows. <c>PlaceSearchTests</c> keeps the
    /// literal deliberately — its claim is about a plausible bus number — and everything else asks for
    /// one of these.
    /// </remarks>
    public static string NextRouteNumber() =>
        "Q" + Interlocked.Increment(ref _routeCounter).ToString("D5", CultureInfo.InvariantCulture);

    public IServiceProvider Services => _app.Services;

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-QY-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<QueryHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's auth
            // wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Query:InternalApiKey"] = InternalApiKey,
            // Both listeners ephemeral. `QueryApplication` binds them itself — HTTP/1.1 for REST and a
            // separate HTTP/2 one for gRPC, because cleartext has no ALPN — so the harness supplies
            // ports rather than configuring Kestrel, and what is under test is the production wiring.
            ["urls"] = "http://127.0.0.1:0",
            ["Query:GrpcListenPort"] = "0",
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

        var app = QueryApplication.Build(
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

                // PostConfigure so this runs after the kernel's AddMageRideAuth has built the options.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        // In the order they were registered above: HTTP/1.1 first, HTTP/2 second.
        var addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();

        var client = new HttpClient { BaseAddress = new Uri(addresses[0]), Timeout = TimeSpan.FromSeconds(120) };

        var positions = new PositionWriter(app.Services.GetRequiredService<IConnectionMultiplexer>());

        return new QueryHarness(app, client, tokens, postgres, positions, addresses[0], addresses[1]);
    }

    // ---------------------------------------------------------------------------------------
    // HTTP
    // ---------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return Client.SendAsync(request);
    }

    /// <summary>GETs and asserts a 200, returning the body. Failures print the problem document.</summary>
    public async Task<JsonElement> GetJsonAsync(string path, string? bearer)
    {
        using var response = await GetAsync(path, bearer);
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------------------------------------------
    // Seeding — query-svc writes nothing, so every row here is written the way its owning
    // service writes it. iam-svc, registry-svc, ride-svc, trip-state-svc and fare-svc do not
    // exist in this process.
    // ---------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public async Task<Guid> CreateUserAsync(string role = "passenger")
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

    /// <summary>An APPROVED vehicle, as registry-svc writes one.</summary>
    public async Task<Guid> CreateVehicleAsync(
        Guid ownerId,
        string mode = "C",
        string vehicleType = "three_wheeler",
        string driverName = "Test Driver")
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, dispatch_state,
               onboarding_status, driver_name)
            VALUES (@Id, @OwnerId, @Plate, @VehicleType, @Mode, 'APPROVED', 'ACTIVE', 'approved', @DriverName);
            """,
            new
            {
                Id = vehicleId,
                OwnerId = ownerId,
                Plate = NextPlate(),
                VehicleType = vehicleType,
                Mode = mode,
                DriverName = driverName,
            });

        return vehicleId;
    }

    /// <summary>A Mode C ride, as ride-svc writes one.</summary>
    public async Task<Guid> CreateRideAsync(
        Guid passengerId,
        GeoPoint pickup,
        GeoPoint dropoff,
        string state = "Completed",
        Guid? driverId = null,
        Guid? vehicleId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? terminalAt = null,
        long? fareEstimateMinor = null,
        Guid? bookerId = null)
    {
        var rideId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO rides.rides
              (id, passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
               state, accepted_driver_id, accepted_vehicle_id, fare_estimate_minor, currency,
               created_at, terminal_at)
            VALUES (@Id, @PassengerId, @ClientRequestId, @BookerId, 'three_wheeler',
                    ST_SetSRID(ST_MakePoint(@PickupLng, @PickupLat), 4326)::geography,
                    ST_SetSRID(ST_MakePoint(@DropoffLng, @DropoffLat), 4326)::geography,
                    @State, @DriverId, @VehicleId, @FareEstimateMinor, 'LKR', @CreatedAt, @TerminalAt);
            """,
            new
            {
                Id = rideId,
                PassengerId = passengerId,
                ClientRequestId = Guid.NewGuid(),
                BookerId = bookerId ?? passengerId,
                PickupLat = pickup.Latitude,
                PickupLng = pickup.Longitude,
                DropoffLat = dropoff.Latitude,
                DropoffLng = dropoff.Longitude,
                State = state,
                DriverId = driverId,
                VehicleId = vehicleId,
                FareEstimateMinor = fareEstimateMinor,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1),
                TerminalAt = terminalAt,
            });

        return rideId;
    }

    /// <summary>A settled payment attempt, as fare-svc writes one.</summary>
    public async Task AddPaymentAsync(
        Guid rideId,
        long amountMinor,
        long surchargeMinor = 0,
        long tipMinor = 0,
        string state = "Succeeded",
        string method = "onepay",
        short attemptNo = 1)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO fares.ride_payments
              (ride_id, state, method, amount_minor, surcharge_minor, tip_amount_minor, currency, attempt_no)
            VALUES (@RideId, @State, @Method, @AmountMinor, @SurchargeMinor, @TipMinor, 'LKR', @AttemptNo);
            """,
            new { RideId = rideId, State = state, Method = method, AmountMinor = amountMinor, SurchargeMinor = surchargeMinor, TipMinor = tipMinor, AttemptNo = attemptNo });
    }

    /// <summary>A Mode A/B tracking session, as trip-state-svc writes one.</summary>
    public async Task<Guid> CreateSessionAsync(
        Guid driverId,
        Guid vehicleId,
        string mode = "A",
        string state = "COMPLETED",
        Guid? routeId = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        var sessionId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO trips.sessions (id, vehicle_id, driver_id, mode, state, route_id, started_at, ended_at)
            VALUES (@Id, @VehicleId, @DriverId, @Mode, @State, @RouteId, @StartedAt, @EndedAt);
            """,
            new
            {
                Id = sessionId,
                VehicleId = vehicleId,
                DriverId = driverId,
                Mode = mode,
                State = state,
                RouteId = routeId,
                StartedAt = startedAt ?? DateTimeOffset.UtcNow.AddHours(-2),
                EndedAt = endedAt ?? (state == "COMPLETED" ? DateTimeOffset.UtcNow.AddHours(-1) : null),
            });

        return sessionId;
    }

    /// <summary>
    /// The ADD §9.2 trip summary, as persistence-writer-svc (C040) writes one on <c>session.ended</c>.
    /// </summary>
    /// <remarks>
    /// This is the artefact the "trip detail returns the <em>stored</em> polyline" claim is about, so it
    /// is written here exactly as that service writes it — a <c>geography(LINESTRING,4326)</c> column,
    /// not a JSON blob a test could shape to suit itself.
    /// </remarks>
    public async Task AddSessionSummaryAsync(
        Guid sessionId,
        Guid vehicleId,
        Guid driverId,
        string mode,
        IReadOnlyList<GeoPoint> path,
        double distanceM,
        string geometrySource = "telemetry",
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var wkt = path.Count >= 2
            ? "LINESTRING(" + string.Join(
                ", ",
                path.Select(point => string.Create(
                    CultureInfo.InvariantCulture, $"{point.Longitude} {point.Latitude}"))) + ")"
            : null;

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO trips.session_summaries
              (session_id, vehicle_id, driver_id, mode, started_at, ended_at,
               start_geo, end_geo, distance_m, polyline, sample_count, geometry_source)
            VALUES (@SessionId, @VehicleId, @DriverId, @Mode, @StartedAt, @EndedAt,
                    CASE WHEN @StartLng IS NULL THEN NULL
                         ELSE ST_SetSRID(ST_MakePoint(@StartLng, @StartLat), 4326)::geography END,
                    CASE WHEN @EndLng IS NULL THEN NULL
                         ELSE ST_SetSRID(ST_MakePoint(@EndLng, @EndLat), 4326)::geography END,
                    @DistanceM,
                    CASE WHEN @Wkt IS NULL THEN NULL
                         ELSE ST_SetSRID(ST_GeomFromText(@Wkt), 4326)::geography END,
                    @SampleCount, @GeometrySource);
            """,
            new
            {
                SessionId = sessionId,
                VehicleId = vehicleId,
                DriverId = driverId,
                Mode = mode,
                StartedAt = startedAt ?? DateTimeOffset.UtcNow.AddHours(-2),
                EndedAt = endedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
                StartLat = path.Count > 0 ? path[0].Latitude : (double?)null,
                StartLng = path.Count > 0 ? path[0].Longitude : (double?)null,
                EndLat = path.Count > 0 ? path[^1].Latitude : (double?)null,
                EndLng = path.Count > 0 ? path[^1].Longitude : (double?)null,
                DistanceM = distanceM,
                Wkt = wkt,
                SampleCount = path.Count,
                GeometrySource = geometrySource,
            });
    }

    /// <summary>A bus route, as C005's spatial seed writes one.</summary>
    public async Task<Guid> CreateRouteAsync(string routeNumber, string name, IReadOnlyList<GeoPoint> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var routeId = Guid.NewGuid();
        var wkt = "LINESTRING(" + string.Join(
            ", ",
            shape.Select(point => string.Create(
                CultureInfo.InvariantCulture, $"{point.Longitude} {point.Latitude}"))) + ")";

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO spatial.routes (id, name, route_number, geom, mode)
            VALUES (@Id, @Name, @RouteNumber, ST_SetSRID(ST_GeomFromText(@Wkt), 4326), 'A');
            """,
            new { Id = routeId, Name = name, RouteNumber = routeNumber, Wkt = wkt });

        return routeId;
    }

    /// <summary>A saved address, as iam-svc writes one (AL-26).</summary>
    public async Task AddSavedAddressAsync(
        Guid userId, string label, string line1, string? line3, GeoPoint point, bool isHome = false)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.saved_addresses (user_id, label, line1, line3, geo, is_home)
            VALUES (@UserId, @Label, @Line1, @Line3,
                    ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @IsHome);
            """,
            new
            {
                UserId = userId,
                Label = label,
                Line1 = line1,
                Line3 = line3,
                Lat = point.Latitude,
                Lng = point.Longitude,
                IsHome = isHome,
            });
    }

    /// <summary>A D-13 daily fee charge, as subscription-svc writes one.</summary>
    public async Task AddDailyFeeAsync(Guid driverId, Guid vehicleId, DateOnly feeDate, long amountMinor)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO billing.daily_fee_charges (driver_id, vehicle_id, fee_date, amount_minor, status)
            VALUES (@DriverId, @VehicleId, @FeeDate, @AmountMinor, 'PAID');
            """,
            new { DriverId = driverId, VehicleId = vehicleId, FeeDate = feeDate, AmountMinor = amountMinor });
    }

    /// <summary>A settled D-05 cancellation penalty credited to a driver.</summary>
    public async Task AddSettledPenaltyAsync(
        Guid passengerId, Guid affectedDriverId, long amountMinor, DateTimeOffset createdAt)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO dispatch.cancellation_penalties
              (passenger_id, original_ride_id, affected_driver_id, amount_minor, status, applied_ride_id, created_at)
            VALUES (@PassengerId, @OriginalRideId, @DriverId, @AmountMinor, 'SETTLED', @AppliedRideId, @CreatedAt);
            """,
            new
            {
                PassengerId = passengerId,
                OriginalRideId = Guid.NewGuid(),
                DriverId = affectedDriverId,
                AmountMinor = amountMinor,
                AppliedRideId = Guid.NewGuid(),
                CreatedAt = createdAt,
            });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
