using MageRide.Fanout;
using MageRide.HotPath.MqttBridge;
using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.PersistenceWriter;
using MageRide.HotPath.PersistenceWriter.Ingest;
using MageRide.HotPath.PersistenceWriter.Summaries;
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

    /// <summary>Hold the backlog to T-05's per-device rate (C038).</summary>
    public bool ThrottleReplay { get; init; } = true;

    /// <summary>T-05's per-device backlog rate. 20/s is the spec'd value.</summary>
    public int ReplaySamplesPerSecond { get; init; } = 20;

    /// <summary>Watch <c>pos/live</c> for D-17's ceiling and raise <c>mqtt.rate_violation</c>.</summary>
    public bool MonitorPublishRate { get; init; }

    /// <summary>
    /// How long the D-17 monitor waits before folding a closed second into Redis. Shortened from
    /// the deployed 500 ms only where a test is waiting on the fold itself.
    /// </summary>
    public TimeSpan? RateFlushInterval { get; init; }

    /// <summary>Run persistence-writer-svc's <c>telemetry.normalized</c> batch writer (C040).</summary>
    public bool Writer { get; init; }

    /// <summary>Run its <c>trip.events</c> consumer, which writes the trip summaries.</summary>
    public bool Summaries { get; init; }

    /// <summary>Rows per <c>COPY</c> batch. Shortened where a test is waiting on a flush.</summary>
    public int? BatchRows { get; init; }

    /// <summary>How long a partially-filled batch waits. ADD §9.5 ships 500 ms.</summary>
    public TimeSpan? FlushInterval { get; init; }

    /// <summary>
    /// The writer's consumer group. Shared deliberately by the two halves of the restart test, which
    /// is the only way to prove an offset survived a process death.
    /// </summary>
    public string? WriterConsumerGroup { get; init; }
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
    private readonly PostgresFixture? _postgres;

    private WebApplication? _fanoutApp;
    private WebApplication? _writerApp;

    private HotPathHarness(
        EmqxFixture emqx,
        RedpandaFixture redpanda,
        RedisFixture redis,
        PostgresFixture? postgres,
        TestTokenIssuer tokens)
    {
        Emqx = emqx;
        Redpanda = redpanda;
        _redis = redis;
        _postgres = postgres;
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

    /// <summary>persistence-writer-svc's batch writer, when this harness started one.</summary>
    public TelemetryWriterWorker Writer => _writerApp?.Services.GetRequiredService<TelemetryWriterWorker>()
        ?? throw new InvalidOperationException("This harness did not start persistence-writer-svc.");

    /// <summary>Its trip-summary consumer, when this harness started one.</summary>
    public TripEventConsumer Summaries => _writerApp?.Services.GetRequiredService<TripEventConsumer>()
        ?? throw new InvalidOperationException("This harness did not start the summary consumer.");

    public static async Task<HotPathHarness> StartAsync(
        EmqxFixture emqx,
        RedpandaFixture redpanda,
        RedisFixture redis,
        HotPathHarnessOptions options,
        PostgresFixture? postgres = null)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        ArgumentNullException.ThrowIfNull(redpanda);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        emqx.RequireAvailable();
        redpanda.RequireAvailable();
        redis.RequireAvailable();

        if (options.Writer || options.Summaries)
        {
            ArgumentNullException.ThrowIfNull(postgres);
            postgres.RequireAvailable();
            await postgres.EnsureMigratedAsync();
        }

        // Created explicitly rather than left to auto-create: a topic nobody declared would come up
        // with one partition, which silently changes the ordering guarantee under test.
        await redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.TelemetryRaw);
        await redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.TelemetryNormalized);

        // D-17's mqtt.rate_violation lands here (C038). Declared for the same reason as the other
        // two: an auto-created topic comes up with one partition.
        await redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.AuditEvents);

        var harness = new HotPathHarness(emqx, redpanda, redis, postgres, new TestTokenIssuer());
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

        if (options.Writer || options.Summaries)
        {
            await harness.StartWriterAsync(options);
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
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kafka:BootstrapServers"] = Redpanda.BootstrapServers,
            // T-05's replay bucket and D-17's publish window are both cluster-wide counters, so the
            // bridge needs Redis as of C038.
            ["ConnectionStrings:Redis"] = _redis.ConnectionString,
            ["Mqtt:Host"] = Emqx.Host,
            ["Mqtt:Port"] = Emqx.Port.ToString(),
            ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
            ["MqttBridge:Enabled"] = "true",
            ["MqttBridge:ConsumeReplay"] = options.ConsumeReplay ? "true" : "false",
            ["MqttBridge:ThrottleReplay"] = options.ThrottleReplay ? "true" : "false",
            ["MqttBridge:ReplaySamplesPerSecond"] = options.ReplaySamplesPerSecond.ToString(),
            ["MqttBridge:MonitorPublishRate"] = options.MonitorPublishRate ? "true" : "false",
            ["MqttBridge:ClientIdPrefix"] = $"test-bridge-{replica}",
            // A failed CONNECT should surface as a test failure inside its timeout, not as a
            // minute of exponential backoff.
            ["MqttBridge:ReconnectDelayMin"] = "00:00:00.250",
            ["MqttBridge:ReconnectDelayMax"] = "00:00:02",
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (options.RateFlushInterval is { } flush)
        {
            settings["MqttBridge:RateFlushInterval"] = flush.ToString();
        }

        var app = MqttBridgeApplication.Build(NewOptions(), builder => Configure(builder, settings));

        await app.StartAsync();

        _apps.Add(app);
        _bridges.Add(app.Services.GetRequiredService<MqttBridgeWorker>());
    }

    /// <summary>
    /// Stops one bridge replica the way a rolling deploy would, and leaves the rest running.
    /// </summary>
    /// <remarks>
    /// The C038 claim under this is "graceful rebalance on replica loss with no duplicate ingest":
    /// a replica that unsubscribes, drains what it already took and only then disconnects hands
    /// nothing back to EMQX that <c>telemetry.raw</c> has already got.
    /// </remarks>
    public async Task StopBridgeAsync(int replica)
    {
        var app = _apps[replica];

        await app.StopAsync(TimeSpan.FromSeconds(30));
        await app.DisposeAsync();

        _apps.RemoveAt(replica);
        _bridges.RemoveAt(replica);
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

    /// <summary>
    /// Starts persistence-writer-svc, built through its own <c>Build</c> so the pipeline under test
    /// is the one the process runs — including the batching consumer, which is this component's
    /// whole difference from the kernel's per-message one.
    /// </summary>
    private async Task StartWriterAsync(HotPathHarnessOptions options)
    {
        await Redpanda.CreateTopicAsync(Shared.Messaging.EventTopics.TripEvents);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kafka:BootstrapServers"] = Redpanda.BootstrapServers,
            ["ConnectionStrings:Postgres"] = _postgres!.ConnectionString,
            ["PersistenceWriter:Enabled"] = options.Writer ? "true" : "false",
            ["PersistenceWriter:SummariesEnabled"] = options.Summaries ? "true" : "false",
            ["PersistenceWriter:ConsumerGroup"] =
                options.WriterConsumerGroup ?? $"writer-{Guid.NewGuid():N}",
            ["PersistenceWriter:TripConsumerGroup"] = $"writer-trips-{Guid.NewGuid():N}",
            ["Otel:PrometheusEnabled"] = "false",
        };

        if (options.BatchRows is { } rows)
        {
            settings["PersistenceWriter:BatchRows"] = rows.ToString();
        }

        if (options.FlushInterval is { } flush)
        {
            settings["PersistenceWriter:FlushInterval"] = flush.ToString();
        }

        var app = PersistenceWriterApplication.Build(NewOptions(), builder => Configure(builder, settings));

        await app.StartAsync();

        _apps.Add(app);
        _writerApp = app;
    }

    /// <summary>
    /// Stops persistence-writer-svc the way a pod kill does, leaving the rest of the pipeline running.
    /// </summary>
    /// <remarks>
    /// The DoD's third and fourth lines are both about this: a writer killed mid-batch has committed
    /// no offsets, and the live map keeps working while it is gone because the live map is Redis and
    /// this service cannot reach it.
    /// </remarks>
    public async Task StopWriterAsync()
    {
        if (_writerApp is null)
        {
            return;
        }

        _apps.Remove(_writerApp);

        await _writerApp.StopAsync(TimeSpan.FromSeconds(10));
        await _writerApp.DisposeAsync();

        _writerApp = null;
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

            // Δ C041: the visibility plane is off in this suite, and deliberately. What is under
            // test here is the *pipeline* — EMQX to a passenger's geocell group under five seconds —
            // and every claim it makes is about an idle Mode C vehicle, which C041's filter passes
            // through unchanged. The entitlement cache, the engagement marks and the last-will
            // subscription are asserted in Fanout.Api.Tests, where the events that drive them are.
            // Leaving them on would add a Kafka consumer and a broker session to a suite whose
            // failures should only ever be about ingest.
            ["Fanout:EventsEnabled"] = "false",
            ["Fanout:PresenceEnabled"] = "false",
            ["Fanout:ControlPlaneEnabled"] = "false",
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
