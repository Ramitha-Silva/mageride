using Testcontainers.Redpanda;

namespace MageRide.TestKit;

/// <summary>
/// A throwaway Redpanda broker — the Kafka-API event backbone every service produces to and
/// consumes from (D6' §2).
/// </summary>
/// <remarks>
/// <para>
/// Pinned to <c>v24.2.26</c>, the same tag <c>infra/docker-compose.dev.slim.yml</c> runs.
/// D7' §2.1/§9 and the replica both write <c>v24.2</c>, which is not a pullable tag — Redpanda
/// publishes only full patch versions (recorded in the C009 handoff).
/// </para>
/// <para>
/// Topics are NOT created here. A test that needs one creates exactly the topics it asserts
/// on, so a missing <c>bootstrap-topics.sh</c> entry cannot be masked by a fixture that
/// created everything; <see cref="Topics"/> lists the D6' §2.1 registry for the tests that do
/// want the full set.
/// </para>
/// </remarks>
public sealed class RedpandaFixture : ContainerFixture
{
    /// <summary>The image tag the dev stack runs.</summary>
    public const string Image = "redpandadata/redpanda:v24.2.26";

    /// <summary>The D6' §2.1 topic registry, in the order the bootstrap script creates them.</summary>
    public static readonly IReadOnlyList<string> Topics =
    [
        "telemetry.raw",
        "telemetry.normalized",
        "trip.events",
        "ride.events",
        "dispatch.events",
        "audit.events",
    ];

    private RedpandaContainer? _container;

    protected override string Name => "Redpanda";

    /// <summary>
    /// Kafka bootstrap address in the <c>host:port</c> form <c>Kafka__BootstrapServers</c>
    /// takes, e.g. <c>127.0.0.1:53411</c>.
    /// </summary>
    /// <remarks>
    /// The Testcontainers module hands back a URI (<c>plaintext://127.0.0.1:53411/</c>). That is
    /// not what a service's configuration holds — <c>.env.common.example</c> sets
    /// <c>Kafka__BootstrapServers=redpanda:9092</c> — so it is normalised here rather than in
    /// every consumer. <see cref="BootstrapUri"/> keeps the raw value.
    /// </remarks>
    public string BootstrapServers
    {
        get
        {
            var raw = BootstrapUri;
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                ? $"{uri.Host}:{uri.Port}"
                : raw;
        }
    }

    /// <summary>The module's raw bootstrap address, scheme and all.</summary>
    public string BootstrapUri => _container?.GetBootstrapAddress()
        ?? throw new InvalidOperationException(
            $"Redpanda container is not running: {SkipReason ?? "not started"}");

    protected override async Task StartAsync()
    {
        _container = new RedpandaBuilder(Image)
            .Build();

        await _container.StartAsync();
    }

    protected override Task StopAsync() =>
        _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a topic through <c>rpk</c> inside the container, with the replica's shape
    /// (3 partitions, RF=1 — lightweight-production-replica.md Container 3).
    /// </summary>
    public async Task CreateTopicAsync(string name, int partitions = 3, int replicas = 1)
    {
        RequireAvailable();

        var result = await _container!.ExecAsync(
        [
            "rpk", "topic", "create", name,
            "--partitions", partitions.ToString(),
            "--replicas", replicas.ToString(),
        ]);

        // Two callers racing both create; the loser gets TOPIC_ALREADY_EXISTS, which is
        // success here — same rule as infra/deploy/redpanda/bootstrap-topics.sh.
        var output = result.Stdout + result.Stderr;
        if (result.ExitCode != 0
            && !output.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"could not create topic '{name}': {output}");
        }
    }

    /// <summary>Creates the six D6' §2.1 topics.</summary>
    public async Task CreateRegistryTopicsAsync()
    {
        foreach (var topic in Topics)
        {
            await CreateTopicAsync(topic);
        }
    }

    /// <summary>Topic names the broker currently knows about.</summary>
    public async Task<IReadOnlyList<string>> ListTopicsAsync()
    {
        RequireAvailable();

        var result = await _container!.ExecAsync(["rpk", "topic", "list"]);

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1) // the NAME / PARTITIONS / REPLICAS header
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToArray();
    }
}

/// <summary>Collection sharing one <see cref="RedpandaFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class RedpandaCollection : ICollectionFixture<RedpandaFixture>
{
    public const string Name = "mageride-redpanda";
}
