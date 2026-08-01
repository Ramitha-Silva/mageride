using System.ComponentModel.DataAnnotations;

namespace MageRide.AdminBff.Configuration;

/// <summary>
/// admin-bff's own settings. Everything cross-cutting is the kernel's
/// (<c>ConnectionStrings:Postgres</c>, <c>Jwt:*</c>, <c>Redis:*</c>, <c>Kafka:*</c>).
/// </summary>
/// <remarks>
/// D7' §4.2 gives this service five variables: <c>Audit__Topic</c>, <c>Pdpa__DueDays</c>,
/// <c>Rbac__DenyByDefault</c>, <c>Login__MaxFailedAttempts</c> and <c>Login__LockoutMinutes</c>
/// (plus the optional <c>Login__IpAllowList</c>). Three of those are not settings of this service:
/// <b>the two <c>Login__*</c> and the allow-list are iam-svc's</b>, which owns every credential path
/// (AL-07, C026's <c>Auth:InternalRoleIpAllowList</c>), and <b><c>Rbac__DenyByDefault</c> is not a
/// switch</b> — see <see cref="Audit"/>'s remark for the same argument. <c>Pdpa__DueDays</c> is
/// C065's. So what is here is the audit topic plus the knobs this component's own decisions needed.
/// </remarks>
public sealed class AdminBffOptions
{
    public const string SectionName = "AdminBff";

    [Required]
    public AuditOptions Audit { get; init; } = new();

    [Required]
    public UpstreamOptions Upstreams { get; init; } = new();

    /// <summary>
    /// How far back <c>GET /v1/admin/audit-log</c> will look when the caller names no window.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> 30 days, because the audit log is append-only and grows without bound: an
    /// unfiltered "everything ever" default would page an auditor through years of rows to reach
    /// yesterday's. A caller who wants more says so with <c>from</c>.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan AuditLogDefaultWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The D-35 audit interceptor.</summary>
    public sealed class AuditOptions
    {
        /// <summary>
        /// The Redpanda topic the interceptor mirrors each row onto (D7' §4.2 <c>Audit__Topic</c>,
        /// D6' §2.1). The database row is the record; this is the sink.
        /// </summary>
        [Required]
        public string Topic { get; init; } = Shared.Messaging.EventTopics.AuditEvents;

        /// <summary>
        /// Whether to publish each audit row onto <see cref="Topic"/> as well as storing it.
        /// </summary>
        /// <remarks>
        /// <b>Off does not weaken D-35.</b> The row is the immutable log and <c>GET
        /// /v1/admin/audit-log</c> reads it from Postgres; the topic is D6' §2.1's cold-storage
        /// sink, and a deployment without Redpanda (a laptop, a contract test) should not be
        /// unable to suspend a vehicle. What is <em>not</em> optional is the row — see
        /// <c>AuditInterceptor</c>.
        /// </remarks>
        public bool PublishToTopic { get; init; } = true;

        /// <summary>
        /// Trust <c>X-Forwarded-For</c> for the <c>ip</c> column.
        /// </summary>
        /// <remarks>
        /// On, because every request arrives through the C008 gateway and the socket address would
        /// otherwise be the gateway's on every row — the same reason iam-svc's
        /// <c>Auth:TrustForwardedFor</c> is on. Turn it off for a deployment reached directly, where
        /// the header is caller-supplied and therefore a caller-chosen audit entry.
        /// </remarks>
        public bool TrustForwardedFor { get; init; } = true;
    }

    /// <summary>
    /// The four services whose decisions this BFF fronts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A missing base URL does not unmap the route.</b> Every route stays in the table and
    /// answers <c>503 dependency-unavailable</c> when its upstream is unconfigured, because the
    /// route table is what the RBAC matrix test and the D-35 start-up guard enumerate — a route
    /// that disappears when a setting is absent is a route neither fence covers. It is announced
    /// at start-up instead, loudly, like every other switch-off on this platform.
    /// </para>
    /// <para>
    /// <b>Transit is the odd one.</b> `gateway-routes.json` sends `/v1/admin/transit/**` straight
    /// to transit-svc at Order 20, ahead of the Order 90 admin-bff catch-all, so in the deployed
    /// topology admin-bff's copy is never reached. It exists so the Configuration nav group is
    /// whole for a deployment that talks to admin-bff directly (the dev compose, a contract run),
    /// and it forwards the caller's own bearer because transit-svc gates it on the same nine roles.
    /// </para>
    /// </remarks>
    public sealed class UpstreamOptions
    {
        /// <summary>safety-svc — the vehicle-report queue and the confirm/dismiss decision (C052).</summary>
        [Required]
        public UpstreamService Safety { get; init; } = new();

        /// <summary>support-svc — the agent ticket queue and its resolution (C053).</summary>
        [Required]
        public UpstreamService Support { get; init; } = new();

        /// <summary>content-svc — `content.broadcasts`, which it owns (D-26, C054).</summary>
        [Required]
        public UpstreamService Content { get; init; } = new();

        /// <summary>transit-svc — the AL-54 GTFS Dataset Manager (SCR-AP-016).</summary>
        [Required]
        public UpstreamService Transit { get; init; } = new();
    }

    /// <summary>One upstream this BFF forwards to.</summary>
    public sealed class UpstreamService
    {
        /// <summary>e.g. <c>http://safety-svc:5000</c>. Unset ⇒ the routes answer 503.</summary>
        public string? BaseUrl { get; init; }

        /// <summary>
        /// The shared secret on the callee's <c>/v1/internal/**</c> plane
        /// (<c>X-MageRide-Internal-Key</c>, C008). Empty where the upstream authenticates the
        /// caller's own bearer instead — content-svc and transit-svc both gate on the nine roles.
        /// </summary>
        public string? InternalApiKey { get; init; }

        /// <summary>
        /// Budget for one forwarded call. 5 minutes rather than seconds because the transit family
        /// includes a 200 MB feed upload (BR-32.1) and an activation that loads a national dataset
        /// (BR-32.2); the queue calls finish in milliseconds and never notice the ceiling.
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:30:00")]
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    }
}
