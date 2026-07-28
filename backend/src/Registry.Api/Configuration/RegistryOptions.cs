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
    /// whatever fare-svc (C046) sends. <b>Unset means the routes are not mapped at all</b> — a
    /// deployment that forgets it gets 404s rather than an unauthenticated write to
    /// <c>registry.driver_payouts</c>, and the missing merchant binding then surfaces as
    /// <c>402 merchant-not-onboarded</c> at <c>POST /v1/fare/pay</c> (D-11).
    /// </remarks>
    public string? InternalApiKey { get; set; }
}
