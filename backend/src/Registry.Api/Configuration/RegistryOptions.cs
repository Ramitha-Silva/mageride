using System.ComponentModel.DataAnnotations;

namespace MageRide.Registry.Configuration;

/// <summary>registry-svc's own settings.</summary>
public sealed class RegistryOptions
{
    public const string SectionName = "Registry";

    /// <summary>
    /// The confidence at or above which an ocr-svc field is <c>auto_verified</c> rather than sent
    /// to a Verification Officer (AL-29, D6' §7.5 "below threshold → manual admin review").
    /// </summary>
    /// <remarks>
    /// <b>No spec pins the number.</b> AL-29, BR-25.2 and D6' §7.5 all say "below threshold" and
    /// none of them says what it is, so 0.80 is registry-svc's choice and is configurable per
    /// deployment — the same situation as <c>Dispatch:SearchRadiusM</c> (C023). Raising it sends
    /// more fields to the officer queue; lowering it auto-approves vehicles on shakier readings,
    /// which is the failure AL-29 exists to prevent, so it is bounded at 0.5 below.
    /// </remarks>
    [Range(0.5, 1.0)]
    public decimal OcrConfidenceThreshold { get; set; } = 0.80m;

    /// <summary>Whether the E-03 nightly document-expiry sweep runs in this process.</summary>
    /// <remarks>
    /// On by default: E-03 is a platform guarantee, and a deployment that silently did not run it
    /// would let expired insurance keep dispatching. Turned off in tests, which drive
    /// <c>DocumentExpiryWorker.SweepOnceAsync</c> directly rather than waiting on a ticker.
    /// </remarks>
    public bool DocumentExpiryEnabled { get; set; } = true;

    /// <summary>
    /// How often the sweep looks. E-03 says "nightly"; this is hourly so a restart, a clock skew
    /// or a deployment window cannot cost a night, and <c>registry.document_notices</c> makes the
    /// extra passes free — the second one in a night finds nothing to emit.
    /// </summary>
    public TimeSpan DocumentExpiryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many documents one sweep claims. Bounds a backlog's first pass.</summary>
    [Range(1, 10_000)]
    public int DocumentExpiryBatchSize { get; set; } = 500;

    /// <summary>
    /// Whether <c>POST /v1/dev/vehicles/{vehicleId}/approve</c> is mapped at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> — the default — means "Development only". Set it explicitly to
    /// <see langword="true"/> for the lightweight production replica, which runs synthetic data
    /// under the Production environment name and still needs an approved vehicle to book a ride
    /// against (<c>specs/lightweight-production-replica.md</c>).
    /// </para>
    /// <para>
    /// This is a seed path, not the real approval. AL-10 makes a valid insurance document
    /// mandatory before any vehicle reaches <c>APPROVED</c>, and AL-30 derives approval from the
    /// four onboarding steps — <b>this endpoint checks neither</b>, because C021 is fenced out of
    /// document upload and OCR entirely. C029 owns the gate. When the route is off it is not
    /// mapped rather than answering 403, so nothing about it is discoverable in a deployment that
    /// did not ask for it.
    /// </para>
    /// </remarks>
    public bool? DevApprovalEnabled { get; set; }

    /// <summary>
    /// Shared secret for <c>/v1/internal/vehicles/**</c>, presented in
    /// <c>X-MageRide-Internal-Key</c>.
    /// </summary>
    /// <remarks>
    /// D3' §0 puts the internal family on service-to-service mTLS and the gateway refuses the
    /// prefix at the edge (C008); this is the interim until C042 lands a mesh, and it must equal
    /// whatever admin-bff (C063) sends. <b>Unset means the routes are not mapped at all</b> — a
    /// deployment that forgets it gets 404s rather than an unauthenticated write.
    /// <b>Δ AL-57:</b> the family is now one route, AL-30's onboarding recompute. The D-11 merchant
    /// bind is retired — OnePay has one merchant account per merchant, so the per-driver binding it
    /// wrote never existed.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -----------------------------------------------------------------------------------------
    // The driver's bank & payout profile (Δ AL-58/AL-59)
    // -----------------------------------------------------------------------------------------

