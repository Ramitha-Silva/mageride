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
}
