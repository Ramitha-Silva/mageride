namespace MageRide.Transit.Gtfs;

/// <summary>
/// The <c>audit.events</c> actions this service writes for the GTFS Dataset Manager (D-35, AL-54).
/// </summary>
/// <remarks>
/// <b>The vocabulary is this service's; the writer is not.</b> The INSERT moved into the kernel as
/// <c>MageRide.Shared.Messaging.IAuditEventWriter</c> when C062 became its third caller — which is
/// exactly what the C057 handoff asked for. The three facts below stay here, beside the lifecycle
/// that reaches them.
/// </remarks>
public static class GtfsAuditActions
{
    /// <summary><c>entity_type</c> for every GTFS lifecycle fact.</summary>
    public const string FeedEntity = "gtfs_feed";

    /// <summary>An operator uploaded a zip (US-28.1).</summary>
    public const string FeedUploaded = "GTFS_FEED_UPLOADED";

    /// <summary>
    /// Validation reached a verdict (BR-32.1). <b>Actor-less by construction</b> — a queued job
    /// decided it, not a person, and <c>audit.events.actor_id</c> is nullable for exactly this.
    /// </summary>
    public const string FeedValidated = "GTFS_FEED_VALIDATED";

    /// <summary>A feed went live (US-28.2), including a rollback, which is the same act (BR-32.3).</summary>
    public const string FeedActivated = "GTFS_FEED_ACTIVATED";
}