    /// <summary>Where payout documents are written until D-36's bucket exists (C125).</summary>
    /// <remarks>
    /// <b>Unset ⇒ a temporary directory</b>, which a restart can take an officer's evidence with.
    /// Warned at construction. Not object storage: this service holds no signing key and no bucket
    /// client, exactly as fleet-svc holds none for the same slots — admin-bff mints the signed URL
    /// an officer opens them by (C063).
    /// </remarks>
    public string? PayoutDocumentRoot { get; set; }

    /// <summary>Ceiling on one payout document.</summary>
    /// <remarks><b>No spec</b> — the same 8 MiB bound fleet-svc uses for the identical slots.</remarks>
    public long PayoutDocumentMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>How long a raw payout document is kept (NFR-28).</summary>
    /// <remarks>
    /// Δ D-36: written to <c>docs.uploads.auto_delete_at</c> <b>and</b> applied by the bucket's own
    /// lifecycle rule, so the deadline is enforced rather than merely recorded. It does <b>not</b>
    /// reach a driver's LankaQR — that is stored <c>retained</c>, because AL-59 makes it what a
    /// passenger scans to pay them on every ride rather than evidence somebody checked once.
    /// </remarks>
    public TimeSpan PayoutDocumentRetention { get; set; } = TimeSpan.FromDays(90);

    // -----------------------------------------------------------------------------------------
    // Onboarding documents (Δ MCS-01) — the licence, the profile photo and the four vehicle docs
    // -----------------------------------------------------------------------------------------

    /// <summary>Ceiling on one onboarding document.</summary>
    /// <remarks>
    /// <b>No spec</b> — the same 8 MiB bound fleet-svc and the payout slots use. Named separately
    /// from <see cref="PayoutDocumentMaxBytes"/> because these are photographs taken on a handset
    /// and those are exports from a banking app; the two will not stay the same number forever.
    /// </remarks>
    public long OnboardingDocumentMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>How long a raw onboarding document is kept (NFR-28).</summary>
    /// <remarks>
    /// <b>Always set.</b> Every one of these is raw identity evidence — a licence, a NIC, a plate —
    /// so unlike the payout slots there is no retained class here and no exception to argue about:
    /// the extraction outlives the image, which is the whole of NFR-28.
    /// </remarks>
    public TimeSpan OnboardingDocumentRetention { get; set; } = TimeSpan.FromDays(90);

    // -----------------------------------------------------------------------------------------
    // C054 — the ocr-svc hop (D6' §7.5)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Where ocr-svc is.
    /// </summary>
    /// <remarks>
    /// <b>Unset leaves <see cref="Onboarding.UnconfiguredDocumentExtractionClient"/> in place</b>,
    /// so every document comes back unread and every document step lands <c>pending_review</c>.
    /// That is a working deployment — a Verification Officer confirms each field — it just has no
    /// AL-27 auto-approval in it, and the service says so at start-up.
    /// </remarks>
    public string? OcrBaseUrl { get; set; }

    /// <summary>Must equal ocr-svc's <c>Ocr:InternalApiKey</c>, or every extraction is a 404.</summary>
    public string? OcrInternalApiKey { get; set; }

    /// <summary>
    /// How long to wait for one document.
    /// </summary>
    /// <remarks>
    /// D6' §8.3 budgets 30 s for OCR and ocr-svc bounds its own pass at that; this is a little
    /// longer so the timeout that fires first is the one that can say <em>which</em> stage was slow.
    /// A step save waits on this, so it is also the worst case for the driver's screen — and the
    /// answer when it expires is a saved step, not an error.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:05:00")]
    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromSeconds(35);
}
