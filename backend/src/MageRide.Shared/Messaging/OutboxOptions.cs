using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Messaging;

/// <summary>
/// Transactional-outbox wiring for a service (D6' §2.4, R-13, E-09).
/// </summary>
/// <remarks>
/// Defaults describe <c>rides.outbox</c> on channel <c>ride_outbox</c>, publishing to
/// <c>ride.events</c> — the pairing D7' §4.2 configures for ride-svc
/// (<c>Outbox__Channel=ride_outbox</c>) and D6' §2.1 lists in the topic registry. dispatch-svc
/// overrides the table, channel and topic.
/// </remarks>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    [Required]
    public string Schema { get; set; } = "rides";

    [Required]
    public string Table { get; set; } = "outbox";

    /// <summary>
    /// Postgres <c>LISTEN/NOTIFY</c> channel. The dispatcher listens on it and the writer signals
    /// it inside the writing transaction, so the wake-up arrives on COMMIT and never before
    /// (R-13: no phantom offers).
    /// </summary>
    [Required]
    public string Channel { get; set; } = "ride_outbox";

    /// <summary>Redpanda topic the dispatched rows are produced to (D6' §2.1).</summary>
    [Required]
    public string Topic { get; set; } = "ride.events";

    /// <summary>Rows drained per wake-up.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Safety-net poll interval. <c>LISTEN/NOTIFY</c> is the primary trigger (E-09 requires a
    /// sub-50 ms wake-up, which polling cannot give); this only catches rows committed while the
    /// listener was reconnecting.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Backoff before re-listening after the direct connection drops.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pause after a failed publish batch before retrying. The rows stay undispatched, so nothing
    /// is lost — the pause stops a broker outage becoming a hot loop.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan PublishRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Runs the dispatcher in this process. Off for services that only write outbox rows.</summary>
    public bool DispatcherEnabled { get; set; } = true;
}
