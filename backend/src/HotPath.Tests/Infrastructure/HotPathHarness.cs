using MageRide.Fanout;
using MageRide.HotPath.MqttBridge;
using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.PositionProcessor;
using MageRide.Shared.Realtime;
using MageRide.TestKit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using StackExchange.Redis;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>Which parts of the pipeline a test wants running.</summary>
/// <remarks>
/// Everything is off unless a test asks for it, for the same reason C023's harness runs its workers
/// off by default: a background consumer or pump running underneath an assertion makes "the frame
/// arrived because the pipeline delivered it" indistinguishable from "something delivered it".
/// </remarks>
internal sealed record HotPathHarnessOptions
{
    /// <summary>How many mqtt-bridge replicas to run. Two is the E-08 case.</summary>
    public int BridgeReplicas { get; init; }

    /// <summary>Run position-processor-svc's <c>telemetry.raw</c> consumer.</summary>
    public bool Processor { get; init; }

    /// <summary>Run fanout-svc, with or without its pump.</summary>
    public bool Fanout { get; init; }

    /// <summary>Run fanout-svc's cell-stream pump.</summary>
    public bool FanoutPump { get; init; }

    /// <summary>How often the pump drains. Tests that are not timing the SLO shorten it.</summary>
    public TimeSpan? BatchInterval { get; init; }

    /// <summary>Frames replayed to a joining connection. 0 keeps a test's assertions to deltas.</summary>
    public int JoinSeedFrames { get; init; }

    /// <summary>Group-membership hysteresis. Tests that assert on it shorten it.</summary>
    public TimeSpan? LeaveHysteresis { get; init; }

    /// <summary>Also consume <c>veh/+/pos/replay</c>.</summary>
    public bool ConsumeReplay { get; init; } = true;
}

