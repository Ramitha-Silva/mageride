using System.ComponentModel.DataAnnotations;

namespace MageRide.Fleet.Configuration;

/// <summary>
/// fleet-svc's knobs. Every default is argued at its declaration; the ones with no spec behind
/// them say so.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    // -------------------------------------------------------------------------------------------
    // The two D7' §4.2 switches
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Enter every read through <c>SET LOCAL ROLE mageride_fleet_reader</c> + the
    /// <c>app.fleet_id</c> GUC, so migration 1806's policies decide what the caller can see.
    /// </summary>
    /// <remarks>
    /// <b>D7' §4.2 names this <c>Fleet__RlsEnabled=true</c>, and true is the only value this
    /// service is designed for.</b> The escape hatch exists for one deployment shape: a login role
    /// that has not been granted membership of <c>mageride_fleet_reader</c> cannot
    /// <c>SET ROLE</c> at all, and 1806's grant is guarded rather than assumed. Turning it off
    /// leaves cross-org reads guarded by the repositories' own <c>WHERE fleet_id = @FleetId</c>
    /// and nothing else — which is precisely the arrangement the C058 fence rules out, so it is
    /// announced as an error at start-up rather than merely logged.
    /// </remarks>
    public bool RlsEnabled { get; set; } = true;

    /// <summary>
    /// Refuse vehicle and assignment operations until a Verification Officer has approved the org
    /// (US-13.A7). D7' §4.2 names it <c>Fleet__VerificationGate=true</c>.
    /// </summary>
    /// <remarks>
    /// <b>Off is a development convenience and nothing else</b> — an org that can onboard vehicles
    /// before anybody has read its KYC is the whole of what US-13.A7 forbids. Announced at
    /// start-up when off.
    /// </remarks>
    public bool VerificationGate { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // The internal plane (AL-39 / AL-49 verification)
    // -------------------------------------------------------------------------------------------

    /// <summary>The interim shared secret <c>/v1/internal/fleets/**</c> demands, until mTLS (C042).</summary>
    /// <remarks>
    /// <b>Unset leaves the internal family unmapped</b>, the posture registry-svc, ride-svc,
    /// notification-svc, safety-svc and support-svc take. What is behind it is the approval
    /// decision itself: an open route here approves any organisation on the platform and verifies
    /// its bank account, which is the one write that decides where Mode B money is sent (BR-31.1).
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // Payout-profile documents (AL-49, SCR-FP-002a)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Where payout-document bytes are written.
    /// </summary>
    /// <remarks>
    /// <b>Not object storage.</b> D-36 puts uploaded documents on SSE-KMS buckets and no service in
    /// this build has an S3 client (C125), so the bytes go to a configured directory and this is
    /// the deployment's mount point. The <c>docs.uploads</c> row is written either way, which is
    /// what makes the swap one class. Same arrangement as <c>Support:ScreenshotRoot</c>.
    /// </remarks>
    public string? DocumentRoot { get; set; }

    /// <summary>
    /// Ceiling on one payout document.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it</b> — the same bound and the same number as
    /// <c>Support:ScreenshotMaxBytes</c>, <c>Ride:ProofPhotoMaxBytes</c> and
    /// <c>Subscription:SlipMaxBytes</c>. A bank statement photographed on a phone is the same
    /// artefact as any of them. The idempotency middleware's request buffer is raised to match in
    /// <see cref="FleetApplication"/>, or it would answer <c>413</c> first with a message about
    /// buffering rather than about the document.
    /// </remarks>
    [Range(64 * 1024, 64L * 1024 * 1024)]
    public long DocumentMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// NFR-28's raw-document retention, written to <c>docs.uploads.auto_delete_at</c> at upload.
    /// </summary>
    /// <remarks>
    /// The sweeper is not this service's — 1301's <c>ix_uploads_auto_delete</c> is the index it
    /// will scan. What this service owes is a correct deadline on the row, so a photograph of
    /// somebody's passbook is not kept for ever by omission.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan DocumentRetention { get; set; } = TimeSpan.FromDays(90);

    // -------------------------------------------------------------------------------------------
    // Bounds
    // -------------------------------------------------------------------------------------------

    /// <summary>Rows the member list and the verification queue return at most (D3' §0 caps a page at 100).</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 50;

    /// <summary>
    /// Members one organisation may hold.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> US-13.A5 gives the Fleet Owner an unbounded "provision team members", and an
    /// unbounded provisioning route on a portal whose sub-users need no verification is a way to
    /// create accounts in bulk. A state bus company's operations desk is tens of people, not
    /// thousands; the cap is a backstop and its refusal says so.
    /// </remarks>
    [Range(2, 10_000)]
    public int MaxMembersPerFleet { get; set; } = 200;

    // -------------------------------------------------------------------------------------------
    // C059 — per-vehicle documents (AL-50, SCR-FP-004)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// ocr-svc's base address (C054, D6' §7.5).
    /// </summary>
    /// <remarks>
    /// <b>Unset means every uploaded document is stored and none is read.</b> Its slot then reads
    /// <c>pending</c> for ever, which blocks approval on every vehicle in the fleet — the honest
    /// outcome, and the same one registry-svc's <c>UnconfiguredDocumentExtractionClient</c>
    /// produces for the Driver App. Announced at start-up.
    /// </remarks>
    public string? OcrBaseUrl { get; set; }

    /// <summary>The shared secret ocr-svc's <c>/v1/internal/ocr/**</c> demands, until mTLS (C042).</summary>
    public string? OcrInternalApiKey { get; set; }

    /// <summary>
    /// How long one extraction may take.
    /// </summary>
    /// <remarks>
    /// Longer than the platform's other internal hops (D6' §8.3's 2 s) because the work is a
    /// redaction pre-pass plus a vision model call plus, on the fallback, an on-prem OCR run —
    /// registry-svc allows the same. A timeout is a document nobody read, not a failed upload.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The confidence at or above which an extracted field is <c>auto_verified</c> (AL-29).
    /// </summary>
    /// <remarks>
    /// <b>No spec pins the number</b> — AL-29, BR-25.2 and D6' §7.5 all say "below threshold" and
    /// none says what it is. This is registry-svc's default and must stay equal to it: the two
    /// services write <c>registry.document_fields</c> for the same Verification Officer queue, and
    /// two thresholds would mean a licence photographed once is doubtful in the Driver App and
    /// certain in the Fleet Portal. Bounded at 0.5 so it cannot become "trust everything".
    /// </remarks>
    [Range(0.5, 1.0)]
    public decimal OcrConfidenceThreshold { get; set; } = 0.80m;

    // -------------------------------------------------------------------------------------------
    // C059 — bulk vehicle onboarding (US-13.1)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows accepted in one CSV.
    /// </summary>
    /// <remarks>
    /// US-13.1 reuses "the Epic 3 bulk-onboarding validation", whose ceiling is T-09's 5,000 IMEIs
    /// per upload; <c>ck_fleet_bulk_jobs</c>'s own <c>total_rows &lt;= 5000</c> is the same number in
    /// the database. Over it is <c>413 too-many-rows</c> with the count in the message.
    /// </remarks>
    [Range(1, 5_000)]
    public int BulkMaxRows { get; set; } = 5_000;

    /// <summary>
    /// Bytes accepted in one CSV upload.
    /// </summary>
    /// <remarks>
    /// 5,000 rows of <c>registrationNumber,vehicleType,mode,…</c> is around 200 KB; 2 MiB leaves
    /// room for quoting and CRLF endings while refusing something that was never a vehicle CSV —
    /// and refusing it at the pipe rather than after buffering it. provisioning-svc's bulk upload
    /// takes the same bound for the same reason.
    /// </remarks>
    [Range(64 * 1024, 16L * 1024 * 1024)]
    public long BulkUploadMaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Signs the <c>errorReportUrl</c> a bulk job answers with.
    /// </summary>
    /// <remarks>
    /// <b>Unset means a key generated for this process</b>: a link minted by one replica does not
    /// verify on another and does not survive a restart, so an operator is shown an expired-looking
    /// 404 on a link they were handed a second ago. Said at start-up, exactly as provisioning-svc
    /// says it about its own.
    /// </remarks>
    public string? ErrorReportSigningKey { get; set; }

    /// <summary>How long an error-report link stays valid.</summary>
    /// <remarks>
    /// <b>No spec.</b> Long enough for an operator to finish reading the import summary and click
    /// the link, short enough that a link left in a browser history is dead. The same 24 h
    /// provisioning-svc uses.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan ErrorReportUrlTtl { get; set; } = TimeSpan.FromHours(24);

    // -------------------------------------------------------------------------------------------
    // C059 — scheduling and the not-started alarm (US-13.11)
    // -------------------------------------------------------------------------------------------

    /// <summary>Run the not-started sweep.</summary>
    /// <remarks>
    /// <b>Off means no alarm ever rings.</b> A schedule can still be created and it simply sits at
    /// <c>SCHEDULED</c> for ever — indistinguishable, from the portal, from a departure nobody has
    /// reached yet. Announced as an error at start-up for that reason.
    /// </remarks>
    public bool ScheduleAlarmsEnabled { get; set; } = true;

    /// <summary>How often the sweep runs.</summary>
    /// <remarks>
    /// <b>No spec.</b> US-13.11 gives an alarm offset in minutes and says nothing about resolution;
    /// 30 s bounds the lateness of an alarm to well inside the smallest offset the contract admits
    /// (one minute), and the claim statement makes every extra pass free.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan ScheduleAlarmInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Departures claimed per pass.</summary>
    [Range(1, 1_000)]
    public int ScheduleAlarmBatchSize { get; set; } = 100;

    /// <summary>
    /// How early a session may open and still count as making its departure.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> A bus that pulls out of the depot eight minutes before its booked time has
    /// made the departure, and an alarm about it is the sort of false positive that gets an alarm
    /// switched off. Half an hour is generous in the direction that costs nothing: the worst case
    /// is a genuinely missed departure whose vehicle happened to run half an hour earlier.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00", "04:00:00")]
    public TimeSpan ScheduleEarlyStartGrace { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>notification-svc's base address (C051), for the US-13.11b driver alarm.</summary>
    /// <remarks>
    /// <b>Unset means the alarm is computed and never delivered.</b> The schedule still moves to
    /// <c>MISSED</c> and <c>alarm_raised_at</c> is still stamped, so the Fleet Portal can show it —
    /// but the ringing alarm US-13.11 promises in the driver's app does not happen. Said once per
    /// alarm in the log, and once at start-up.
    /// </remarks>
    public string? NotificationBaseUrl { get; set; }

    /// <summary>The shared secret notification-svc's <c>/v1/internal/notify/**</c> demands.</summary>
    public string? NotificationInternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // C059 — the outbound hops
    // -------------------------------------------------------------------------------------------

    /// <summary>provisioning-svc's base address (C030), for US-13.12's tracker binding.</summary>
    /// <remarks>
    /// <b>Unset means <c>POST /v1/fleets/{id}/trackers/bind</c> is not mapped.</b> The alternative —
    /// a route that accepts a bind and does nothing — would leave an operator believing an ST-901
    /// was armed on a bus that is not being tracked. T-02's credential mint is provisioning-svc's
    /// and cannot be done here: this service holds no CA.
    /// </remarks>
    public string? ProvisioningBaseUrl { get; set; }

    /// <summary>subscription-svc's base address (C048), for the Epic 23 org-scoped proxies.</summary>
    /// <remarks>
    /// <b>Unset means the Mode B request, subscriber and payment routes are not mapped</b>
    /// (SCR-FP-011/012). Those screens are the operator's only view of who is paying for a school
    /// van's seats, and a proxy that answered from nowhere would be a screen full of zeroes.
    /// </remarks>
    public string? SubscriptionBaseUrl { get; set; }

    /// <summary>The timeout on a forwarded hop to another MageRide service.</summary>
    /// <remarks>
    /// D6' §8.3's internal-hop budget. A proxy adds no retry of its own — the caller did not ask for
    /// one, and a retried <c>accept</c> would be a second grant.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan ProxyTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------------------------
    // C059 — map, analytics and geofences
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How old a position may be and still be drawn on the fleet map.
    /// </summary>
    /// <remarks>
    /// <b>No spec for the fleet map specifically</b>; US-7.16/7.17 make the same judgement for the
    /// passenger map, where a vehicle whose tracker has gone quiet is removed rather than left at
    /// its last fix. Fifteen minutes is long enough to survive a tunnel and a cadence drop
    /// (US-5.5's adaptive rate) and short enough that a parked bus does not haunt the map overnight.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan MapStaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The widest date range <c>GET /analytics</c> will evaluate.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> The distance sum is a window function over every telemetry sample in the
    /// range, and an unbounded range on a fleet of two hundred vehicles is a query that runs for
    /// minutes and returns a number nobody was waiting for. 92 days is a quarter, which is the
    /// longest period an operator's report is likely to be about.
    /// </remarks>
    [Range(1, 366)]
    public int MaxAnalyticsDays { get; set; } = 92;

    /// <summary>Geofences one organisation may define.</summary>
    /// <remarks><b>No spec.</b> A backstop on a route that replaces a set wholesale.</remarks>
    [Range(1, 1_000)]
    public int MaxGeofences { get; set; } = 100;

    /// <summary>Vertices one geofence ring may have.</summary>
    /// <remarks>
    /// <b>No spec.</b> A hand-drawn operating zone is tens of points; ten thousand is a traced
    /// coastline, which is a payload rather than a zone.
    /// </remarks>
    [Range(4, 10_000)]
    public int MaxGeofenceVertices { get; set; } = 1_000;
}
