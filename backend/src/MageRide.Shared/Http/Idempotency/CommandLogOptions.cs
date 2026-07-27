using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Http.Idempotency;

/// <summary>How a response body is persisted in the command log.</summary>
public enum CommandLogBodyStorage
{
    /// <summary>
    /// Postgres <c>json</c>. Stores the exact text it was given, so a replay is byte for byte
    /// (R-14), and the column is still queryable with the JSON operators.
    /// </summary>
    Json,

    /// <summary>
    /// <c>bytea</c>. Lossless for any payload, including non-JSON, at the cost of queryability.
    /// </summary>
    Bytea,

    /// <summary>
    /// <c>jsonb</c>, as the D4' §5 DDL currently declares.
    /// <para>
    /// <b>Lossy.</b> <c>jsonb</c> is a parsed representation: it discards insignificant whitespace,
    /// drops duplicate keys and reorders object members. A replay is semantically equal but not
    /// byte for byte, so this mode does not satisfy R-14 as written. Supported for compatibility
    /// only — see the C002 handoff note in <c>build/progress.md</c>.
    /// </para>
    /// </summary>
    Jsonb,
}

/// <summary>
/// Points <see cref="Postgres.PostgresCommandLog"/> at a service's command-log table.
/// </summary>
/// <remarks>
/// Each bounded context owns its own table. The defaults describe <c>rides.command_log</c>
/// (ADD §9.1, D4' §5, R-14); other services override <see cref="Schema"/>, <see cref="Table"/>
/// and <see cref="AggregateIdColumn"/>.
/// </remarks>
public sealed class CommandLogOptions
{
    public const string SectionName = "CommandLog";

    [Required]
    public string Schema { get; set; } = "rides";

    [Required]
    public string Table { get; set; } = "command_log";

    /// <summary>
    /// Column holding the aggregate the command targets — <c>ride_id</c> in
    /// <c>rides.command_log</c>. Set to <see langword="null"/> for a table without one.
    /// </summary>
    public string? AggregateIdColumn { get; set; } = "ride_id";

    /// <summary>
    /// Column holding the original <c>Content-Type</c>. Without it a replay cannot distinguish
    /// <c>application/json</c> from <c>application/problem+json</c>, so
    /// <see cref="DefaultContentType"/> is used instead.
    /// </summary>
    public string? ContentTypeColumn { get; set; } = "response_content_type";

    /// <summary>Content type assumed on replay when <see cref="ContentTypeColumn"/> is not configured.</summary>
    public string DefaultContentType { get; set; } = "application/json; charset=utf-8";

    public CommandLogBodyStorage BodyStorage { get; set; } = CommandLogBodyStorage.Json;

    /// <summary>
    /// How long a reservation may sit without a response before another caller may take it over.
    /// Covers the case where the process holding it died mid-command; without it the key would
    /// answer 409 <c>idempotency-in-progress</c> forever. Must exceed the API timeout budget
    /// (D6' §8.3: 15 s).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:15", "01:00:00")]
    public TimeSpan StaleReservationAfter { get; set; } = TimeSpan.FromSeconds(60);
}
