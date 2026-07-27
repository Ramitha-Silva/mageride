using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Persistence;

/// <summary>
/// Postgres wiring for a service. Bound from <c>ConnectionStrings</c> plus the
/// <c>Postgres</c> section (D7' §4.1).
/// </summary>
public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    /// <summary>
    /// The pooled DSN every request-path query uses — <c>pgbouncer:6432</c> in every environment
    /// (D7' §4.1 <c>ConnectionStrings__Postgres</c>, ADD §9.3, replica §M-5).
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// A DSN that reaches Postgres directly, bypassing PgBouncer.
    /// <para>
    /// Required by anything that holds session state across statements — in this platform that is
    /// <c>LISTEN/NOTIFY</c> for the outbox dispatcher (E-09). PgBouncer in transaction mode hands
    /// the server connection back to the pool at COMMIT, so a LISTEN registered on it is silently
    /// dropped and notifications never arrive.
    /// </para>
    /// <para>
    /// When unset the connection factory falls back to <see cref="ConnectionString"/> and logs a
    /// warning; that works against a direct-to-Postgres dev compose and fails against PgBouncer.
    /// </para>
    /// </summary>
    public string? DirectConnectionString { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="ConnectionString"/> points at PgBouncer in
    /// transaction mode, which is the deployed topology. Turns off server-side prepared statements
    /// and the <c>DISCARD ALL</c> reset Npgsql would otherwise send — both are session-scoped and
    /// break under transaction pooling.
    /// </summary>
    public bool PgBouncerTransactionMode { get; set; } = true;

    /// <summary>Per-command timeout. D6' §8.3 budgets 15 s for an API call end to end.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Client-side pool ceiling. PgBouncer owns the real pool, so this only bounds how many
    /// concurrent connections one service instance asks it for.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPoolSize { get; set; } = 40;

    [Range(0, 1000)]
    public int MinPoolSize { get; set; }

    /// <summary>Connect timeout in seconds (D6' §8.3 "per-service connectTimeout set").</summary>
    [Range(1, 120)]
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Application name reported to Postgres; shows up in <c>pg_stat_activity</c>.</summary>
    public string? ApplicationName { get; set; }
}
