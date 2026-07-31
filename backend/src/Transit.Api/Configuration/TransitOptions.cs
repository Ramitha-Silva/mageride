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
    // AL-20 — the paste-link resolver
    // -----------------------------------------------------------------------------------------

    public MapsLinkOptions MapsLink { get; set; } = new();

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
