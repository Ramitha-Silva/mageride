using MageRide.Shared.Health;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MageRide.Shared.Caching;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// A shared <see cref="IConnectionMultiplexer"/>, the token-bucket rate limiter and the
    /// readiness health check (ADD §9.4, D-32, D7' §5.1).
    /// </summary>
    public static IServiceCollection AddMageRideRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .Configure(options =>
                options.ConnectionString = configuration.GetConnectionString("Redis") ?? options.ConnectionString)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisOptions>>().Value;

            var config = ConfigurationOptions.Parse(options.ConnectionString);
            config.AbortOnConnectFail = options.AbortOnConnectFail;
            config.ConnectTimeout = options.ConnectTimeoutMs;
            config.SyncTimeout = options.SyncTimeoutMs;
            config.DefaultDatabase = options.Database;

            if (!string.IsNullOrWhiteSpace(options.ClientName))
            {
                config.ClientName = options.ClientName;
            }

            return ConnectionMultiplexer.Connect(config);
        });

        services.TryAddSingleton<ITokenBucketRateLimiter, RedisTokenBucketRateLimiter>();

        services.AddHealthChecks().AddCheck<RedisHealthCheck>(
            "redis",
            HealthStatus.Unhealthy,
            [HealthTags.Ready, HealthTags.Cache]);

        return services;
    }
}
