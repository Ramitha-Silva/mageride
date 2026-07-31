using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.TestKit;
using MageRide.Voip.Signalling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Voip.Tests.Infrastructure;

/// <summary>A <c>comms.call_log</c> row, as this suite reads one back.</summary>
public sealed record CallLogRow(
    Guid Id, Guid? RideId, Guid? CallerId, string CalleeRole, string CallType,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Outcome);

/// <summary>A <c>comms.voip_sessions</c> row.</summary>
public sealed record VoipSessionRow(
    Guid Id, Guid RideId, string LivekitRoom, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// Records the rooms voip-svc asked LiveKit to close, instead of talking to an SFU.
/// </summary>
/// <remarks>
/// <b>What is under test is the decision, not LiveKit.</b> Whether a real SFU honours
/// <c>DeleteRoom</c> is LiveKit's contract; whether this service issues it exactly when the ride
/// ends is D6' §6's requirement and is what the suite asserts. <see cref="LiveKitRoomService"/>'s
/// own wire shape — the Twirp path, the admin token, the 404-is-success rule — is covered by
/// <c>LiveKitRoomServiceTests</c> against a stub server.
/// </remarks>
internal sealed class RecordingRoomService : ILiveKitRoomService
{
    public ConcurrentQueue<string> Closed { get; } = new();

    public bool IsConfigured => true;

    public Task<bool> CloseRoomAsync(string roomName, CancellationToken cancellationToken)
    {
        Closed.Enqueue(roomName);

        return Task.FromResult(true);
    }
}

/// <summary>
/// A running voip-svc on a real socket, against a real Postgres and a real Redpanda.
/// </summary>
/// <remarks>
/// Built through <see cref="VoipApplication.Build"/>, so the pipeline under test — the bearer
/// handler, the problem+json handler, the options validation, the idempotency middleware and the
/// <c>ride.events</c> consumer — is the one the process runs.
/// </remarks>
internal sealed class VoipHarness : IAsyncDisposable
{
    /// <summary>Asserted against on the decoded token, so both are constants.</summary>
    public const string LiveKitApiKey = "c055-livekit-key";

    public const string LiveKitApiSecret = "c055-livekit-secret-not-a-secret";

    public const string WsUrl = "wss://voip.mageride.test";

    private readonly WebApplication _app;
    private readonly PostgresFixture _postgres;
    private readonly RedpandaFixture? _redpanda;

    private VoipHarness(
        WebApplication app,
        PostgresFixture postgres,
        RedpandaFixture? redpanda,
        TestTokenIssuer tokens,
        VoipSeed seed,
        RecordingRoomService rooms)
    {
        _app = app;
        _postgres = postgres;
        _redpanda = redpanda;

        Tokens = tokens;
        Seed = seed;
        Rooms = rooms;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(120) };
    }

    public HttpClient Client { get; }

    public TestTokenIssuer Tokens { get; }

    public VoipSeed Seed { get; }

    public RecordingRoomService Rooms { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<VoipHarness> StartAsync(
        PostgresFixture postgres,
        RedpandaFixture? redpanda = null,
        IDictionary<string, string?>? settings = null,
        bool withLiveKit = true)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        postgres.RequireAvailable();
        await postgres.EnsureMigratedAsync();
        await ResetAsync(postgres);

        redpanda?.RequireAvailable();

        var tokens = new TestTokenIssuer();
        var rooms = new RecordingRoomService();

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Postgres"] = postgres.ConnectionString,
            // The container is plain Postgres, not PgBouncer.
            ["Postgres:PgBouncerTransactionMode"] = "false",
            // Never fetched — the bearer handler is pointed at the test key below. The kernel's auth
            // wiring binds the setting all the same, so it has to be present and parseable.
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",

            ["Voip:LiveKit:WsUrl"] = withLiveKit ? WsUrl : null,
            ["Voip:LiveKit:ApiKey"] = withLiveKit ? LiveKitApiKey : null,
            ["Voip:LiveKit:ApiSecret"] = withLiveKit ? LiveKitApiSecret : null,
            // The room service is replaced below; the URL only decides which implementation the
            // registration would pick, and the test double is registered ahead of it either way.
            ["Voip:LiveKit:ApiUrl"] = "http://127.0.0.1:1/",

            ["Voip:RoomTeardownEnabled"] = redpanda is not null ? "true" : "false",
            ["Voip:ConsumerGroup"] = $"voip-test-{Guid.NewGuid():N}",

            ["urls"] = "http://127.0.0.1:0",
            // One /metrics endpoint per harness would collide across concurrently running tests.
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (redpanda is not null)
        {
            overrides["Kafka:BootstrapServers"] = redpanda.BootstrapServers;
        }

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                overrides[key] = value;
            }
        }

        var app = VoipApplication.Build(
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

                // Ahead of AddVoipServices's TryAdd, so the recorder wins.
                builder.Services.TryAddSingleton<ILiveKitRoomService>(rooms);

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

        return new VoipHarness(app, postgres, redpanda, tokens, new VoipSeed(postgres), rooms);
    }

    // -----------------------------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------------------------

    public async Task<HttpResponseMessage> PostAsync(string path, object? body, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        return await Client.SendAsync(request);
    }

    public async Task<T> PostAsync<T>(string path, object? body, string bearer)
    {
        using var response = await PostAsync(path, body, bearer);

        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"POST {path} answered {(int)response.StatusCode}: {payload}");

        return JsonSerializer.Deserialize<T>(payload, MageRideJson.Options)!;
    }

    // -----------------------------------------------------------------------------------------
    // Kafka
    // -----------------------------------------------------------------------------------------

    /// <summary>Publishes a <c>ride.events</c> message exactly as ride-svc's outbox dispatcher does.</summary>
    public async Task PublishRideEventAsync(Guid rideId, string eventType)
    {
        ArgumentNullException.ThrowIfNull(_redpanda);

        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers }).Build();

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventType,
            payload = new { rideId = rideId.ToString() },
        });

        await producer.ProduceAsync(EventTopics.RideEvents, new Message<string, byte[]>
        {
            Key = rideId.ToString(),
            Value = payload,
            // The dispatcher puts eventType on the header; the consumer reads the key, but the
            // shape has to be the real one or the test proves nothing about the real producer.
            Headers = [new Header("eventType", Encoding.UTF8.GetBytes(eventType))],
        });

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    // -----------------------------------------------------------------------------------------
    // Rows
    // -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CallLogRow>> CallLogAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<CallLogRow>(
            """
            SELECT id, ride_id AS RideId, caller_id AS CallerId, callee_role AS CalleeRole,
                   call_type AS CallType, started_at AS StartedAt, ended_at AS EndedAt, outcome
              FROM comms.call_log
             WHERE ride_id = @RideId
             ORDER BY started_at, id;
            """,
            new { RideId = rideId });

        return [.. rows];
    }

    public async Task<IReadOnlyList<VoipSessionRow>> SessionsAsync(Guid rideId)
    {
        await using var connection = await _postgres.OpenAsync();

        var rows = await connection.QueryAsync<VoipSessionRow>(
            """
            SELECT id, ride_id AS RideId, livekit_room AS LivekitRoom,
                   started_at AS StartedAt, ended_at AS EndedAt
              FROM comms.voip_sessions
             WHERE ride_id = @RideId
             ORDER BY started_at, id;
            """,
            new { RideId = rideId });

        return [.. rows];
    }

    public async Task SetRideStateAsync(Guid rideId, string state)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE rides.rides SET state = @State WHERE id = @RideId;",
            new { RideId = rideId, State = state });
    }

    /// <summary>Runs arbitrary SQL, for the constraint assertions migration 1311 is judged on.</summary>
    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = await _postgres.OpenAsync();

        await connection.ExecuteAsync(sql, parameters);
    }

    private static async Task ResetAsync(PostgresFixture postgres)
    {
        await using var connection = await postgres.OpenAsync();

        // Ordered by dependency: call_log and voip_sessions point at rides, rides at users.
        await connection.ExecuteAsync(
            """
            DELETE FROM comms.call_log;
            DELETE FROM comms.voip_sessions;
            DELETE FROM comms.command_log;
            DELETE FROM rides.rides;
            DELETE FROM registry.vehicles;
            DELETE FROM iam.users;
            """);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
