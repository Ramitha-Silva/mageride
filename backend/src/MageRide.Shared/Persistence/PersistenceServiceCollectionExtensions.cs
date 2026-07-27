using MageRide.Shared.Health;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Http.Idempotency.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MageRide.Shared.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Dapper over Npgsql: connection factory, unit of work, type handlers and the readiness
    /// health check (AL-53, D7' §5.1).
    /// </summary>
    /// <remarks>
    /// Reads <c>ConnectionStrings:Postgres</c> (D7' §4.1) and, when present,
    /// <c>ConnectionStrings:PostgresDirect</c> for the LISTEN/NOTIFY path. Anything else comes
    /// from the <c>Postgres</c> section.
    /// </remarks>
    public static IServiceCollection AddMageRidePostgres(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        DapperSetup.Configure();

        services.AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName))
            .Configure(options =>
            {
                options.ConnectionString = configuration.GetConnectionString("Postgres") ?? options.ConnectionString;
                options.DirectConnectionString =
                    configuration.GetConnectionString("PostgresDirect") ?? options.DirectConnectionString;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<INpgsqlConnectionFactory, NpgsqlConnectionFactory>();
        services.TryAddSingleton<IUnitOfWorkFactory, NpgsqlUnitOfWorkFactory>();

        services.AddHealthChecks().AddCheck<PostgresHealthCheck>(
            "postgres",
            HealthStatus.Unhealthy,
            [HealthTags.Ready, HealthTags.Database]);

        return services;
    }

    /// <summary>
    /// The Postgres-backed command log behind idempotent replay (R-14, R-18). Defaults to
    /// <c>rides.command_log</c>; other services override the table through the
    /// <c>CommandLog</c> configuration section.
    /// </summary>
    public static IServiceCollection AddMageRideCommandLog(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CommandLogOptions>()
            .Bind(configuration.GetSection(CommandLogOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<ICommandLog, PostgresCommandLog>();

        return services;
    }
}
