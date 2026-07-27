using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace MageRide.Shared.Tests.Infrastructure;

/// <summary>
/// A throwaway PostGIS-enabled Postgres 16 for the tests that exercise real SQL — the outbox
/// LISTEN/NOTIFY path (E-09), the command log (R-14) and the Dapper type handlers.
/// </summary>
/// <remarks>
/// Postgres 16 + PostGIS matches the deployed stack (CLAUDE.md). The container is started per
/// collection, not per test, and every test namespaces its own tables.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Reason the container could not start, or <see langword="null"/> when it is up.</summary>
    public string? SkipReason { get; private set; }

    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Postgres container is not running.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            // The module's own wait strategy is deliberately kept: the official Postgres image
            // runs a throwaway server on the unix socket during initdb, so a bare `pg_isready`
            // reports ready before the real server is listening on TCP.
            _container = new PostgreSqlBuilder("postgis/postgis:16-3.4")
                .WithDatabase("mageride_test")
                .WithUsername("mageride")
                .WithPassword("mageride")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Postgres container unavailable: {ex.GetType().Name}: {ex.Message}";
            _container = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>A throwaway Redis for the token-bucket tests (D-32).</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? SkipReason { get; private set; }

    public string ConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Redis container is not running.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder("redis:7-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            SkipReason = $"Redis container unavailable: {ex.GetType().Name}: {ex.Message}";
            _container = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
