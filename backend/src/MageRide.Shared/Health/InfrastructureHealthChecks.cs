using Confluent.Kafka;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Shared.Health;

/// <summary>Tags the readiness probe selects on (D7' §5.1).</summary>
public static class HealthTags
{
    /// <summary>Included in <c>/health/ready</c>.</summary>
    public const string Ready = "ready";

    public const string Database = "db";
    public const string Cache = "redis";
    public const string Messaging = "kafka";
}

/// <summary>Readiness ping against Postgres — the "DB" in D7' §5.1's <c>DB+Redis+Kafka ping</c>.</summary>
public sealed class PostgresHealthCheck(INpgsqlConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1;", connection) { CommandTimeout = 3 };
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("Postgres reachable.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Postgres unreachable.", ex);
        }
    }
}

/// <summary>Readiness ping against Redis.</summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!redis.IsConnected)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "Redis is not connected.");
            }

            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis reachable in {latency.TotalMilliseconds:0.##} ms.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis unreachable.", ex);
        }
    }
}

/// <summary>
/// Readiness ping against Redpanda: a metadata fetch over the Kafka API.
/// </summary>
/// <remarks>
/// The admin client is built per check rather than held. A readiness probe runs every 10–15 s
/// (D7' §5.1), so the cost is irrelevant, and a cached client that lost its broker connection
/// would keep reporting the stale view the probe exists to catch.
/// </remarks>
public sealed class KafkaHealthCheck(IOptions<KafkaOptions> options) : IHealthCheck
{
    private readonly KafkaOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = _options.BootstrapServers,
                ClientId = _options.ClientId,
            }).Build();

            var metadata = admin.GetMetadata(TimeSpan.FromMilliseconds(_options.MetadataTimeoutMs));

            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"Redpanda reachable ({metadata.Brokers.Count} broker(s)).")
                : new HealthCheckResult(context.Registration.FailureStatus, "Redpanda reported no brokers."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Redpanda unreachable.", ex));
        }
    }
}
