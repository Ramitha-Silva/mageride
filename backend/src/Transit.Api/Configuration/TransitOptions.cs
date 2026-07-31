using System.ComponentModel.DataAnnotations;

namespace MageRide.Transit.Configuration;

/// <summary>
/// transit-svc's knobs. Every default is argued at its declaration; the ones with no spec behind
/// them say so.
/// </summary>
public sealed class TransitOptions
{
    public const string SectionName = "Transit";

    /// <summary>
    /// How far a passenger is assumed to walk to a halt.
    /// </summary>
    /// <remarks>
    /// BR-23.2: "within an admin halt-radius, **default 400 m**"; D6' I-32.1 repeats the number. It
    /// is the one parameter that decides whether a corridor has a direct route at all, so it is a
    /// setting rather than a constant — and it is bounded, because a 5 km "walk" would return every
    /// route in the city as reachable.
    /// </remarks>
    [Range(50, 2000)]
    public int HaltRadiusM { get; set; } = 400;

    /// <summary>
    /// How many halts near each end are considered.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> A dense interchange puts a dozen halts inside 400 m and every extra
    /// one multiplies the transfer search; twelve covers a Colombo interchange and bounds the work.
    /// Nearest first, so the ones dropped are the furthest walk.
    /// </remarks>
    [Range(1, 50)]
    public int MaxHaltsPerEnd { get; set; } = 12;

    /// <summary>Ceiling on the options returned. Direct options are never dropped for a transfer.</summary>
    /// <remarks><b>No spec pins it.</b> SCR-PA-009 is a scrollable list, not a top-3.</remarks>
    [Range(1, 200)]
    public int MaxOptions { get; set; } = 50;

    /// <summary>
    /// Whether transfer options are computed at all.
    /// </summary>
    /// <remarks>
    /// On. BR-23.2 asks for them "listed below direct options"; the switch exists because the
    /// transfer search is the only part of the request whose cost grows with the feed, and an
    /// operator staring at a slow endpoint should be able to turn off the expensive half rather
    /// than the screen.
    /// </remarks>
    public bool TransferOptionsEnabled { get; set; } = true;

    /// <summary>How many halts may serve as an interchange between two patterns.</summary>
    /// <remarks><b>No spec.</b> A bound on the transfer search, not a routing rule.</remarks>
    [Range(1, 20)]
    public int MaxTransferOptions { get; set; } = 10;

    // -----------------------------------------------------------------------------------------
    // The feed cache (AL-54's `transit_feed_activated`)
    // -----------------------------------------------------------------------------------------

    /// <summary>The Postgres channel activation signals on.</summary>
    /// <remarks>D6' I-32.1 names it: "one transaction swaps live tables → `NOTIFY transit_feed_activated`".</remarks>
    public string FeedChannel { get; set; } = "transit_feed_activated";

    /// <summary>
    /// The safety-net poll, for a <c>NOTIFY</c> that never arrived.
    /// </summary>
    /// <remarks>
    /// <c>LISTEN</c> is the primary trigger and makes a reload near-instant. This is what keeps the
    /// ≤ 60 s guarantee true when the notification is lost — a dropped connection, a reconnect
    /// window, a PgBouncer in transaction mode — and 30 s leaves a whole missed cycle inside the
    /// bound. It costs one indexed row read.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan FeedPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether the cache is loaded and kept fresh at all.</summary>
    /// <remarks>
    /// <b>Off ⇒ every corridor degrades</b> as if no feed were active. Only sensible in a
    /// deployment with no transit data; the service says so at start-up.
    /// </remarks>
    public bool FeedCacheEnabled { get; set; } = true;

    // -----------------------------------------------------------------------------------------
    // AL-54 — the GTFS Dataset Manager (Δ C057)
    // -----------------------------------------------------------------------------------------

    public GtfsOptions Gtfs { get; set; } = new();

    // -----------------------------------------------------------------------------------------
    // AL-20 — the paste-link resolver
    // -----------------------------------------------------------------------------------------

    public MapsLinkOptions MapsLink { get; set; } = new();

    /// <summary>The SCR-AP-016 lifecycle: upload, validate, activate, roll back (AL-54).</summary>
    public sealed class GtfsOptions
    {
        /// <summary>
        /// The upload ceiling. <b>BR-32.1 pins it at 200 MB</b>, and so do D3' and
        /// <c>contracts/transit.yaml</c>.
        /// </summary>
        /// <remarks>
        /// It is a setting rather than a constant only so an operator can lower it — raising it
        /// past the spec would let a feed through that admin-bff and the gateway will refuse
        /// anyway, which is a worse failure than a 413 here.
        /// </remarks>
        [Range(1024 * 1024, 200L * 1024 * 1024)]
        public long MaxUploadBytes { get; set; } = 200L * 1024 * 1024;

        /// <summary>
        /// Where the original zips live.
        /// </summary>
        /// <remarks>
        /// <b>Not object storage.</b> D-36 and BR-32.3 put them on an SSE bucket with ≥ 12 months
        /// retention; no service in this build has an S3 client, so this is a directory and the
        /// service says so at start-up — the same interim ride-svc's <c>ProofPhotoRoot</c> is, for
        /// the same reason and with the same one-method swap. Unset ⇒ a directory under the
        /// system temp path, which a pod restart can lose.
        /// </remarks>
        public string? StorageRoot { get; set; }

