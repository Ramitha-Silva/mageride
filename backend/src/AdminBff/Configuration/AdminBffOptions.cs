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
/// switch</b> — see <see cref="Audit"/>'s remark for the same argument. <b>Δ C065:
/// <c>Pdpa__DueDays</c> now lands here</b>, on <see cref="PdpaOptions.DueDays"/>. So what is here is
/// the audit topic, the statutory deadline, and the knobs this service's own decisions needed.
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

    /// <summary>The AL-39 document viewer's links (C063).</summary>
    [Required]
    public DocumentOptions Documents { get; init; } = new();

    /// <summary>The E-06 data-rights workflow (C065).</summary>
    [Required]
    public PdpaOptions Pdpa { get; init; } = new();

    /// <summary>The SCR-AP-006 finance surface (C065).</summary>
    [Required]
    public FinanceOptions Finance { get; init; } = new();

    /// <summary>
    /// E-06's export and erasure workflow — the one place D7' §4.2's <c>Pdpa__DueDays</c> lands.
    /// </summary>
    public sealed class PdpaOptions
    {
        /// <summary>
        /// The statutory deadline, in days, a request must be fulfilled within (US-1.8).
        /// </summary>
        /// <remarks>
        /// <b>D7' §4.2 names this variable and the database already answers it</b>:
        /// <c>pdpa.requests.due_by</c> defaults to <c>now() + INTERVAL '30 days'</c> (migration
        /// 1306), and iam-svc's <c>DELETE /v1/users/me</c> lets that default stand. So this is
        /// <em>not</em> a second source of truth for the deadline — it is what this service uses to
        /// compute the deadline it *reports* on the 202 before the row is read back, and it is
        /// validated against the row on the way out. Changing it without changing 1306 makes the two
        /// disagree, which is why the value is checked at start-up rather than trusted.
        /// </remarks>
        [Range(1, 365)]
        public int DueDays { get; init; } = 30;

        /// <summary>
        /// How long the signed download URL of a fulfilled export lives.
        /// </summary>
        /// <remarks>
        /// <b>No spec.</b> Fifteen minutes: an export archive is a copy of everything the platform
        /// holds about one person, so the link is worth less than the document viewer's five-minute
        /// one is worth more of — long enough for a large ZIP to download over a slow connection,
        /// short enough that a URL left in a browser's history is dead. <c>GET /v1/pdpa/{requestId}</c>
        /// mints a fresh one on every read, so a subject who is too slow simply asks again.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
        public TimeSpan ArtifactUrlTtl { get; init; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The most rows of any one dataset an export archive carries.
        /// </summary>
        /// <remarks>
        /// <b>No spec.</b> A cap rather than an unbounded read, because an export is assembled in
        /// memory into a ZIP and a three-year-old driver's wallet history is tens of thousands of
        /// rows. Ten thousand per dataset is more than any real account and small enough that the
        /// archive stays a request rather than a job. The manifest inside the ZIP records when a
        /// dataset was truncated, so the subject is told rather than quietly given less.
        /// </remarks>
        [Range(100, 1_000_000)]
        public int MaxRowsPerDataset { get; init; } = 10_000;
    }

    /// <summary>The reconciliation exception queue's one judgement call.</summary>
    public sealed class FinanceOptions
    {
        /// <summary>
        /// How long a gateway session may stay <c>Pending</c> before it is an exception.
        /// </summary>
        /// <remarks>
        /// D6' §7.1 gives the rail a <b>90-second</b> pending window and wallet-svc's
        /// <c>Wallet:TopupPendingWindow</c> is that number. This is deliberately much larger: the
        /// gateway's window is how long a client polls before falling back, and a session that is
        /// still open a minute later is normal (the payer is typing a card number). A session still
        /// open <em>an hour</em> later is a lost callback or the amount mismatch wallet-svc refuses
        /// to credit — both of which are D6' §7.2's "exceptions → Finance queue". Putting the
        /// gateway's own 90 seconds here would fill the queue with people who are still paying.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
        public TimeSpan SettlementGracePeriod { get; init; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// How the AL-39 viewer turns a stored object pointer into something an officer's browser can
    /// open (US-24.8, SCR-AP-003b).
    /// </summary>
    /// <remarks>
    /// There is no object-storage client on this platform yet — D-36's bucket is C125's — and
    /// fleet-svc's own file records that admin-bff is the service that mints these links. Every knob
    /// here exists because of that gap and each is argued at its declaration.
    /// </remarks>
    public sealed class DocumentOptions
    {
        /// <summary>
        /// Where the object store is reachable from an officer's browser, e.g.
        /// <c>https://docs.mageride.lk</c>. Prefixed to a stored pointer that is not already an
        /// absolute http(s) URL.
        /// </summary>
        /// <remarks>
        /// <b>Unset ⇒ the stored pointer is passed through unchanged</b>, which is a filesystem path
        /// on a deployment whose uploads went to fleet-svc's <c>DocumentRoot</c> — the officer's
        /// lightbox will not resolve it. Announced at start-up rather than papered over: inventing a
        /// host would produce a link that 404s somewhere nobody is looking.
        /// </remarks>
        public string? PublicBaseUrl { get; init; }

        /// <summary>
        /// The HMAC key the signed object URL carries.
        /// </summary>
        /// <remarks>
        /// <b>Unset ⇒ a key generated per process</b>, so a URL minted by one replica does not
        /// verify on another. Logged as a warning by <c>DocumentLinks</c>, exactly as
        /// subscription-svc and fleet-svc do for their own signed links.
        /// </remarks>
        public string? SigningKey { get; init; }

        /// <summary>
        /// How long a signed object URL lives.
        /// </summary>
        /// <remarks>
        /// <b>No spec</b> beyond AL-39's "short-lived". Five minutes: long enough for a lightbox to
        /// load a scan of a licence over a slow connection, short enough that a URL copied out of a
        /// browser's history is worthless by the time anybody pastes it. The audited route can
        /// always mint another.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
        public TimeSpan UrlTtl { get; init; } = TimeSpan.FromMinutes(5);
    }

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

        /// <summary>
        /// registry-svc — AL-30's recompute, which is what turns a confirmed field into an approved
        /// Mode C vehicle (C029, C063).
        /// </summary>
        /// <remarks>
        /// registry-svc built <c>POST /v1/internal/vehicles/{id}/onboarding/recompute</c> for this
        /// caller and says so: "admin-bff writes <c>document_fields.verify_status='confirmed'</c>
        /// and then has no way to tell registry-svc, so the vehicle would sit at
        /// <c>pending_review</c> for a field that is no longer pending".
        /// </remarks>
        [Required]
        public UpstreamService Registry { get; init; } = new();

        /// <summary>
        /// fleet-svc — the fleet-org queue and both AL-49/AL-50 decisions (C058, C059, C063).
        /// </summary>
        /// <remarks>
        /// The whole <c>/v1/internal/fleets/**</c> plane exists for this BFF; fleet-svc's file says
        /// so. Approving an organisation here is what sets <c>payout_profiles.status='verified'</c>
        /// and therefore what makes <c>payTo</c> available to subscription-svc (AL-49, BR-31.1).
        /// </remarks>
        [Required]
        public UpstreamService Fleet { get; init; } = new();

        /// <summary>
        /// wallet-svc — the ledger seam US-14.11's fee reversal posts through (C046, C065).
        /// </summary>
        /// <remarks>
        /// Unset ⇒ <c>POST /v1/admin/drivers/wallet/{driverId}/reverse-fee</c> answers 503 and
        /// <b>no driver can be given back a fee they were wrongly charged</b>. The route stays
        /// mapped and stays gated, like every other unconfigured upstream.
        /// </remarks>
        [Required]
        public UpstreamService Wallet { get; init; } = new();

        /// <summary>
        /// fare-svc — E-05's refund execution (C050, C065).
        /// </summary>
        /// <remarks>
        /// Role-gated rather than internal, so this one carries the operator's own bearer and no
        /// shared key — the same split content-svc and transit-svc are on. Unset ⇒ the refund
        /// <em>queue</em> still reads (it is this service's own query over <c>fares.refunds</c>) and
        /// only the decision answers 503.
        /// </remarks>
        [Required]
        public UpstreamService Fare { get; init; } = new();
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
