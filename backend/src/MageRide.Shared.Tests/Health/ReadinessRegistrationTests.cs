using MageRide.Shared.Caching;
using MageRide.Shared.Health;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Tests.Health;

/// <summary>
/// D7' §5.1: a stateless .NET service's readiness probe is a "DB+Redis+Kafka ping". Each
/// registration extension has to contribute its own check, or a service silently reports ready
/// while a dependency it needs is down.
/// </summary>
public sealed class ReadinessRegistrationTests
{
    private static IServiceCollection Configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=pgbouncer;Port=6432;Database=mageride;Username=svc;Password=x",
            ["ConnectionStrings:Redis"] = "redis:6379",
            ["Kafka:BootstrapServers"] = "redpanda:9092",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddMageRidePostgres(configuration);
        services.AddMageRideRedis(configuration);
        services.AddMageRideKafka(configuration);

        return services;
    }

    private static IReadOnlyList<HealthCheckRegistration> Registrations(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToArray();

    [Fact]
    public void All_three_dependencies_register_a_readiness_check()
    {
        var registrations = Registrations(Configured());

        Assert.Equal(
            ["kafka", "postgres", "redis"],
            registrations.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.All(registrations, r => Assert.Contains(HealthTags.Ready, r.Tags));
        Assert.All(registrations, r => Assert.Equal(HealthStatus.Unhealthy, r.FailureStatus));
    }

    [Fact]
    public void Each_check_carries_its_own_dependency_tag()
    {
        var registrations = Registrations(Configured()).ToDictionary(r => r.Name, StringComparer.Ordinal);

        Assert.Contains(HealthTags.Database, registrations["postgres"].Tags);
        Assert.Contains(HealthTags.Cache, registrations["redis"].Tags);
        Assert.Contains(HealthTags.Messaging, registrations["kafka"].Tags);
    }

    /// <summary>
    /// The connection factory needs a distinct direct DSN for LISTEN/NOTIFY; the pooled DSN
    /// points at PgBouncer, where a session-scoped LISTEN is dropped at COMMIT (E-09).
    /// </summary>
    [Fact]
    public void The_direct_dsn_is_bound_from_connection_strings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=pgbouncer;Port=6432;Database=mageride;Username=svc;Password=x",
            ["ConnectionStrings:PostgresDirect"] = "Host=postgres;Port=5432;Database=mageride;Username=svc;Password=x",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMageRidePostgres(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<PostgresOptions>>().Value;

        Assert.Contains("pgbouncer", options.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("postgres", options.DirectConnectionString!, StringComparison.Ordinal);
        Assert.True(options.PgBouncerTransactionMode);
    }

    /// <summary>
    /// Falling back to the pooled DSN is allowed (it is what the direct-to-Postgres dev compose
    /// wants) but must be an explicit, observable state rather than a silent one.
    /// </summary>
    [Fact]
    public void Without_a_direct_dsn_the_factory_reports_that_it_is_falling_back()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=postgres;Port=5432;Database=mageride;Username=svc;Password=x",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMageRidePostgres(configuration);

        var factory = (NpgsqlConnectionFactory)services.BuildServiceProvider().GetRequiredService<INpgsqlConnectionFactory>();

        Assert.True(factory.DirectConnectionIsPooled);
    }

    [Fact]
    public void A_missing_postgres_connection_string_fails_at_start_up_not_at_first_query()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMageRidePostgres(new ConfigurationBuilder().Build());

        Assert.ThrowsAny<Exception>(() =>
            services.BuildServiceProvider().GetRequiredService<INpgsqlConnectionFactory>());
    }
}
