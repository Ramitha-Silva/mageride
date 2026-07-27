using Testcontainers.Redis;

namespace MageRide.TestKit;

/// <summary>
/// A throwaway Redis 7 — live geo index, dispatch and ride locks, rate-limit buckets, wallet
/// and entitlement caches, SignalR backplane (ADD §9.4).
/// </summary>
/// <remarks>
/// <c>redis:7-alpine</c>, matching D7' §2.1/§9 and <c>infra/docker-compose.dev.slim.yml</c>.
/// The replica's Container 4 says <c>7.4-alpine</c>; the two documents disagree and D7' §3's
/// compose block is what the dev stack landed, so that is what a test runs against.
/// </remarks>
public sealed class RedisFixture : ContainerFixture
{
    /// <summary>The image the dev stack and the replica run.</summary>
    public const string Image = "redis:7-alpine";

    private RedisContainer? _container;

    protected override string Name => "Redis 7";

    /// <summary>StackExchange.Redis configuration string for the running container.</summary>
    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException(
            $"Redis container is not running: {SkipReason ?? "not started"}");

    protected override async Task StartAsync()
    {
        _container = new RedisBuilder(Image)
            // appendonly matches the deployed configuration, so a test that asserts anything
            // about persistence behaviour sees the same durability window (<= 1 s).
            .WithCommand("--appendonly", "yes", "--appendfsync", "everysec")
            .Build();

        await _container.StartAsync();
    }

    protected override Task StopAsync() =>
        _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();
}

/// <summary>Collection sharing one <see cref="RedisFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "mageride-redis";
}
