using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Grpc.Core;
using Grpc.Net.Client;
using MageRide.Reputation.Grpc;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace MageRide.Reputation.Tests.Infrastructure;

/// <summary>
/// A running reputation-svc on a real socket, against a real Postgres and a real Redis.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="ReputationApplication.Build"/>, so the pipeline under test is the one
/// the process runs — including the pair of Kestrel endpoints, without which the gRPC half of this
/// component could not be reached at all.
/// </para>
/// <para>
/// <b>Background workers are off by default.</b> A sweep, a consumer or a detector pass running
/// underneath an assertion would make "the delisting expired" indistinguishable from "something
/// expired it"; the tests that need one turn exactly that one on, or drive a single pass by hand.
/// </para>
/// <para>
/// <b>The clock is a <see cref="FakeTimeProvider"/>.</b> Half of this component is time — a 30-day
/// rolling window, a 7-day delisting, a 30-minute brief delist, a daily detection window — and none
/// of it is assertable against a real clock without sleeping through it.
/// </para>
/// </remarks>
internal sealed class ReputationHarness : IAsyncDisposable
{
    /// <summary>Guards the gRPC service and <c>/v1/internal/**</c> until C042 lands a mesh.</summary>
    public const string InternalApiKey = "mageride-c033-test-internal-key";

    private static int _phoneCounter = Random.Shared.Next(1_000_000, 8_999_999);

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly GrpcChannel _channel;

    private ReputationHarness(WebApplication app, PostgresFixture postgres, TestTokenIssuer tokens, FakeTimeProvider clock)
    {
        _app = app;
        _postgres = postgres;
        Tokens = tokens;
        Clock = clock;

        // Two endpoints, in the order ReputationApplication binds them: HTTP/1.1 for the admin
        // routes, then the HTTP/2-only one for reputation.v1. They cannot be one socket — cleartext
        // has no ALPN to negotiate the protocol with — which is the whole reason D7' §4.2 gives
        // this service its own Grpc__ListenPort.
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();

        Client = new HttpClient { BaseAddress = new Uri(addresses[0]), Timeout = TimeSpan.FromSeconds(60) };

        _channel = GrpcChannel.ForAddress(addresses[^1]);
        Reputation = new Reputation.Grpc.Reputation.ReputationClient(_channel);
    }

    public HttpClient Client { get; }

    /// <summary>The generated <c>reputation.v1</c> client, talking over a real socket.</summary>
    public MageRide.Reputation.Grpc.Reputation.ReputationClient Reputation { get; }

    public TestTokenIssuer Tokens { get; }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The metadata the internal-key interceptor demands.</summary>
    public static Metadata InternalCallCredentials =>
        new() { { InternalKeyInterceptor.MetadataKey, InternalApiKey } };

