using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Shared.Persistence;

/// <summary>
/// PgBouncer-aware <see cref="INpgsqlConnectionFactory"/> over two <see cref="NpgsqlDataSource"/>
/// instances — one through the pooler for request-path work, one direct for session-scoped
/// features.
/// </summary>
public sealed class NpgsqlConnectionFactory : INpgsqlConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _pooled;
    private readonly NpgsqlDataSource _direct;
    private readonly bool _directIsPooled;

    public NpgsqlConnectionFactory(
        IOptions<PostgresOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<NpgsqlConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres is not configured (D7' §4.1 lists it as required for every .NET service).");
        }

        CommandTimeoutSeconds = settings.CommandTimeoutSeconds;

        _pooled = Build(settings.ConnectionString, settings, pooled: true, loggerFactory);

        if (string.IsNullOrWhiteSpace(settings.DirectConnectionString))
        {
            _directIsPooled = true;
            _direct = _pooled;

            if (settings.PgBouncerTransactionMode)
            {
                logger.LogWarning(
                    "Postgres:DirectConnectionString is not set while PgBouncerTransactionMode is on. " +
                    "LISTEN/NOTIFY (E-09) will not receive notifications through a transaction-mode pooler; " +
                    "the outbox dispatcher will fall back to polling.");
            }
        }
        else
        {
            _direct = Build(settings.DirectConnectionString, settings, pooled: false, loggerFactory);
        }
    }

    public int CommandTimeoutSeconds { get; }

    /// <summary><see langword="true"/> when no separate direct DSN was configured.</summary>
    public bool DirectConnectionIsPooled => _directIsPooled;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        await _pooled.OpenConnectionAsync(cancellationToken);

    public async Task<NpgsqlConnection> OpenDirectAsync(CancellationToken cancellationToken = default) =>
        await _direct.OpenConnectionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _pooled.DisposeAsync();
        if (!_directIsPooled)
        {
            await _direct.DisposeAsync();
        }
    }

    private static NpgsqlDataSource Build(
        string connectionString, PostgresOptions settings, bool pooled, ILoggerFactory loggerFactory)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            CommandTimeout = settings.CommandTimeoutSeconds,
            Timeout = settings.ConnectTimeoutSeconds,
        };

        if (!string.IsNullOrWhiteSpace(settings.ApplicationName))
        {
            csb.ApplicationName = settings.ApplicationName;
        }

        if (pooled)
        {
            csb.MaxPoolSize = settings.MaxPoolSize;
            csb.MinPoolSize = settings.MinPoolSize;

            if (settings.PgBouncerTransactionMode)
            {
                // Server-side prepared statements are bound to a server connection; under
                // transaction pooling the next statement may land on a different one.
                csb.MaxAutoPrepare = 0;

                // Npgsql sends DISCARD ALL when returning a connection to its own pool. PgBouncer
                // already resets between transactions, and DISCARD ALL cannot run inside one.
                csb.NoResetOnClose = true;
            }
        }
        else
        {
            // A direct connection is held open for the life of the listener; one is enough.
            // Idle pruning and max-lifetime recycling only apply to connections sitting in the
            // pool, and this one stays checked out, so both are left at their defaults.
            csb.MaxPoolSize = Math.Max(2, settings.MinPoolSize + 1);
            csb.MinPoolSize = 0;
        }

        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);

        // PostGIS geometry/geography <-> NetTopologySuite (ADD §9.1 geography(Point,4326) columns).
        builder.UseNetTopologySuite(geographyAsDefault: true);
        builder.UseLoggerFactory(loggerFactory);

        return builder.Build();
    }
}
