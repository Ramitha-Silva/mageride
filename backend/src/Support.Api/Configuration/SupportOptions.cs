using System.ComponentModel.DataAnnotations;

namespace MageRide.Support.Configuration;

/// <summary>
/// support-svc's knobs. Every default is argued at its declaration; the ones with no spec behind
/// them say so.
/// </summary>
public sealed class SupportOptions
{
    public const string SectionName = "Support";

    /// <summary>The interim shared secret <c>/v1/internal/support/**</c> demands, until mTLS (C042).</summary>
    /// <remarks>
    /// <b>Unset leaves the internal family unmapped</b>, the posture ride-svc, registry-svc,
    /// notification-svc and safety-svc take. What is behind it is the whole agent queue: an open
    /// resolve route is a way to close any complaint on the platform, in the complainant's name,
    /// with any words you like — and the user is shown that text verbatim.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // US-16.2 — the screenshot
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Where screenshot bytes are written.
    /// </summary>
    /// <remarks>
    /// <b>Not object storage.</b> D-36 puts uploaded images on SSE-KMS buckets and D7' §4.2 names
    /// <c>Storage__ScreenshotBucket</c> for this service; no service in this build has an S3 client
    /// (C125), so the bytes go to a configured directory and <see cref="ScreenshotRoot"/> is the
    /// deployment's mount point. The <c>docs.uploads</c> row is written either way, which is what
    /// makes the swap one class.
    /// </remarks>
    public string? ScreenshotRoot { get; set; }

    /// <summary>
    /// Ceiling on one upload.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it</b> — the same bound and the same number as <c>Ride:ProofPhotoMaxBytes</c>
    /// and <c>Subscription:SlipMaxBytes</c>, because all three are a phone photograph. The
    /// idempotency middleware's request buffer is raised to match in
    /// <see cref="SupportApplication"/>, or it would answer <c>413</c> first with a message about
    /// buffering rather than about the screenshot.
    /// </remarks>
    [Range(64 * 1024, 64L * 1024 * 1024)]
    public long ScreenshotMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// NFR-28's raw-document retention. Written to <c>docs.uploads.auto_delete_at</c> at upload.
    /// </summary>
    /// <remarks>
    /// The sweeper is not this service's — 1301's <c>ix_uploads_auto_delete</c> is the index it will
    /// scan and nothing in this build runs it. What this service owes is a correct deadline on the
    /// row, so a screenshot of somebody's wallet screen is not kept for ever by omission.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan ScreenshotRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Signs the expiring URL on <c>TicketDetail.screenshotUrl</c>.
    /// </summary>
    /// <remarks>
    /// <b>Unset means a key generated per process</b>, which is correct for one instance and wrong
    /// for several: a link minted by replica A does not verify on replica B, and the user's ticket
    /// shows a broken image the server produced a second ago. Announced at start-up. Same trade
    /// subscription-svc's <c>FileLinkSigningKey</c> makes, for the same reason.
    /// </remarks>
    public string? FileLinkSigningKey { get; set; }

    /// <summary>
    /// How long one of those links lives.
    /// </summary>
    /// <remarks>
    /// <b>No spec</b>: long enough to render a ticket detail and open the image, short enough that a
    /// link copied out of a screenshot of the screenshot is dead before it can be shared.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan FileLinkTtl { get; set; } = TimeSpan.FromMinutes(15);

    // -------------------------------------------------------------------------------------------
    // US-16.1 — the FAQ
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows one FAQ read returns at most.
    /// </summary>
    /// <remarks>
    /// <b>No spec</b> — a bound, and truncation is logged rather than silent. One more than this is
    /// asked for, so a full page can be told from a truncated one without a second count query. The
    /// same arrangement, and the same default, as <c>Content:MaxFaqItems</c>: the two read the same
    /// table and a smaller cap here would silently disagree with content-svc's own answer.
    /// </remarks>
    [Range(1, 5_000)]
    public int MaxFaqItems { get; set; } = 500;

    // -------------------------------------------------------------------------------------------
    // Bounds
    // -------------------------------------------------------------------------------------------

    /// <summary>Rows a ticket list or a queue read returns at most (D3' §0 caps a page at 100).</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 50;

    /// <summary>
    /// Events one thread returns at most.
    /// </summary>
    /// <remarks>
    /// <b>No spec</b>. A thread is a handful of entries and this is a backstop, not a working limit;
    /// it is applied oldest-first so a truncated thread loses the newest replies rather than the
    /// complaint, and it is logged when it bites.
    /// </remarks>
    [Range(10, 5_000)]
    public int MaxThreadEvents { get; set; } = 200;
}
