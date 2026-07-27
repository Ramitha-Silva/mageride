using MageRide.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace MageRide.Shared.Tests.Infrastructure;

/// <summary>Builds minimal in-memory hosts so middleware can be exercised over real HTTP.</summary>
internal static class TestHosts
{
    /// <summary>
    /// A <see cref="WebApplication"/> on the TestServer transport with the given configuration
    /// values, ready for the caller to add services and map endpoints.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(IDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        if (configuration is { Count: > 0 })
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        return builder;
    }

    /// <summary>A connection factory pointed at a Testcontainers Postgres, with Dapper configured.</summary>
    public static NpgsqlConnectionFactory ConnectionFactory(string connectionString, int commandTimeoutSeconds = 15)
    {
        DapperSetup.Configure();

        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = connectionString,
            // The test container is plain Postgres, so LISTEN works on the pooled DSN too.
            DirectConnectionString = connectionString,
            PgBouncerTransactionMode = false,
            CommandTimeoutSeconds = commandTimeoutSeconds,
        });

        return new NpgsqlConnectionFactory(
            options, NullLoggerFactory.Instance, NullLogger<NpgsqlConnectionFactory>.Instance);
    }
}
