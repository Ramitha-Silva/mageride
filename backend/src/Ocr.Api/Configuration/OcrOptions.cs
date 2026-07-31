using System.ComponentModel.DataAnnotations;

namespace MageRide.Ocr.Configuration;

/// <summary>
/// ocr-svc's knobs. Every default is argued at its declaration; the ones with no spec behind them
/// say so.
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>The interim shared secret <c>/v1/internal/ocr/**</c> demands, until mTLS (C042).</summary>
    /// <remarks>
    /// <b>Unset leaves the internal family unmapped</b> — the posture registry-svc, notification-svc,
    /// safety-svc and support-svc take. What is behind it is a route that reads any
    /// <c>docs.uploads</c> row by id and returns what is written on it: somebody's licence number,
    /// their NIC, their address. There is no public surface on this service at all.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    /// <summary>
    /// The confidence at or above which a field is <c>auto_verified</c> rather than sent to a
    /// Verification Officer.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins the number.</b> AL-29, BR-25.2 and D6' §7.5 all say "below threshold" and none
    /// of them says what it is. <b>0.80, the same value and the same argument as
    /// <c>Registry:OcrConfidenceThreshold</c></b> — the two services apply the rule to the same
    /// document and a deployment that set them differently would have the officer queue and the
    /// step verdict disagree about it. Bounded at 0.5 so it cannot be turned into "trust everything".
    /// </remarks>
    [Range(0.5, 1.0)]
    public decimal ConfidenceThreshold { get; set; } = 0.80m;

    /// <summary>
    /// The ceiling every field read by the on-prem fallback is clamped to.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it either</b>, and it is not a statement about Tesseract's accuracy — it is
    /// D6' §7.5's "Gemini down/low-confidence → Tesseract on-prem; below threshold → manual admin
    /// review" made structural. Kept below <see cref="ConfidenceThreshold"/>, and validated against
    /// it at start-up, so a Gemini outage can never auto-approve a vehicle (AL-27) on a keyword
    /// match. 0.60 leaves room to tell a clean fallback read from a poor one in the officer queue.
    /// </remarks>
    [Range(0.0, 0.99)]
    public decimal TesseractConfidenceCeiling { get; set; } = 0.60m;

    /// <summary>NFR-28's raw-document retention, stamped on <c>docs.uploads.auto_delete_at</c>.</summary>
    /// <remarks>
    /// The sweeper is not this service's — 1301's <c>ix_uploads_auto_delete</c> is the index it will
    /// scan, and nothing in this build runs it (C125). What this service owes is a deadline on every
    /// row it processes, because it is the first thing on the platform that reads those bytes and a
    /// licence photograph with no deadline is one kept for ever by omission.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Where raw uploads are read from. Not object storage — see <see cref="Storage"/>.</summary>
    public StorageOptions Storage { get; set; } = new();

    public GeminiOptions Gemini { get; set; } = new();

    public TesseractOptions Tesseract { get; set; } = new();

    public RedactionOptions Redaction { get; set; } = new();

    public QueueOptions Queue { get; set; } = new();

    /// <summary>Where the bytes are, and how big one is allowed to be.</summary>
    public sealed class StorageOptions
    {
        /// <summary>
        /// The directory <c>docs.uploads.storage_url</c> paths resolve under.
        /// </summary>
        /// <remarks>
        /// <b>Not an S3 client.</b> D-36 puts raw documents on an SSE-KMS bucket with signed-URL
        /// access, and no service in this build talks to one (C125) — support-svc's screenshot store
        /// is the same seam and the same note. What this service holds is
        /// <c>IRawDocumentStore</c>, one method, so the swap is one class. The deadline on
        /// <c>docs.uploads.auto_delete_at</c> and the fact that the bytes never leave unredacted are
        /// this service's regardless of where they sit.
        /// </remarks>
        public string? Root { get; set; }

        /// <summary>
        /// Ceiling on one document.
        /// </summary>
        /// <remarks>
        /// <b>No spec pins it</b> — the same bound as <c>Support:ScreenshotMaxBytes</c> and
        /// <c>Ride:ProofPhotoMaxBytes</c>, because all three are a phone photograph. A document over
        /// it is refused before it is decoded: the redaction pass allocates the whole image.
        /// </remarks>
        [Range(64 * 1024, 64L * 1024 * 1024)]
        public long MaxBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>Allow reading over http(s) as well as from the mounted root.</summary>
        /// <remarks>
        /// Off by default. A <c>storage_url</c> is a value from another service's table, and a
        /// service that will fetch any URL written into a row it does not own is one SSRF away from
        /// reading the cluster's metadata endpoint.
        /// </remarks>
        public bool AllowHttpSources { get; set; }
    }

    /// <summary>D6' §7.5's primary path.</summary>
    public sealed class GeminiOptions
    {
        /// <summary>Off leaves every document on the on-prem path. Not a fault — a posture.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Generative Language API root. Unset ⇒ Gemini is never called.</summary>
        public string? BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

        /// <summary>Unset ⇒ Gemini is never called, and the service says so at start-up.</summary>
        public string? ApiKey { get; set; }

        /// <summary>D6' §7.5 and ADD §12.5 both name Flash 3.0 by version.</summary>
        public string Model { get; set; } = "gemini-flash-3.0";

        /// <summary>
        /// D6' §8.3's OCR timeout, applied per attempt.
        /// </summary>
        /// <remarks>
        /// The spec's 30 s is the budget for the whole extraction, which also has to fetch the
        /// bytes, run Tesseract and re-encode; the model gets the larger part of it and
        /// <see cref="QueueOptions.JobTimeout"/> is the ceiling on all of it.
        /// </remarks>
        [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>Enough for a dozen fields and their confidences, and nothing like enough for prose.</summary>
        [Range(256, 32768)]
        public int MaxOutputTokens { get; set; } = 2048;

        /// <summary>D6' §8.3: 3 attempts, exponential with jitter, on the transient failures only.</summary>
        [Range(1, 5)]
        public int Attempts { get; set; } = 3;
    }

    /// <summary>D6' §7.5's fallback, and ADD §12.5's source of redaction boxes.</summary>
    public sealed class TesseractOptions
    {
        /// <summary>
        /// The binary. Resolved on <c>PATH</c> when it is a bare name.
        /// </summary>
        /// <remarks>
        /// <b>Absent ⇒ nothing is sent to Gemini at all.</b> The pre-pass gets its ID-number boxes
        /// from this engine, so no engine means no redaction, and D-36 fails closed.
        /// </remarks>
        [Required]
        public string ExecutablePath { get; set; } = "tesseract";

        /// <summary>
        /// Tesseract language packs.
        /// </summary>
        /// <remarks>
        /// <b><c>eng</c> only, and deliberately.</b> D-26's trilingual rule is about strings MageRide
        /// authors; a Sri Lankan licence, insurance certificate and revenue licence print the
        /// machine-readable fields — numbers, dates, plates, licence classes — in Latin script, and
        /// the Sinhala and Tamil on them is the boilerplate. Adding <c>sin+tam</c> costs every
        /// document the extra passes and changes no field this service extracts.
        /// </remarks>
        public string Language { get; set; } = "eng";

        /// <summary>
        /// Tesseract's page-segmentation mode. 3 is "fully automatic".
        /// </summary>
        /// <remarks>
        /// <b>Not 11 ("sparse text"), which is the intuitive choice and is wrong here.</b> A form —
        /// a licence, an insurance certificate — reads identically under both. A <b>number plate</b>
        /// does not: framed by its border and sitting alone on the image, PSM 11 returns
        /// <em>nothing at all</em> for it while PSM 3 reads it cleanly. Step 4/4 is the one this
        /// service cannot afford to be blind on, so the default is the mode that sees it.
        /// </remarks>
        [Range(0, 13)]
        public int PageSegmentationMode { get; set; } = 3;

        /// <summary>
        /// The mode retried when <see cref="PageSegmentationMode"/> reads nothing at all.
        /// </summary>
        /// <remarks>
        /// The two modes fail on different material, and which one a given document needs is not
        /// knowable in advance — so a page that came back empty is read once more the other way
        /// before it is called unreadable. Only on an empty page, so the common document pays
        /// nothing. Set to the same value as <see cref="PageSegmentationMode"/> to disable.
        /// </remarks>
        [Range(0, 13)]
        public int FallbackPageSegmentationMode { get; set; } = 11;

        [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>Where a document is staged for the child process. A mount, not <c>TMPDIR</c>.</summary>
        public string? WorkRoot { get; set; }
    }

    /// <summary>D-36 / ADD §12.5's pre-pass.</summary>
    public sealed class RedactionOptions
    {
        /// <summary>
        /// The OpenCV Haar cascade. Unset probes the well-known <c>opencv-data</c> locations.
        /// </summary>
        /// <remarks>Not found ⇒ no face blur ⇒ nothing is sent to Gemini.</remarks>
        public string? FaceCascadePath { get; set; }

        /// <summary>Width the image is scaled to for detection. Bounds the work on a large scan.</summary>
        [Range(320, 4096)]
        public int DetectionWidth { get; set; } = 1024;

        /// <summary>
        /// The smallest face, as a fraction of the shorter side of the (scaled) page.
        /// </summary>
        /// <remarks>
        /// A portrait on an identity document is a substantial part of it. A floor stops the
        /// detector spending its time on 20 px false positives in the background of a vehicle
        /// photograph — and a false positive there is a blurred rectangle over the number plate.
        /// </remarks>
        [Range(0.01, 0.5)]
        public double MinimumFaceFraction { get; set; } = 0.08;

        /// <summary>Blur kernel as a fraction (1/N) of the region's shorter side.</summary>
        [Range(2, 32)]
        public int BlurDivisor { get; set; } = 6;

        /// <summary>Floor on the kernel, so a small region is still destroyed rather than softened.</summary>
        [Range(3, 199)]
        public int MinimumBlurKernel { get; set; } = 21;

        /// <summary>
        /// Re-encode a JPEG as a JPEG rather than as PNG.
        /// </summary>
        /// <remarks>
        /// Off. A second JPEG generation over a freshly blacked-out rectangle leaves ringing along
        /// its edges — faint, and a partial reconstruction of the glyphs that were under it. PNG
        /// costs bandwidth on a hop that is already sending an image.
        /// </remarks>
        public bool PreserveJpeg { get; set; }
    }

    /// <summary>The extraction worker (ADD §6: "stateless, queue-driven").</summary>
    public sealed class QueueOptions
    {
        /// <summary>
        /// How many documents may wait at once.
        /// </summary>
        /// <remarks>
        /// Bounded, and a full queue is a refusal rather than a wait: the caller's own budget is
        /// D6' §8.3's 30 s, and a document that queued behind two hundred others has already lost
        /// it. registry-svc treats the refusal as an unavailable extraction, saves the step and
        /// sends it to an officer.
        /// </remarks>
        [Range(1, 10000)]
        public int Capacity { get; set; } = 256;

        /// <summary>
        /// Concurrent workers.
        /// </summary>
        /// <remarks>
        /// The pipeline is dominated by two things that are not this process's CPU — a child
        /// Tesseract and a network hop — so this is not a core count. Four keeps four Tesseract
        /// processes at most, which is what bounds the memory on a small node.
        /// </remarks>
        [Range(1, 64)]
        public int Workers { get; set; } = 4;

        /// <summary>D6' §8.3's "OCR 30 s", for the whole job: fetch, redact, extract, persist.</summary>
        [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
        public TimeSpan JobTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}
