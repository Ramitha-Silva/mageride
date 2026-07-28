using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using MageRide.TripState.Domain;
using MageRide.TripState.Endpoints;
using MageRide.TripState.Sessions;
using MageRide.TripState.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.TripState.Tests.Infrastructure;

/// <summary>
/// A running trip-state-svc on a real socket, against a real Postgres, Redis and Redpanda.
/// </summary>
/// <remarks>
/// Built through <see cref="TripStateApplication.Build"/>, so the pipeline under test — the role
/// gate, the idempotency middleware, the problem+json handler, the outbox dispatcher — is the one
/// the process runs. Kestrel rather than TestServer, for the reason C021's and C030's harnesses
/// use it: the idempotency middleware swaps the response body feature.
/// </remarks>
internal sealed class TripStateHarness : IAsyncDisposable
{
    /// <summary>The shared secret <c>/v1/internal/sessions/**</c> demands until C042 lands a mesh.</summary>
    public const string InternalApiKey = "c031-trip-state-internal-key-not-a-secret";

    private static int _plateCounter = Random.Shared.Next(1_000, 9_000) * 1_000;

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;

    private TripStateHarness(WebApplication app, HttpClient client, TestTokenIssuer tokens, PostgresFixture postgres)
    {
        _app = app;
        _postgres = postgres;
        Client = client;
        Tokens = tokens;
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>A plate no other test in this run will use.</summary>
    public static string NextPlate() =>
        "WP-TS-" + (Interlocked.Increment(ref _plateCounter) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<TripStateHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture? redis = null,
        RedpandaFixture? redpanda = null,
        IDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        var tokens = new TestTokenIssuer();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's
            // auth wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            // A dead address rather than an empty one, so a code path that reaches Redis without a
            // fixture fails loudly instead of connecting to whatever is on localhost.
            ["ConnectionStrings:Redis"] = redis?.ConnectionString
                                          ?? "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200",
            ["Kafka:BootstrapServers"] = redpanda?.BootstrapServers ?? "127.0.0.1:1",
            ["Outbox:DispatcherEnabled"] = redpanda is null ? "false" : "true",
            ["TripState:InternalApiKey"] = InternalApiKey,
            // The sweep and the position consumer are driven a pass at a time by the tests that
            // care, for the same reason C029's E-03 job and C030's rotation sweep are: a ticker
            // would make every other suite's timing part of the assertion.
            ["TripState:SweepEnabled"] = "false",
            ["TripState:PositionConsumerEnabled"] = "false",
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

        var app = TripStateApplication.Build(
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
                // options. Everything else about validation is left as the kernel configured it.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        var baseAddress = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(120) };

        return new TripStateHarness(app, client, tokens, postgres);
    }

    // ---------------------------------------------------------------------------------------
    // HTTP
    // ---------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? bearer, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer);

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request, bearer);
        return Client.SendAsync(request);
    }

    /// <summary>Calls a service-to-service route with the shared secret the adapter would carry.</summary>
    public Task<HttpResponseMessage> PostInternalAsync(string path, object? body = null, string? apiKey = InternalApiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        if (apiKey is not null)
        {
            request.Headers.Add(InternalSessionEndpoints.ApiKeyHeader, apiKey);
        }

        return Client.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------
    // Seeding — trip-state-svc creates neither accounts nor vehicles; iam-svc and registry-svc do
    // ---------------------------------------------------------------------------------------

    public async Task<Guid> CreateUserAsync(string role = "driver")
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

    /// <summary>An APPROVED vehicle owned by <paramref name="ownerId"/>, in the mode given.</summary>
    /// <remarks>
    /// Mode A/B vehicles are registered through the Fleet Portal (C059), which does not exist yet;
    /// the rows are seeded the way that component will write them so the eligibility projection
    /// this service reads answers exactly as it will in production.
    /// </remarks>
    public async Task<Guid> CreateVehicleAsync(
        Guid ownerId, string mode = OperatingModes.A, string status = "APPROVED", string dispatchState = "ACTIVE")
    {
        var vehicleId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO registry.vehicles
              (id, owner_id, registration_number, vehicle_type, mode, status, dispatch_state,
               onboarding_status, driver_name)
            VALUES (@Id, @OwnerId, @Plate, @VehicleType, @Mode, @Status, @DispatchState,
                    CASE WHEN @Status = 'APPROVED' THEN 'approved' ELSE 'incomplete' END, 'Test Driver');
            """,
            new
            {
                Id = vehicleId,
                OwnerId = ownerId,
                Plate = NextPlate(),
                // AL-09's Mode A type. A Mode C session is refused by this service on the mode, not
                // on the type, so the type only has to be one the CHECK admits.
                VehicleType = mode == OperatingModes.C ? "three_wheeler" : "bus",
                Mode = mode,
                Status = status,
                DispatchState = dispatchState,
            });

        return vehicleId;
    }

    // ---------------------------------------------------------------------------------------
    // Reads the assertions need
    // ---------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    /// <summary>Every session on a vehicle, newest first — state, reason and who did it.</summary>
    public async Task<IReadOnlyList<(string State, string? EndReason, string StartedBy, string? EndedBy)>>
        SessionsAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, string?, string, string?)>(
            """
            SELECT state, end_reason, started_by, ended_by FROM trips.sessions
             WHERE vehicle_id = @VehicleId ORDER BY started_at DESC, id DESC;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>How many live sessions a driver holds. D-03 makes the answer 0 or 1, always.</summary>
    public async Task<int> ActiveSessionCountAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM trips.sessions WHERE driver_id = @DriverId AND state = '{SessionStates.Active}';",
            new { DriverId = driverId });
    }

