using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MageRide.Fanout.Realtime;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using StackExchange.Redis;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>Which parts of fanout-svc a test wants running.</summary>
/// <remarks>
/// Everything that could push under an assertion is off unless a test asks for it: the pumps are
/// resolvable either way, so a test that wants determinism steps <see cref="FanoutHarness.CellsAsync"/>
/// itself rather than racing a two-second loop.
/// </remarks>
internal sealed record FanoutHarnessOptions
{
    /// <summary>Run the two position pumps as hosted services.</summary>
    public bool Pump { get; init; }

    /// <summary>How often they drain. Tests that are not timing the SLO shorten it.</summary>
    public TimeSpan? BatchInterval { get; init; }

    /// <summary>Frames replayed to a joining connection. 0 keeps a test's assertions to deltas.</summary>
    public int JoinSeedFrames { get; init; }

    /// <summary>Group-membership hysteresis. Tests that assert on it shorten it.</summary>
    public TimeSpan? LeaveHysteresis { get; init; }

    /// <summary>US-7.17's window. Shortened where a test is waiting for a vehicle to go stale.</summary>
    public TimeSpan? FreshnessWindow { get; init; }

    /// <summary>Consume <c>ride.events</c> and <c>registry.events</c>.</summary>
    public bool Events { get; init; } = true;

    /// <summary>Hold the EMQX <c>veh/+/status</c> subscription (US-7.17's <c>offline</c> half).</summary>
    public bool Presence { get; init; }

    /// <summary>Broadcast directed sends over <c>fanout:control</c> rather than applying them locally.</summary>
    public bool ControlPlane { get; init; } = true;

    /// <summary>
    /// The Kafka consumer group. Shared deliberately by a test that starts two replicas, which is
    /// the only way to prove a signal crossed between them.
    /// </summary>
    public string? ConsumerGroup { get; init; }
}

/// <summary>
/// One or more fanout-svc replicas against a real Redis, a real Redpanda and a real EMQX.
/// </summary>
/// <remarks>
/// Each replica is built through <c>FanoutApplication.Build</c>, so the pipeline under test is the
/// one the process runs — the <c>access_token</c> query authentication, the res-7 validation, the
/// control-channel subscription and the JSON hub protocol included. A hub method invoked in-process
/// would exercise none of them.
/// </remarks>
internal sealed class FanoutHarness : IAsyncDisposable
{
    private readonly List<WebApplication> _replicas = [];
    private readonly RedisFixture _redis;
    private readonly RedpandaFixture _redpanda;
    private readonly EmqxFixture _emqx;
    private readonly FanoutHarnessOptions _options;
    private readonly List<IAsyncDisposable> _disposables = [];

    private IProducer<string, byte[]>? _producer;
    private ConnectionMultiplexer? _multiplexer;

    private FanoutHarness(
        RedisFixture redis, RedpandaFixture redpanda, EmqxFixture emqx, FanoutHarnessOptions options)
    {
        _redis = redis;
        _redpanda = redpanda;
        _emqx = emqx;
        _options = options;
        Tokens = new TestTokenIssuer();
    }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The first replica — the one a single-replica test means by "the service".</summary>
    public IServiceProvider Services => _replicas[0].Services;

    /// <summary>Every replica's services, in start order.</summary>
    public IReadOnlyList<IServiceProvider> Replicas => [.. _replicas.Select(replica => replica.Services)];

    /// <summary>Writes positions the way position-processor-svc does.</summary>
    public PositionWriter Positions { get; private set; } = null!;

    /// <summary>An admin-capable multiplexer, for the assertions that read Redis directly.</summary>
    public IConnectionMultiplexer Redis => _multiplexer
        ?? throw new InvalidOperationException("The harness has not connected to Redis.");

