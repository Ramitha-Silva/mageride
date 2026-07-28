using System.ComponentModel.DataAnnotations;

namespace MageRide.Provisioning.Configuration;

/// <summary>provisioning-svc's own settings.</summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    /// <summary>
    /// How long two presentations of one IMEI may be apart and still count as a clone (T-08).
    /// </summary>
    /// <remarks>
    /// D6' §4.3 says "two devices presenting the same IMEI <b>within 24 h</b> → both quarantined",
    /// so the rule is a window and this is it. Outside the window the older binding is treated as
    /// stale and released rather than quarantined: an operator moving a tracker between vehicles a
    /// week later has not cloned anything, and quarantining both would need an admin to undo a
    /// legitimate re-provision. Inside it, both are held — <b>failing closed is the point</b>.
    /// </remarks>
    public TimeSpan AntiCloneWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the <c>imei:{imei}</c> cache entry lives (D6' §4.3 names 24 h).
    /// </summary>
    /// <remarks>
    /// The TTL is the backstop, not the mechanism: a revoke deletes the key and publishes on
    /// <see cref="MageRide.Shared.Caching.RedisKeys.TrackerCredentialChannel"/> immediately. What
    /// the TTL bounds is the damage from a revoke whose Redis write failed — 24 h is D6''s number
    /// and is far outside T-12's 60 s, which is why the durable outbox event exists as well.
    /// </remarks>
    public TimeSpan ImeiCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether the T-02 rotation sweep runs in this process.
    /// </summary>
    /// <remarks>
    /// On by default — a deployment that silently skipped it would let every device certificate
    /// lapse 90 days after provisioning. Turned off in tests, which drive
    /// <c>CredentialRotationWorker.SweepOnceAsync</c> directly rather than waiting on a ticker.
    /// </remarks>
    public bool RotationEnabled { get; set; } = true;

    /// <summary>How often the rotation sweep looks for credentials inside their renewal window.</summary>
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many bindings one rotation sweep claims.</summary>
    [Range(1, 10_000)]
    public int RotationBatchSize { get; set; } = 200;

    /// <summary>Whether the T-09 bulk mint worker runs in this process.</summary>
    public bool BulkMintEnabled { get; set; } = true;

    /// <summary>How often the bulk mint worker looks for a job with pending rows.</summary>
    /// <remarks>
    /// NFR-43 budgets 5,000 rows in ≤ 5 minutes — about 17 rows/s. The worker drains a claimed job
    /// in a tight loop and only comes back here when there is nothing left, so this interval is
    /// how quickly a *newly accepted* job starts, not the throughput.
    /// </remarks>
    public TimeSpan BulkMintInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many rows the bulk mint worker takes per claim.</summary>
    [Range(1, 1_000)]
    public int BulkMintBatchSize { get; set; } = 50;

    /// <summary>D3''s ceiling on a bulk CSV. Above it the answer is <c>413 too-many-rows</c>.</summary>
    [Range(1, 5_000)]
    public int BulkMaxRows { get; set; } = 5_000;

    /// <summary>
    /// Shared secret for <c>/v1/internal/trackers/**</c>, presented in
    /// <c>X-MageRide-Internal-Key</c>.
    /// </summary>
    /// <remarks>
    /// D3' §0 puts the internal family on service-to-service mTLS and the gateway refuses the
    /// prefix at the edge (C008); this is the interim until C042 lands a mesh, and it must equal
    /// whatever the tcp-adapter (C043) sends. <b>Unset means the routes are not mapped at all</b> —
    /// a deployment that forgets it gets 404s rather than an unauthenticated IMEI oracle, and the
    /// symptom is an adapter that refuses every device rather than one that admits any.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    /// <summary>
    /// Signing key for the bulk error-report URL. Unset means one is generated per process.
    /// </summary>
    /// <remarks>
    /// D3' calls <c>errorReportUrl</c> a "signed URL" and there is no object store in front of
    /// this service, so the report is served by the service itself under an HMAC the link carries.
    /// A per-process key is fine for a single instance and wrong behind more than one — a link
    /// minted by replica A would not verify on replica B — so a multi-replica deployment must set
    /// it. The service says so at start-up.
    /// </remarks>
    public string? ErrorReportSigningKey { get; set; }

    /// <summary>How long a bulk error-report link stays valid.</summary>
    public TimeSpan ErrorReportUrlTtl { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// The device PKI (T-02). Bound from <c>StepCa:*</c> and <c>Cred:*</c>, which is how D7' §4.2
/// spells them.
/// </summary>
public sealed class DevicePkiOptions
{
    public const string StepCaSectionName = "StepCa";
    public const string CredentialSectionName = "Cred";

    /// <summary>
    /// Directory holding the CA material, in step-ca's own layout —
    /// <c>certs/root_ca.crt</c>, <c>secrets/root_ca_key</c>, <c>certs/intermediate_ca.crt</c>,
    /// <c>secrets/intermediate_ca_key</c>, <c>secrets/psk_signing_key</c>.
    /// </summary>
    /// <remarks>
    /// <b>The layout is step-ca's on purpose.</b> D6' §4.2 names step-ca as the issuer and the
    /// dev compose mounts <c>provisioning-ca-data</c> at <c>/var/step</c> for it; matching the
    /// paths means swapping the embedded issuer for a real <c>step-ca</c> container is a
    /// configuration change rather than a migration of key material. EMQX reads
    /// <c>certs/root_ca.crt</c> out of the same directory as its <c>cacertfile</c>.
    /// </remarks>
    [Required]
    public string RootKeyPath { get; set; } = "/var/step";

    /// <summary>
    /// URL of a real step-ca, when there is one. <b>Unset means the embedded issuer.</b>
    /// </summary>
    /// <remarks>
    /// Recorded rather than used: C030 ships the embedded issuer only, and a configured URL is
    /// refused at start-up rather than silently ignored, because a deployment that set it believes
    /// its keys live in step-ca's HSM-backed store and not on a Docker volume.
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>Credential lifetime. D6' §4.2 and D7' §4.2's <c>Cred__RotationDays</c> say 90.</summary>
    [Range(1, 3650)]
    public int RotationDays { get; set; } = 90;

    /// <summary>
    /// How long before expiry a credential is rotated.
    /// </summary>
    /// <remarks>
    /// <b>Rotation is not revocation.</b> The replacement is minted while the old credential is
    /// still valid, and the old one keeps working until its own <c>expires_at</c> — a tracker in a
    /// vehicle parked out of coverage for a fortnight has to be able to come back and collect the
    /// new one. A rotation that invalidated the outgoing credential the moment it ran would brick
    /// exactly the devices that most need the overlap.
    /// </remarks>
    public TimeSpan RotationLeadTime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Base URL written into each certificate's CRL distribution point.
    /// </summary>
    /// <remarks>
    /// Empty means no CDP extension, which is what dev runs with: EMQX's
    /// <c>ssl_options.enable_crl_check</c> refuses a certificate whose CRL it cannot fetch, so a
    /// broker that starts before this service would refuse every tracker. The replica and
    /// production set it and turn the check on together — see <c>infra/deploy/emqx/emqx.conf</c>.
    /// </remarks>
    public string? CrlDistributionPoint { get; set; }

    /// <summary>Common name on the generated root, when this process creates one.</summary>
    public string RootCommonName { get; set; } = "MageRide Device Root CA";

    /// <summary>Common name on the generated intermediate.</summary>
    public string IntermediateCommonName { get; set; } = "MageRide Device Issuing CA";

    /// <summary>Years the generated root is valid for. Intermediates are re-issued from it.</summary>
    [Range(1, 50)]
    public int RootValidityYears { get; set; } = 10;
}
