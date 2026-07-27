using DbUp.Engine.Output;
using MageRide.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MageRide.TestKit;

/// <summary>
/// A throwaway PostgreSQL 16 with PostGIS and TimescaleDB — the same image the dev stack and
/// the replica run (D7' §2.1, T-06).
/// </summary>
/// <remarks>
/// <para>
/// The image is <c>timescale/timescaledb-ha:pg16</c>, not <c>postgis/postgis</c>: C006's DDL
/// creates a hypertable and four continuous aggregates, so a Postgres without TimescaleDB
/// cannot apply the migration set at all. It is the image
/// <c>infra/docker-compose.dev.slim.yml</c> and <c>infra/scripts/migrate-verify.sh</c> use, so
/// a test, a dev stack and CI all migrate the same server build.
/// </para>
/// <para>
/// One container per xUnit collection, not per test. <see cref="EnsureMigratedAsync"/> applies
/// the schema once and every later call is free, so a collection of integration tests pays the
/// ~4 s migration cost once.
/// </para>
/// </remarks>
public sealed class PostgresFixture : ContainerFixture
{
    /// <summary>The image the whole platform standardises on (CLAUDE.md, D7' §9).</summary>
    public const string Image = "timescale/timescaledb-ha:pg16";

    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private PostgreSqlContainer? _container;
    private MigrationOutcome? _migrated;

    protected override string Name => "PostgreSQL (timescaledb-ha:pg16)";

    /// <summary>Npgsql connection string for the running container.</summary>
    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException(
            $"Postgres container is not running: {SkipReason ?? "not started"}");

    /// <summary>Host port the container's 5432 is published on.</summary>
    public int Port => _container?.GetMappedPublicPort(5432)
        ?? throw new InvalidOperationException("Postgres container is not running.");

    protected override async Task StartAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("mageride_test")
            .WithUsername("mageride")
            .WithPassword("mageride")
            // The module's own wait strategy is deliberately kept: the Postgres entrypoint runs
            // a throwaway server on the unix socket during initdb, so a bare `pg_isready`
            // reports ready before the real server is listening on TCP.
            .Build();

        await _container.StartAsync();
    }

    protected override Task StopAsync() =>
        _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();

    /// <summary>
    /// Applies <c>db/migrations/*.sql</c> once for this fixture and returns what happened.
    /// Repeat calls return the first outcome without touching the database.
    /// </summary>
    /// <remarks>
    /// Runs through <see cref="MigrationEngine"/> — the same DbUp pipeline, journal table and
    /// script ordering as the <c>migrate</c> container — so a schema that applies here applies
    /// in the dev stack and in the replica.
    /// </remarks>
    public async Task<MigrationOutcome> EnsureMigratedAsync(IUpgradeLog? log = null)
    {
        RequireAvailable();

        await _migrationGate.WaitAsync();
        try
        {
            if (_migrated is { } already)
            {
                return already;
            }

            var outcome = MigrationEngine.Apply(ConnectionString, log: log);
            _migrated = outcome;

            if (!outcome.Successful)
            {
                throw new InvalidOperationException(
                    $"migrations failed on '{outcome.FailedScript ?? "<unknown>"}': {outcome.Error?.Message}",
                    outcome.Error);
            }

            return outcome;
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    /// <summary>
    /// Applies the migration set again, deliberately bypassing the cache. Use to assert a
    /// second run is a no-op (<c>ScriptsApplied == 0</c>) or, with
    /// <paramref name="ignoreJournal"/>, that the DDL itself is re-runnable.
    /// </summary>
    public MigrationOutcome ApplyMigrations(bool ignoreJournal = false, IUpgradeLog? log = null)
    {
        RequireAvailable();
        return MigrationEngine.Apply(ConnectionString, ignoreJournal: ignoreJournal, log: log);
    }

    /// <summary>Opens a connection to the container.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        RequireAvailable();
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs one scalar query. Convenience for assertions about schema state.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }
}

/// <summary>Collection sharing one <see cref="PostgresFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "mageride-postgres";
}