    public static async Task<FanoutHarness> StartAsync(
        RedisFixture redis,
        RedpandaFixture redpanda,
        EmqxFixture emqx,
        FanoutHarnessOptions options,
        int replicas = 1)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(emqx);
        ArgumentNullException.ThrowIfNull(options);

        redis.RequireAvailable();

        if (options.Events)
        {
            redpanda.RequireAvailable();

            // Created explicitly rather than left to auto-create: an auto-created topic comes up
            // with one partition, which silently changes the ordering guarantee under test.
            await redpanda.CreateTopicAsync(EventTopics.RideEvents);
            await redpanda.CreateTopicAsync(EventTopics.RegistryEvents);
        }

        if (options.Presence)
        {
            emqx.RequireAvailable();
        }

        var harness = new FanoutHarness(redis, redpanda, emqx, options);

        var configuration = ConfigurationOptions.Parse(redis.ConnectionString);
        configuration.AllowAdmin = true;
        harness._multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        harness.Positions = new PositionWriter(harness._multiplexer);

        var group = options.ConsumerGroup ?? $"fanout-{Guid.NewGuid():N}";

        for (var replica = 0; replica < replicas; replica++)
        {
            await harness.StartReplicaAsync(group);
        }

        return harness;
    }

    /// <summary>Opens a passenger's SignalR connection to <c>/hubs/live</c> over a real WebSocket.</summary>
    /// <remarks>
    /// The access token goes in the query string, which is SignalR's convention and unavoidable —
    /// a browser <c>WebSocket</c> cannot set an <c>Authorization</c> header (<c>signalr-hub.md</c>
    /// §1). Passing it any other way here would not test the path the apps use.
    /// </remarks>
    public HubConnection Passenger(Guid userId, int replica = 0) =>
        Connect(Tokens.Passenger(userId), replica);

    /// <summary>Opens a driver's connection — a token carrying the <c>driver</c> role (AL-31).</summary>
    public HubConnection Driver(Guid userId, int replica = 0) => Connect(Tokens.Driver(userId), replica);

    public HubConnection Connect(string accessToken, int replica = 0) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(new Uri(BaseAddressOf(_replicas[replica])), Contract.Path),
                connection =>
                {
                    connection.Transports = HttpTransportType.WebSockets;
                    connection.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                })
            .Build();

    /// <summary>Runs one cell-pump tick on every replica, in start order.</summary>
    public async Task CellsAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.Services.GetRequiredService<CellStreamPump>().TickAsync(CancellationToken.None);
        }
    }

    /// <summary>Runs one vehicle/ride-pump tick on every replica.</summary>
    public async Task VehiclesAsync()
    {
        foreach (var replica in _replicas)
        {
            await replica.Services.GetRequiredService<VehicleStreamPump>().TickAsync(CancellationToken.None);
        }
    }

    /// <summary>Both pumps, everywhere. What a test means by "let the service push".</summary>
    public async Task PumpAsync()
    {
        await CellsAsync();
        await VehiclesAsync();
    }

    /// <summary>
    /// Produces one row onto a topic exactly as the outbox dispatcher would — the payload verbatim
    /// as the value, the aggregate id as the key, and the type in an <c>eventType</c> header.
    /// </summary>
    public async Task PublishAsync(string topic, Guid aggregateId, string eventType, object payload)
    {
        _producer ??= new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = _redpanda.BootstrapServers }).Build();

        var message = new Message<string, byte[]>
        {
            Key = aggregateId.ToString(),
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, MageRideJson.StorageOptions)),
            Headers = [],
        };

        message.Headers.Add("eventType", Encoding.UTF8.GetBytes(eventType));

        await _producer.ProduceAsync(topic, message);
    }

    /// <summary>
    /// Publishes a retained <c>veh/{vehicleId}/status</c>, which is what an EMQX last will looks
    /// like to a subscriber (R-15, T-04).
    /// </summary>
    public async Task PublishStatusAsync(Guid vehicleId, string status)
    {
        var tokens = new MqttSessionTokenIssuer(
            Microsoft.Extensions.Options.Options.Create(new MqttOptions
            {
                Host = _emqx.Host,
                Port = _emqx.Port,
                SessionTokenSecret = EmqxFixture.SessionTokenSecret,
            }),
            TimeProvider.System);

        var credential = tokens.IssueForService("test-status");
        var client = new MqttClientFactory().CreateMqttClient();

        var connected = await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(_emqx.Host, _emqx.Port)
                .WithClientId($"mageride-test-status-{Guid.NewGuid():N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build(),
            CancellationToken.None);

        if (connected.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"EMQX refused the status CONNECT: {connected.ResultCode}.");
        }

        await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.Status(vehicleId))
                .WithPayload(status)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build(),
            CancellationToken.None);

        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        client.Dispose();
    }

    /// <summary>Waits until <paramref name="condition"/> holds, or fails the test.</summary>
    public static async Task WaitAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting: {because}.");
    }

    /// <summary>Waits for the presence subscription of every replica that holds one.</summary>
    public async Task WaitForPresenceAsync()
    {
        var workers = _replicas
            .Select(replica => replica.Services.GetService<PresenceWorker>())
            .OfType<PresenceWorker>()
            .ToArray();

        await WaitAsync(
            () => workers.Length > 0 && Array.TrueForAll(workers, worker => worker.IsSubscribed),
            "every replica's EMQX presence subscription should be live");
    }

    public async ValueTask DisposeAsync()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();

        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        foreach (var replica in _replicas)
        {
            try
            {
                await replica.StopAsync(TimeSpan.FromSeconds(10));
                await replica.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: could not stop a fanout replica: {ex.Message}");
            }
        }

        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }
    }

    private async Task StartReplicaAsync(string group)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Redis"] = _redis.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = Tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fanout:PumpEnabled"] = _options.Pump ? "true" : "false",
            ["Fanout:JoinSeedFrames"] = _options.JoinSeedFrames.ToString(),
            ["Fanout:EventsEnabled"] = _options.Events ? "true" : "false",
            ["Fanout:PresenceEnabled"] = _options.Presence ? "true" : "false",
            ["Fanout:ControlPlaneEnabled"] = _options.ControlPlane ? "true" : "false",
            ["Fanout:ConsumerGroup"] = group,
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (_options.Events)
        {
            settings["Kafka:BootstrapServers"] = _redpanda.BootstrapServers;
        }

        if (_options.Presence)
        {
            settings["Mqtt:Host"] = _emqx.Host;
            settings["Mqtt:Port"] = _emqx.Port.ToString();
            settings["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret;
        }

        if (_options.BatchInterval is { } interval)
        {
            settings["Fanout:BatchInterval"] = interval.ToString();
        }

        if (_options.LeaveHysteresis is { } hysteresis)
        {
            settings["Fanout:LeaveHysteresis"] = hysteresis.ToString();
        }

        if (_options.FreshnessWindow is { } freshness)
        {
            settings["Fanout:FreshnessWindow"] = freshness.ToString();
        }

        var app = FanoutApplication.Build(
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

                builder.Configuration.AddInMemoryCollection(settings);
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                // PostConfigure so this runs after the kernel's AddMageRideAuth and after
                // FanoutApplication's own access_token hook — everything else about validation,
                // including that hook, is left exactly as the service configured it, because that
                // is what is under test.
                builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                    .PostConfigure(bearer =>
                    {
                        bearer.ConfigurationManager = null;
                        bearer.TokenValidationParameters.IssuerSigningKey = Tokens.PublicKey;
                        bearer.TokenValidationParameters.IssuerSigningKeyResolver = null;
                    });
            });

        await app.StartAsync();

        _replicas.Add(app);
    }

    private static string BaseAddressOf(WebApplication app) =>
        // Fully qualified: StackExchange.Redis has its own IServer, and this file needs both.
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