    /// <summary>The rows trip-state-svc queued for <c>trip.events</c> (migration 0505).</summary>
    public async Task<IReadOnlyList<(string EventType, Guid AggregateId, string Payload)>> OutboxAsync(Guid vehicleId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string, Guid, string)>(
            """
            SELECT event_type, aggregate_id, payload::text FROM trips.outbox
             WHERE aggregate_id = @VehicleId ORDER BY id;
            """,
            new { VehicleId = vehicleId });

        return [.. rows];
    }

    /// <summary>The kinds written to <c>trips.events</c> (0502) for a session, newest first.</summary>
    public async Task<IReadOnlyList<string>> TripEventKindsAsync(Guid sessionId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<string>(
            "SELECT kind FROM trips.events WHERE session_id = @SessionId ORDER BY ts DESC, id DESC;",
            new { SessionId = sessionId });

        return [.. rows];
    }

    /// <summary>The session published into <c>lock:session:{driverId}</c> for the other planes (D-03).</summary>
    public async Task<string?> PublishedSessionAsync(Guid driverId)
    {
        var redis = Services.GetRequiredService<IConnectionMultiplexer>();
        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.DriverSession(driverId));

        return value.IsNullOrEmpty ? null : value.ToString();
    }

    /// <summary>Ages a session's clocks so a timer fires without waiting half an hour.</summary>
    public async Task AgeSessionAsync(Guid sessionId, TimeSpan by)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            """
            UPDATE trips.sessions
               SET started_at = started_at - @By,
                   last_movement_at = COALESCE(last_movement_at, started_at) - @By,
                   last_position_at = last_position_at - @By,
                   ended_at = ended_at - @By,
                   offline_since = offline_since - @By
             WHERE id = @SessionId;
            """,
            new { SessionId = sessionId, By = by });
    }

    /// <summary>Puts a vehicle at a point, as the position consumer would from a fix.</summary>
    public Task<bool> ReportPositionAsync(Guid vehicleId, GeoPoint point, double? speedMps = null) =>
        Services.GetRequiredService<SessionPositionConsumer>()
            .ApplyAsync(
                new VehicleFix(vehicleId, point, DateTimeOffset.UtcNow, speedMps), CancellationToken.None);

    /// <summary>Every rating recorded against a session.</summary>
    public async Task<IReadOnlyList<(Guid RaterId, Guid RateeId, short Stars, string Direction)>> RatingsAsync(
        Guid sessionId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(Guid, Guid, short, string)>(
            @"SELECT rater_id, ratee_id, stars, direction FROM trips.ratings
               WHERE subject_kind = 'session' AND subject_id = @SessionId
               ORDER BY created_at, id;",
            new { SessionId = sessionId });

        return [.. rows];
    }

    /// <summary>When the broker's last will marked this session's vehicle away (R-15, T-04).</summary>
    public async Task<DateTimeOffset?> OfflineSinceAsync(Guid sessionId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            "SELECT offline_since FROM trips.sessions WHERE id = @SessionId;",
            new { SessionId = sessionId });
    }

    /// <summary>Runs one sweep pass, as the durable timers would.</summary>
    public Task<SweepResult> SweepAsync() =>
        Services.GetRequiredService<SessionSweepWorker>().SweepOnceAsync(CancellationToken.None);

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Starts a session and returns the 201 body, failing the test on anything else.</summary>
    public async Task<JsonElement> StartAsync(
        string bearer, Guid vehicleId, string mode = OperatingModes.A, bool autoEndAtDestination = false)
    {
        var response = await PostAsync(
            "/v1/sessions/start",
            new { vehicleId = vehicleId.ToString(), mode, autoEndAtDestination },
            bearer);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"start returned {response.StatusCode}: {text}");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static void Authorize(HttpRequestMessage request, string? bearer)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
    }
}
