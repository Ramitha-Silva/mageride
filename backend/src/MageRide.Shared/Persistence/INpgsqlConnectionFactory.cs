using Npgsql;

namespace MageRide.Shared.Persistence;

/// <summary>
/// Hands out Npgsql connections. The only way a MageRide service reaches Postgres — repositories
/// take this, run parameterised SQL through Dapper, and own no connection lifetime of their own
/// (AL-53).
/// </summary>
public interface INpgsqlConnectionFactory
{
    /// <summary>
    /// An open connection through the pooled DSN (PgBouncer in every deployed environment). Use
    /// for all request-path work.
    /// </summary>
    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// An open connection straight to Postgres, bypassing PgBouncer.
    /// <para>
    /// Only for session-scoped features that transaction pooling breaks: <c>LISTEN/NOTIFY</c>
    /// (E-09), advisory locks held across statements, <c>COPY</c> streams that outlive a
    /// transaction. Never for ordinary queries — it consumes a real backend slot for as long as it
    /// is held.
    /// </para>
    /// </summary>
    Task<NpgsqlConnection> OpenDirectAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured per-command timeout, in seconds (D6' §8.3).</summary>
    int CommandTimeoutSeconds { get; }
}