        /// <summary>
        /// HMAC key for the download links, base64.
        /// </summary>
        /// <remarks>
        /// A signed URL <em>is</em> the credential — <c>GET …/objects/{id}</c> carries no bearer,
        /// because the 302 is followed by a browser that will not attach one. Unset outside
        /// Development is a failed start; unset in Development mints a per-process key, so links
        /// stop working across a restart and nothing silently serves a feed unsigned.
        /// </remarks>
        public string? DownloadSigningKey { get; set; }

        /// <summary>How long a signed download link stays valid.</summary>
        /// <remarks><b>No spec pins it.</b> Long enough for a browser to follow a 302 and download
        /// 200 MB on a bad connection; short enough that a link pasted into a ticket is dead.</remarks>
        [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
        public TimeSpan DownloadUrlTtl { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Origin the signed download URL is built on. Unset ⇒ the scheme and host the caller
        /// reached this service on, which behind the gateway is the gateway's.
        /// </summary>
        public string? PublicBaseUrl { get; set; }

        /// <summary>
        /// Whether the validation job runs in this process.
        /// </summary>
        /// <remarks>
        /// <b>Off ⇒ every upload sits at <c>uploaded</c> for ever</b> and nothing can ever be
        /// activated, because BR-32.2 admits only a <c>validated</c> or <c>archived</c> version.
        /// The service says so at start-up.
        /// </remarks>
        public bool ValidationEnabled { get; set; } = true;

        /// <summary>
        /// How often the validation worker looks for an unvalidated upload.
        /// </summary>
        /// <remarks>
        /// <b>No spec pins it.</b> The in-process latch is what makes validation start the instant
        /// an upload lands; this covers the upload that was accepted by a replica which then died,
        /// and the 2 s poll SCR-AP-016's status stepper is already doing means an operator sees
        /// the move without waiting a full interval.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
        public TimeSpan ValidationPollInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a feed may sit at <c>validating</c> before another worker may take it over.
        /// </summary>
        /// <remarks>
        /// <b>No spec.</b> Without it, a replica that dies mid-validation leaves an upload stuck at
        /// <c>Validating</c> in SCR-AP-016's stepper for ever, with no way past it but SQL. Fifteen
        /// minutes is comfortably longer than a 200 MB feed takes to read twice.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:01:00", "02:00:00")]
        public TimeSpan ValidationStaleAfter { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>BR-32.1: "warn if &lt; 30 days ahead".</summary>
        [Range(1, 365)]
        public int ServiceWindowWarnDays { get; set; } = 30;

        /// <summary>
        /// Ceiling on the rows written into <c>validation_report</c>.
        /// </summary>
        /// <remarks>
        /// <b>No spec pins it</b> — BR-32.1 asks for "a complete row-level report", and a feed
        /// whose <c>stop_times.txt</c> names a stop that does not exist is wrong on every one of
        /// half a million rows. The report is a <c>jsonb</c> column an operator downloads, not an
        /// archive: past this many the report says how many were dropped, which is the fact that
        /// actually helps ("all of them") where the other 495 000 rows would not.
        /// </remarks>
        [Range(50, 100_000)]
        public int MaxReportedIssues { get; set; } = 5_000;

        /// <summary>
        /// How long a caller waits for a concurrent activation before being answered 409.
        /// </summary>
        /// <remarks>
        /// <b>No spec.</b> Activation takes a session advisory lock for its whole run — the
        /// staging import as well as the swap — because two imports into one staging schema would
        /// interleave into a feed that is neither. A second operator waits rather than corrupting
        /// it, and is told rather than hanging.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan ActivationLockWait { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>The short-link resolver (BR-23.4). <b>No Google API is involved.</b></summary>
    public sealed class MapsLinkOptions
    {
        /// <summary>
        /// BR-23.4's budget: "3 s timeout, 1 retry → pick-on-map".
        /// </summary>
        /// <remarks>
        /// The whole resolve, including the retry — the sheet says "Reading link…" for this long and
        /// then offers the map. A per-attempt budget would make the worst case twice what the spec
        /// promises the user.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:00.500", "00:00:30")]
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>BR-23.4's "1 retry".</summary>
        [Range(0, 3)]
        public int Retries { get; set; } = 1;

        /// <summary>How many redirects a short link may take before it is refused.</summary>
        /// <remarks>
        /// <b>No spec.</b> A shortener normally uses one; four covers a consent interstitial without
        /// letting a chain walk somewhere unbounded.
        /// </remarks>
        [Range(1, 10)]
        public int MaxRedirects { get; set; } = 4;

        /// <summary>
        /// The only hosts this service will fetch.
        /// </summary>
        /// <remarks>
        /// <b>This is a fetch of a URL a user pasted, so the allowlist is the whole security
        /// story.</b> Without it the endpoint is an authenticated SSRF primitive: an attacker pastes
        /// <c>http://169.254.169.254/…</c> and the platform fetches the cluster's metadata endpoint
        /// on their behalf. Every hop of a redirect is re-checked against this list, not only the
        /// first — a shortener that redirects off-list is refused mid-chain.
        /// </remarks>
        public string[] AllowedHosts { get; set; } =
        [
            "maps.app.goo.gl",
            "goo.gl",
            "maps.google.com",
            "www.google.com",
            "google.com",
        ];
    }
}
