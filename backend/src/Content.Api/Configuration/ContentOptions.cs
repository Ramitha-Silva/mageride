using System.ComponentModel.DataAnnotations;

namespace MageRide.Content.Configuration;

/// <summary>
/// content-svc's settings. D7' §4.2 gives this service exactly one — <c>Cache__Ttl</c>=300 — and
/// everything else here is argued at its declaration.
/// </summary>
/// <remarks>
/// <para>
/// The section is <c>Content</c>, but <b><see cref="CacheTtl"/> is also readable as
/// <c>Cache:Ttl</c></b>, because that is how D7' §4.2 spells it and how
/// <c>infra/env/.env.app.example</c> ships it: an unprefixed key in a file that every co-located
/// service loads. <c>AddContentServices</c> binds both and lets <c>Content:CacheTtl</c> win, so an
/// operator who set the documented variable is not left setting a key nothing reads — the same
/// problem reputation-svc's <c>Reputation__Outbox__*</c> block records.
/// </para>
/// <para>
/// <b>Two of these change who may publish and how fast a change is seen</b>, which is why both are
/// announced at start-up rather than left to be discovered: <see cref="PublishOnEdit"/> removes the
/// approval step D3' asks for, and <see cref="InvalidationEnabled"/> turns "a template edit is live
/// now" into "a template edit is live within <see cref="CacheTtl"/>".
/// </para>
/// </remarks>
public sealed class ContentOptions
{
    public const string SectionName = "Content";

    /// <summary>The D7' §4.2 spelling of <see cref="CacheTtl"/>: <c>Cache__Ttl</c>, in seconds.</summary>
    public const string LegacyCacheSection = "Cache";

    // -------------------------------------------------------------------------------------------
    // Caching (D7' §4.2 `Cache__Ttl`, D6' §7)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How long a cached dataset is served before it is re-read — D7' §4.2's <c>Cache__Ttl</c>=300.
    /// </summary>
    /// <remarks>
    /// This is the number the C045 definition of done measures: "a template change is visible to
    /// notification-svc within the documented cache TTL". It is also the <c>max-age</c> the two
    /// public endpoints advertise, so an intermediary caches for the window this service does rather
    /// than a guess of its own.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Cache reads at all. On.
    /// </summary>
    /// <remarks>
    /// Off means every notification render is a database round trip. Correct, and the thing the
    /// cache exists to avoid — E-01 sends an offer push to every candidate driver of every ride.
    /// </remarks>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>
    /// Ceiling on cached entries. A backstop against a caller-supplied key growing the cache, not a
    /// working limit.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> The five datasets need about a dozen entries between them; one key family — the
    /// template key on the internal render route — comes from a caller, and negative results are cached
    /// too, so without a ceiling the only thing bounding this service's memory would be the internal
    /// key. At the ceiling reads are served uncached and the fact is logged.
    /// </remarks>
    [Range(16, 1_000_000)]
    public int MaxCacheEntries { get; set; } = 1_000;

    /// <summary>
    /// Publish and honour cross-replica cache purges over Redis pub/sub. On.
    /// </summary>
    /// <remarks>
    /// The in-process cache is per replica, so a publish on one instance leaves every other
    /// instance serving the old template until its own entry expires. Off is not a broken
    /// deployment — it is the TTL-only behaviour the DoD's worst case describes — but it is a
    /// different promise, so it is logged.
    /// </remarks>
    public bool InvalidationEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // The approval workflow (D3' `PUT /v1/admin/content/{key}`)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Make an admin template edit live immediately instead of drafting it. <b>Off.</b>
    /// </summary>
    /// <remarks>
    /// D3' calls the edit route a "versioned template edit (approval workflow)" and
    /// <c>content.notification_templates.approved_by</c> exists to record who approved it, so the
    /// default is draft → <c>POST …/approve</c>. On, the author is also the approver and the column
    /// records them twice; the response's <c>status</c> always says which happened, so a portal
    /// never has to assume.
    /// </remarks>
    public bool PublishOnEdit { get; set; }

    // -------------------------------------------------------------------------------------------
    // The internal plane (D3' §0 — mTLS; a shared key until C042's mesh identity lands)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Shared secret for <c>GET /v1/content/templates/{key}</c> and
    /// <c>POST /v1/internal/content/cache/purge</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3' marks the template read "mTLS internal". Unlike every other internal family on the
    /// platform it is <b>not</b> under the <c>/v1/internal/**</c> prefix the gateway refuses — D3'
    /// prints the path under <c>/v1/content</c> and <c>gateway-routes.json</c> forwards that prefix
    /// — so the guard has to be in the service.
    /// </para>
    /// <para>
    /// <b>Unset leaves the template read open to any caller that reaches this service</b>, rather
    /// than unmapping it the way registry-svc and trip-state-svc unmap theirs. A template body with
    /// placeholders is not a secret, and unmapping the route would stop every notification on the
    /// platform rendering — the failure would land on notification-svc looking like a bug there.
    /// It is logged loudly at start-up, the same trade reputation-svc's gRPC surface makes.
    /// The purge route <i>is</i> unmapped without it: that one is a write.
    /// </para>
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // AL-28's carousel
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Absolute base that turns a stored <c>illustration_ref</c> into a URL. Unset = serve the
    /// reference as stored.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it and unset is the intended state.</b> AL-28 calls the carousel "pure
    /// presentation — no new API", the illustrations ship in the app bundle, and D7' §4 names four
    /// object-storage buckets, all private. Setting this is how a deployment moves the artwork to a
    /// CDN without an app release; a stored ref that is already an absolute URL is left alone.
    /// </remarks>
    public string? AssetBaseUrl { get; set; }

    // -------------------------------------------------------------------------------------------
    // Bounds. Neither is a spec value; both exist so an admin cannot make a read unbounded.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Most FAQ articles one answer may carry. Truncation is logged, never silent.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> US-16.1's four topics are 12 rows in three languages (1902); 500 is three
    /// orders of magnitude of editorial growth and still one small response.
    /// </remarks>
    [Range(1, 5_000)]
    public int MaxFaqItems { get; set; } = 500;

    /// <summary>
    /// Most active broadcasts one answer may carry. Truncation is logged, never silent.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> US-14.8 is a banner: a client shows one at a time and the newest matters
    /// most, which is the order they come back in.
    /// </remarks>
    [Range(1, 500)]
    public int MaxBroadcasts { get; set; } = 50;
}