    public static async Task<ReputationHarness> StartAsync(
        PostgresFixture postgres,
        RedisFixture redis,
        IDictionary<string, string?>? settings = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(redis);

        postgres.RequireAvailable();
        redis.RequireAvailable();
        await postgres.EnsureMigratedAsync();

        // The TestKit shares one container per collection and does not reset between tests. Every
        // test here mints fresh user ids, so the tables are left alone rather than truncated —
        // truncating reputation.counters would also destroy the E-07 detector's window for a test
        // running beside this one.
        var tokens = new TestTokenIssuer();

        // A fixed instant rather than "now": the detection window key is derived from the clock,
        // and a test that ran across midnight UTC would otherwise be flaky once a day.
        var clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero));

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
            ["Reputation:InternalApiKey"] = InternalApiKey,

            // 0 = an ephemeral port. D7' §4.2's fixed 5005 would collide the moment two test
            // classes ran at once; everything else about the endpoint is the deployed
            // configuration.
            ["Reputation:GrpcListenPort"] = "0",

            // Off unless a test asks. See the class remarks.
            ["Reputation:ConsumerEnabled"] = "false",
            ["Reputation:ExpiryWorkerEnabled"] = "false",
            ["Reputation:DetectorEnabled"] = "false",
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
                // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(overrides);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                builder.Services.AddSingleton<TimeProvider>(clock);

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

        return new ReputationHarness(app, postgres, tokens, clock);
    }

    // -----------------------------------------------------------------------------------------
    // Seeding. reputation-svc creates no accounts — iam-svc does.
    // -----------------------------------------------------------------------------------------

    /// <summary>Creates the <c>iam.users</c> row every foreign key in this schema needs.</summary>
    public async Task<Guid> CreateUserAsync(string role = "passenger")
    {
        var userId = Guid.NewGuid();

        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO iam.users (id, phone, role) VALUES (@Id, @Phone, @Role);",
            new { Id = userId, Phone = NextPhone(), Role = role });

        return userId;
    }

    public Task<Guid> CreateDriverAsync() => CreateUserAsync("driver");

    /// <summary>A device row as iam-svc's OTP flow would leave it (C020, migration 0105).</summary>
    public async Task BindDeviceAsync(Guid userId, string deviceKey)
    {
        await using var connection = await _postgres.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO iam.devices (user_id, platform, device_key)
            VALUES (@UserId, 'android', @DeviceKey);
            """,
            new { UserId = userId, DeviceKey = deviceKey });
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public Task<HttpResponseMessage> GetAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        return Client.SendAsync(request);
    }

    /// <summary>POSTs JSON with a fresh <c>Idempotency-Key</c> (D3' §0).</summary>
    public Task<HttpResponseMessage> PostAsync(
        string path, object? body, string? bearer, string? idempotencyKey = null, string? internalKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        Authorize(request, bearer, internalKey);

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PutAsync(string path, object? body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };

        Authorize(request, bearer, internalKey: null);

        return Client.SendAsync(request);
    }

    // -----------------------------------------------------------------------------------------
    // Reading what was written
    // -----------------------------------------------------------------------------------------

    public Task<NpgsqlConnection> OpenAsync() => _postgres.OpenAsync();

    public async Task<BlockStateSnapshot?> ReadBlockStateAsync(Guid userId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<BlockStateSnapshot>(
            """
            SELECT state AS State, expires_at AS ExpiresAt, source AS Source, reason AS Reason, set_by AS SetBy
              FROM reputation.block_states WHERE user_id = @UserId;
            """,
            new { UserId = userId });
    }

    public async Task<CounterSnapshot?> ReadCountersAsync(Guid userId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<CounterSnapshot>(
            """
            SELECT cancellations_continuous::int AS CancellationsContinuous,
                   reports_total AS ReportsTotal,
                   no_shows AS NoShows,
                   window_reset_at AS WindowStartedAt
              FROM reputation.counters WHERE user_id = @UserId;
            """,
            new { UserId = userId });
    }

    public async Task<int?> ReadLevelAsync(Guid driverId)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT level FROM dispatch.driver_levels WHERE driver_id = @DriverId;",
            new { DriverId = driverId });
    }

    /// <summary>
    /// Outbox rows this service wrote for one subject, oldest first, with the envelope parsed.
    /// </summary>
    /// <remarks>
    /// Parsed rather than string-matched: <c>payload</c> is <c>jsonb</c>, which is a parsed
    /// representation — Postgres re-orders the members and re-spaces the text, so
    /// <c>"state":"OK"</c> is never found in it however correct the event was.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxSnapshot>> ReadOutboxAsync(Guid aggregateId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<(string EventType, string Payload)>(
            """
            SELECT event_type AS EventType, payload::text AS Payload
              FROM reputation.outbox WHERE aggregate_id = @AggregateId ORDER BY id;
            """,
            new { AggregateId = aggregateId });

        return [.. rows.Select(row =>
        {
            using var document = JsonDocument.Parse(row.Payload);
            return new OutboxSnapshot(row.EventType, document.RootElement.Clone());
        })];
    }

    public async Task<IReadOnlyList<AuditSnapshot>> ReadAuditAsync(Guid entityId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<AuditSnapshot>(
            """
            SELECT actor_id AS ActorId, action AS Action, entity_type AS EntityType,
                   before::text AS Before, after::text AS After
              FROM audit.events WHERE entity_id = @EntityId ORDER BY id;
            """,
            new { EntityId = entityId });

        return [.. rows];
    }

    public async Task<int> CountFlagsAsync(Guid subjectId, string kind)
    {
        await using var connection = await _postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM reputation.fraud_flags WHERE subject_id = @SubjectId AND kind = @Kind;",
            new { SubjectId = subjectId, Kind = kind });
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _channel.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static void Authorize(HttpRequestMessage request, string? bearer, string? internalKey)
    {
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        if (internalKey is not null)
        {
            request.Headers.Add("X-MageRide-Internal-Key", internalKey);
        }
    }

    private static string NextPhone() =>
        "+9477" + (Interlocked.Increment(ref _phoneCounter) % 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
}

internal sealed record BlockStateSnapshot(
    string State, DateTimeOffset? ExpiresAt, string Source, string? Reason, Guid? SetBy);

internal sealed record CounterSnapshot(
    int CancellationsContinuous, int ReportsTotal, int NoShows, DateTimeOffset? WindowStartedAt);

internal sealed record OutboxSnapshot(string EventType, JsonElement Envelope)
{
    /// <summary>The <c>payload</c> member of the D6' §2.2 envelope.</summary>
    public JsonElement Payload => Envelope.GetProperty("payload");

    public string? String(string member) =>
        Payload.TryGetProperty(member, out var value) ? value.GetString() : null;
}

internal sealed record AuditSnapshot(
    Guid? ActorId, string Action, string? EntityType, string? Before, string? After);