/// <summary>
/// A running mqtt-bridge (or two), position-processor and fanout-svc, against a real EMQX, a real
/// Redpanda and a real Redis.
/// </summary>
/// <remarks>
/// <para>
/// Each is built through its own <c>*Application.Build</c>, so the pipelines under test are the
/// ones the processes run — including the <c>access_token</c> query-string authentication, the
/// manual MQTT acknowledgement and the res-7 cell validation, none of which a hand-assembled host
/// would exercise.
/// </para>
/// <para>
/// <b>Every test gets its own Kafka consumer group and its own vehicle ids.</b> The containers are
/// shared across the collection and the TestKit does not reset them, so isolation is by namespace
/// rather than by teardown — and the processor is pointed at <c>StartFromEarliest</c> so a test
/// never races its consumer's group assignment. That knob is the one deliberate difference from a
/// deployed processor, and <c>PositionProcessorOptions</c> says why it exists.
/// </para>
/// </remarks>
internal sealed class HotPathHarness : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<MqttBridgeWorker> _bridges = [];
    private readonly RedisFixture _redis;

    private WebApplication? _fanoutApp;

    private HotPathHarness(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis, TestTokenIssuer tokens)
    {
        Emqx = emqx;
        Redpanda = redpanda;
        _redis = redis;
        Tokens = tokens;
    }

    public EmqxFixture Emqx { get; }

    public RedpandaFixture Redpanda { get; }

    public TestTokenIssuer Tokens { get; }

    /// <summary>The mqtt-bridge replicas, in the order they were started.</summary>
    public IReadOnlyList<MqttBridgeWorker> Bridges => _bridges;

    /// <summary>fanout-svc's base address, e.g. <c>http://127.0.0.1:41234</c>.</summary>
    public string FanoutBaseAddress => _fanoutApp is null
        ? throw new InvalidOperationException("This harness did not start fanout-svc.")
        : BaseAddressOf(_fanoutApp);

    public IServiceProvider FanoutServices => _fanoutApp?.Services
        ?? throw new InvalidOperationException("This harness did not start fanout-svc.");

    public static async Task<HotPathHarness> StartAsync(
        EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis, HotPathHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        emqx.RequireAvailable();
        redpanda.RequireAvailable();
        redis.RequireAvailable();

        // Created explicitly rather than left to auto-create: a topic nobody declared would come up
        // with one partition, which silently changes the ordering guarantee under test.
        await redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.TelemetryRaw);
        await redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.TelemetryNormalized);

        var harness = new HotPathHarness(emqx, redpanda, redis, new TestTokenIssuer());
        var group = $"hotpath-{Guid.NewGuid():N}";

        for (var replica = 0; replica < options.BridgeReplicas; replica++)
        {
            await harness.StartBridgeAsync(options, replica);
        }

        if (options.Processor)
        {
            await harness.StartProcessorAsync(group);
        }

        if (options.Fanout)
        {
            await harness.StartFanoutAsync(options);
        }

        return harness;
    }

    /// <summary>
    /// Opens a passenger's SignalR connection to <c>/hubs/live</c> over a real WebSocket.
    /// </summary>
    /// <remarks>
    /// The access token goes in the query string, which is SignalR's convention and unavoidable —
    /// a browser <c>WebSocket</c> cannot set an <c>Authorization</c> header (<c>signalr-hub.md</c>
    /// §1). Passing it any other way here would not test the path the apps use.
    /// </remarks>
    public HubConnection PassengerConnection(Guid? passengerId = null, string? accessToken = null)
    {
        var token = accessToken ?? Tokens.Passenger(passengerId ?? Guid.NewGuid());

        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(new Uri(FanoutBaseAddress), LiveHub.Path),
                connection =>
                {
                    connection.Transports = HttpTransportType.WebSockets;
                    connection.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();
    }

    /// <summary>An admin-capable multiplexer, for the assertions that read Redis directly.</summary>
    public async Task<ConnectionMultiplexer> ConnectRedisAsync()
    {
        var config = ConfigurationOptions.Parse(_redis.ConnectionString);
        config.AllowAdmin = true;

        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    /// <summary>Waits until every bridge replica holds its subscription.</summary>
    /// <remarks>
    /// Not a sleep: a publish that beats the subscription is simply not delivered, and the test
    /// would fail as "no ingest" rather than as "started too early".
    /// </remarks>
    public async Task WaitForBridgesAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            if (_bridges.Count > 0 && _bridges.TrueForAll(bridge => bridge.IsSubscribed))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Only {_bridges.Count(bridge => bridge.IsSubscribed)} of {_bridges.Count} bridge replicas subscribed.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(10));
                await app.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: could not stop a harness service: {ex.Message}");
            }
        }
    }

    private async Task StartBridgeAsync(HotPathHarnessOptions options, int replica)
    {
        var app = MqttBridgeApplication.Build(
            NewOptions(),
            builder => Configure(builder, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kafka:BootstrapServers"] = Redpanda.BootstrapServers,
                ["Mqtt:Host"] = Emqx.Host,
                ["Mqtt:Port"] = Emqx.Port.ToString(),
                ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
                ["MqttBridge:Enabled"] = "true",
                ["MqttBridge:ConsumeReplay"] = options.ConsumeReplay ? "true" : "false",
                ["MqttBridge:ClientIdPrefix"] = $"test-bridge-{replica}",
                // A failed CONNECT should surface as a test failure inside its timeout, not as a
                // minute of exponential backoff.
                ["MqttBridge:ReconnectDelayMin"] = "00:00:00.250",
                ["MqttBridge:ReconnectDelayMax"] = "00:00:02",
                ["Otel:PrometheusEnabled"] = "false",
            }));

        await app.StartAsync();

        _apps.Add(app);
        _bridges.Add(app.Services.GetRequiredService<MqttBridgeWorker>());
    }

    private async Task StartProcessorAsync(string group)
    {
        var app = PositionProcessorApplication.Build(
            NewOptions(),
            builder => Configure(builder, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kafka:BootstrapServers"] = Redpanda.BootstrapServers,
                ["ConnectionStrings:Redis"] = _redis.ConnectionString,
                ["PositionProcessor:Enabled"] = "true",
                ["PositionProcessor:ConsumerGroup"] = group,
                ["PositionProcessor:StartFromEarliest"] = "true",
                ["Otel:PrometheusEnabled"] = "false",
            }));

        await app.StartAsync();
        _apps.Add(app);
    }

    private async Task StartFanoutAsync(HotPathHarnessOptions options)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Redis"] = _redis.ConnectionString,
            ["Jwt:JwksUrl"] = "http://127.0.0.1:1/.well-known/jwks.json",
            ["Jwt:Issuer"] = Tokens.IssuerName,
            ["Jwt:RequireHttpsMetadata"] = "false",
            ["Fanout:PumpEnabled"] = options.FanoutPump ? "true" : "false",
            ["Fanout:JoinSeedFrames"] = options.JoinSeedFrames.ToString(),
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (options.BatchInterval is { } interval)
        {
            settings["Fanout:BatchInterval"] = interval.ToString();
        }

        if (options.LeaveHysteresis is { } hysteresis)
        {
            settings["Fanout:LeaveHysteresis"] = hysteresis.ToString();
        }

        var app = FanoutApplication.Build(
            NewOptions(),
            builder =>
            {
                Configure(builder, settings);

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

        _apps.Add(app);
        _fanoutApp = app;
    }

    private static WebApplicationOptions NewOptions() => new()
    {
        EnvironmentName = Environments.Development,
        ContentRootPath = AppContext.BaseDirectory,
    };

    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string?> overrides)
    {
        // MAGERIDE_TEST_LOGS=1 keeps the console provider when a failure needs a trace.
        if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
        {
            builder.Logging.ClearProviders();
        }

        builder.Configuration.AddInMemoryCollection(overrides);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
    }

    private static string BaseAddressOf(WebApplication app) =>
        // Fully qualified: StackExchange.Redis has its own IServer, and this file needs both.
        app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
