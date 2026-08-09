using System.Collections.Concurrent;

namespace MageRide.Contract.Tests.Runtime;

/// <summary>
/// Composes a service's real pipeline, once, with settings that satisfy its option validators and
/// reach nothing.
///
/// <para>
/// <b>Composed, not started.</b> Reading a route table needs the pipeline built; it does not need a
/// socket, a database or a broker, and asking for those would make a structural assertion depend on
/// a container. <see cref="ServiceFleet"/> is where a service is actually started and asked a
/// question over HTTP.
/// </para>
/// </summary>
internal static class ServiceComposition
{
    /// <summary>
    /// A syntactically valid Postgres address on a port nothing listens on.
    /// </summary>
    /// <remarks>
    /// Present because the kernel parses the connection string while composing; unreachable because
    /// composition never opens a connection. A service that *did* connect during
    /// <c>Build</c> would fail here loudly, which is itself worth knowing.
    /// </remarks>
    public const string UnreachablePostgres =
        "Host=127.0.0.1;Port=1;Database=mageride;Username=contract;Password=contract;Timeout=1";

    public const string UnreachableRedis = "127.0.0.1:1,abortConnect=false,connectTimeout=200";

    /// <summary>The issuer the suite's own token issuer signs as.</summary>
    public const string Issuer = "https://iam.mageride.test";

    private static readonly ConcurrentDictionary<string, WebApplication> Cache = new(StringComparer.Ordinal);

    /// <summary>The composed pipeline for a service, built at most once per run.</summary>
    public static WebApplication Compose(ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return Cache.GetOrAdd(
            service.Document,
            _ => Compose(service, UnreachablePostgres, UnreachableRedis, configure: null));
    }

    /// <summary>The composed pipeline against a given infrastructure, for a fleet that will start it.</summary>
    public static WebApplication Compose(
        ServiceDefinition service,
        string postgres,
        string redis,
        Action<WebApplicationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(service);

        var settings = ServiceCatalog.CommonSettings(postgres, redis, Issuer);
        foreach (var (key, value) in service.Settings)
        {
            settings[key] = value;
        }

        return service.Compose(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            },
            builder =>
            {
                // Twenty-four pipelines' start-up logs would bury the one assertion that failed.
                if (Environment.GetEnvironmentVariable("MAGERIDE_TEST_LOGS") != "1")
                {
                    builder.Logging.ClearProviders();
                }

                builder.Configuration.AddInMemoryCollection(settings);
                configure?.Invoke(builder);
            });
    }
}
