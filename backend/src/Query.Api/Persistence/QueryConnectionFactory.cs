using MageRide.Query.Configuration;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Query.Persistence;

/// <summary>
/// Which copy of the database a read may come from.
/// </summary>
/// <remarks>
/// The distinction is the whole of ADD §9.3's "read-after-write consistency <b>only where
/// required</b>" — and "where required" is a property of the read, not a preference a caller
/// expresses per call site.
/// </remarks>
public enum ReadConsistency
{
    /// <summary>
    /// A replica is fine: a few hundred milliseconds of lag changes the answer by at most one row at
    /// the edge of a page, and the caller cannot tell.
    /// </summary>
    Eventual,

    /// <summary>
    /// The primary. For reads whose subject was written moments ago by another service, where
    /// replica lag does not degrade the answer but <em>inverts</em> it — a missing row reads as
    /// "no such trip", which is indistinguishable from a deleted one.
    /// </summary>
    ReadAfterWrite,
}

/// <summary>
/// Hands out connections, choosing between the primary and a read replica (ADD §9.3).
/// </summary>
public interface IQueryConnectionFactory
{
    /// <summary><see langword="true"/> when a replica DSN is configured.</summary>
    bool HasReplica { get; }

    /// <summary>Opens a connection appropriate to <paramref name="consistency"/>.</summary>
    Task<NpgsqlConnection> OpenAsync(ReadConsistency consistency, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IQueryConnectionFactory"/>
/// <remarks>
/// <para>
/// <b>Why this is a second factory rather than a setting on the kernel's.</b>
/// <see cref="INpgsqlConnectionFactory"/> already distinguishes pooled from direct, and that
/// distinction is about <em>session state</em> — <c>LISTEN/NOTIFY</c> and <c>COPY</c> need a backend
/// they keep. A replica is a different axis: same session semantics, different freshness. Folding
/// them into one enum would let a caller ask for "direct replica" and mean nothing by it. query-svc
/// is also the only service on the platform that reads a replica (ADD §9.3 names it alone), so
/// putting it in the kernel would put a facility in front of twenty services that must not use it.
/// </para>
/// <para>
/// <b>Where the platform requires read-after-write, and why only there.</b> Exactly one class of
/// read in this service is reached by a user immediately after a write they can perceive: the
/// single-trip detail (<c>GET /v1/trips/{userId}/{tripId}</c>), opened from the receipt screen
/// seconds after ride-svc marked the ride terminal. Lag there produces a <c>404</c>, and a 404 on a
/// trip a passenger has just finished is not a stale answer, it is a wrong one. Everything else here
/// is a list or an aggregate: a trip missing from the top of a history page appears on the next
/// pull, and an earnings total short by one just-settled fare is a number that was true a moment
/// ago. Sending those to the primary as well would give up the whole point of §9.3 to protect
/// against something no user can see.
/// </para>
/// <para>
/// <b>The live plane is not affected either way.</b> Positions come from Redis; the only Postgres
/// read on the nearby path is the registration-number and driver-name enrichment, which is registry
/// state measured in months.
/// </para>
/// </remarks>
public sealed class QueryConnectionFactory : IQueryConnectionFactory, IAsyncDisposable
{
    private readonly INpgsqlConnectionFactory _primary;
    private readonly NpgsqlDataSource? _replica;

    public QueryConnectionFactory(
        INpgsqlConnectionFactory primary,
        IOptions<QueryOptions> options,
        IOptions<PostgresOptions> postgres,
        ILoggerFactory loggerFactory,
        ILogger<QueryConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _primary = primary;

        var replicaDsn = options.Value.ReplicaConnectionString;

        if (string.IsNullOrWhiteSpace(replicaDsn))
        {
            // Not a warning. A single-node deployment — the dev compose, the Contabo replica, every
            // test run — has no replica by design, and ADD §9.3 puts the second and third node in
            // the DOKS topology. Serving every read from the primary is correct, just not scaled.
            logger.LogInformation(
                "Query:ReplicaConnectionString is not set; every read is served from the primary. "
                + "ADD §9.3's read scaling is inactive, which is expected outside DOKS.");

            _replica = null;
            return;
        }

        var settings = postgres.Value;

        var csb = new NpgsqlConnectionStringBuilder(replicaDsn)
        {
            CommandTimeout = settings.CommandTimeoutSeconds,
            Timeout = settings.ConnectTimeoutSeconds,
            ApplicationName = string.IsNullOrWhiteSpace(settings.ApplicationName)
                ? "query-svc-replica"
                : settings.ApplicationName + "-replica",
            MaxPoolSize = settings.MaxPoolSize,
            MinPoolSize = settings.MinPoolSize,

            // A streaming replica refuses a write anyway, so this is belt and braces — but it is the
            // belt that turns a DSN accidentally pointing back at the primary into a connection
            // failure rather than a silently unscaled deployment.
            TargetSessionAttributes = "prefer-standby",
        };

        if (settings.PgBouncerTransactionMode)
        {
            // The same two accommodations the kernel's factory makes: the replica sits behind its
            // own PgBouncer (ADD §9.3 puts one in front of every service), and both server-side
            // prepared statements and Npgsql's DISCARD ALL are session-scoped.
            csb.MaxAutoPrepare = 0;
            csb.NoResetOnClose = true;
        }

        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);

        builder.UseNetTopologySuite(geographyAsDefault: true);
        builder.UseLoggerFactory(loggerFactory);

        _replica = builder.Build();

        logger.LogInformation("Read replica configured; list and aggregate reads will use it (ADD §9.3).");
    }

    public bool HasReplica => _replica is not null;

    public async Task<NpgsqlConnection> OpenAsync(
        ReadConsistency consistency, CancellationToken cancellationToken)
    {
        if (_replica is null || consistency is ReadConsistency.ReadAfterWrite)
        {
            MageRideDiagnostics.QueryReplicaReads.Add(
                1, new KeyValuePair<string, object?>("target", "primary"),
                new KeyValuePair<string, object?>("reason", consistency.ToString()));

            return await _primary.OpenAsync(cancellationToken);
        }

        MageRideDiagnostics.QueryReplicaReads.Add(
            1, new KeyValuePair<string, object?>("target", "replica"),
            new KeyValuePair<string, object?>("reason", consistency.ToString()));

        return await _replica.OpenConnectionAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_replica is not null)
        {
            await _replica.DisposeAsync();
        }
    }
}
