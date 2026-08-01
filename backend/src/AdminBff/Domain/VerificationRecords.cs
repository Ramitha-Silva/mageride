namespace MageRide.AdminBff.Domain;

/// <summary>
/// The three subjects AL-39's verification family is agnostic over.
/// </summary>
/// <remarks>
/// One route set rather than three, because the officer's actions — confirm a field, approve,
/// reject — are the same act whichever kind of applicant is on the screen, and D3' spells the write
/// endpoints once (<c>PUT /admin/verification/{id}/fields/{key}</c>, <c>/approve</c>,
/// <c>/reject</c>) for all of them.
/// </remarks>
public static class VerificationSubjectTypes
{
    /// <summary>A driver's identity submission — Profile Setup's licence (AL-29, US-2.4a).</summary>
    public const string Driver = "driver";

    /// <summary>A vehicle registration, Mode C (AL-30) or a fleet's Mode A/B (AL-50).</summary>
    public const string Vehicle = "vehicle";

    /// <summary>A fleet organisation's KYC and its payout profile (US-13.A7, AL-49).</summary>
    public const string Org = "org";
}

/// <summary><c>registry.document_fields.verify_status</c> (migration 0305).</summary>
public static class VerificationFieldStatuses
{
    public const string AutoVerified = "auto_verified";
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
}

/// <summary><c>registry.document_fields.source</c> (migration 0305).</summary>
public static class VerificationFieldSources
{
    public const string Ai = "ai";
    public const string Manual = "manual";
}

/// <summary>
/// The wire spelling of a step's verdict — <c>admin-bff.yaml</c>'s <c>stepStatus</c> enum.
/// </summary>
/// <remarks>
/// Upper case here and lower case in <c>registry.onboarding_steps.status</c>, which is registry-svc's
/// column and not this component's to respell. The mapping is in one place
/// (<c>VerificationRepository</c>) so the two vocabularies meet exactly once.
/// </remarks>
public static class VerificationStepVerdicts
{
    public const string Verified = "VERIFIED";
    public const string PendingReview = "PENDING_REVIEW";
    public const string PendingInput = "PENDING_INPUT";
}

/// <summary>The subject statuses the three queue rows carry.</summary>
public static class VerificationStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Deactivated = "DEACTIVATED";
}

/// <summary>
/// The single step a driver's identity submission has.
/// </summary>
/// <remarks>
/// <c>registry.onboarding_steps</c> is keyed by <c>vehicle_id</c> and its CHECK admits four names,
/// none of which is a driver's — AL-30's machine is about a vehicle. SCR-AP-003a's decision rail
/// still needs a per-step breakdown for a driver, so the licence is presented as one synthetic step
/// rather than as an empty rail the officer would read as "nothing to check".
/// </remarks>
public static class VerificationSteps
{
    public const string Profile = "profile";

    /// <summary>
    /// Which step of AL-30's wizard a document kind belongs to, or the kind itself where there is
    /// no wizard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four Mode C names are registry-svc's <c>OnboardingSteps.DocumentKind</c> read backwards
    /// — <c>registration</c> is step 4 because a Mode C driver never uploads a CR book and the
    /// plated photos are what evidence the registration (that service's own comment).
    /// </para>
    /// <para>
    /// A fleet vehicle has no <c>registry.onboarding_steps</c> rows at all, so its rail is AL-50's
    /// four named slots under their own names — which is what SCR-FP-004 shows the operator who
    /// uploaded them, and therefore what the officer should be reading back to them.
    /// </para>
    /// </remarks>
    public static string ForKind(string kind, bool hasWizardSteps) => (kind, hasWizardSteps) switch
    {
        ("driving_license", _) => Profile,
        ("insurance", true) => "insurance",
        ("revenue_license", true) => "revenue",
        ("registration", true) => "photos",
        _ => kind,
    };
}

/// <summary>Which rendition of a document a link points at (SCR-AP-003a's grid vs 003b's lightbox).</summary>
public static class DocumentVariants
{
    public const string Thumb = "thumb";
    public const string Full = "full";

    public static bool IsKnown(string? variant) =>
        variant is Thumb or Full;
}

/// <summary>
/// Who the officer is looking at, resolved from an id that could name any of the three.
/// </summary>
/// <param name="Id">The subject's own id.</param>
/// <param name="Type">One of <see cref="VerificationSubjectTypes"/>.</param>
/// <param name="DisplayName">A plate, a person's name or an organisation's — whatever the row has.</param>
/// <param name="Status">PENDING / APPROVED / REJECTED, derived for a driver and stored for the other two.</param>
/// <param name="Mode">
/// <c>A</c>, <c>B</c> or <c>C</c> for a vehicle; null otherwise. It is what decides which service
/// owns the approval — Mode C is registry-svc's wizard, Mode A/B is fleet-svc's AL-50 gate.
/// </param>
/// <param name="FleetId">The owning organisation, for a fleet vehicle or for an org subject.</param>
public sealed record VerificationSubject(
    Guid Id, string Type, string? DisplayName, string Status, string? Mode, Guid? FleetId);

/// <summary>One <c>registry.document_fields</c> row as the officer's rail renders it.</summary>
public sealed record VerificationField(
    Guid DocumentId,
    string FieldKey,
    string? FieldValue,
    string Source,
    decimal? Confidence,
    string VerifyStatus)
{
    public bool IsPending => VerifyStatus == VerificationFieldStatuses.Pending;
}

/// <summary>One attached document, before its links are minted.</summary>
/// <param name="StorageUrl">
/// <c>registry.documents.file_url</c> or <c>docs.uploads.storage_url</c> — a D-36 object-storage
/// pointer, never bytes and never something a browser can follow on its own.
/// </param>
/// <param name="CapturedVia">
/// AL-43 provenance from <c>docs.uploads.captured_via</c>: a gallery pick where the in-app
/// drag-crop scanner was expected is the fraud signal SCR-AP-003a sorts on. Null where the upload
/// row cannot be resolved or predates the column.
/// </param>
public sealed record VerificationDocumentRow(
    Guid DocId, string Kind, string StorageUrl, string? CapturedVia, DateTimeOffset CreatedAt);

/// <summary>One row of the decision rail's per-step breakdown (SCR-AP-003a).</summary>
public sealed record VerificationStepRow(string Step, string Status);

/// <summary>A row of the driving-licence queue (SCR-AP-003 tab 1).</summary>
public sealed record DriverQueueRow(
    Guid DriverId, string Name, DateTimeOffset SubmittedAt, IReadOnlyList<string> FlaggedFields, string Status);

/// <summary>A row of the vehicle-registration queue (SCR-AP-003 tab 2).</summary>
public sealed record VehicleQueueRow(
    Guid VehicleId,
    string RegNo,
    Guid? OwnerDriverId,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<string> FlaggedFields,
    string Status);

/// <summary>
/// A document resolved for the viewer, whichever table holds it.
/// </summary>
/// <param name="Source">
/// <c>registry.documents</c> for an onboarding document, <c>docs.uploads</c> for an AL-49 payout
/// document. Recorded on the DOC_VIEW row so an auditor can find the row that was opened.
/// </param>
/// <param name="OwnerId">
/// Whose document it is: <c>registry.documents.driver_id</c>, or the uploader on a payout document
/// (<c>docs.uploads.owner_id</c>). Recorded on the DOC_VIEW row — "which licence was opened" is a
/// question about a person, and the doc id alone answers it only for as long as the row survives
/// NFR-28's 90-day sweep.
/// </param>
public sealed record StoredDocument(
    Guid DocId, string Kind, string StorageUrl, string Source, Guid? OwnerId, Guid? FleetId, Guid? VehicleId);

/// <summary>The tables a document id can name. Recorded verbatim on the DOC_VIEW row.</summary>
public static class DocumentSources
{
    /// <summary>An onboarding document — a licence, a registration, an insurance certificate.</summary>
    public const string RegistryDocuments = "registry.documents";

    /// <summary>An AL-49 payout document, which has no <c>registry.documents</c> row.</summary>
    public const string DocsUploads = "docs.uploads";
}

/// <summary>
/// A driver waiting on a bank &amp; payout decision (AL-58, AL-59) — SCR-AP-003's fourth tab.
/// </summary>
/// <remarks>
/// <para>
/// <b>This queue is not built from <c>registry.document_fields</c>, and it cannot be.</b> The other
/// two are "has a flagged extracted field", which is AL-27's fence expressed as a query — but
/// nothing extracts fields from a bank statement, exactly as nothing does for a fleet's (the org
/// rail says so in as many words). Membership here is the profile's own
/// <c>status = 'pending_verification'</c>, which the partial index <c>ix_driver_payout_pending</c>
/// (migration 0316) exists to serve and whose comment names this queue.
/// </para>
/// <para>
/// <b>Why it is a tab of its own rather than rows on the licence queue.</b> A payout profile is
/// submitted and edited independently of identity, repeatedly, years apart (BR-31.1) — a driver
/// approved in March who changes banks in September has nothing pending on their licence and would
/// appear in no queue at all. That is the gap this closes.
/// </para>
/// </remarks>
public sealed record DriverPayoutQueueRow(
    Guid DriverId,
    string Name,
    string Bank,
    string AccountNo,
    DateTimeOffset SubmittedAt,
    bool HasProof,
    bool HasLankaQr,
    /// <summary>The driver's <em>identity</em> verdict, not this profile's — see below.</summary>
    string Status);

/// <summary>One version of where a driver's swept earnings go, as the officer reads it.</summary>
public sealed record DriverPayoutProfileRow(
    Guid ProfileId,
    Guid DriverId,
    string Name,
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    Guid? ProofUploadId,
    Guid? LankaqrUploadId,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt);

/// <summary>
/// <c>registry.driver_payout_profiles.status</c> (migration 0316), as this service reads it.
/// </summary>
/// <remarks>
/// Declared here rather than referenced from registry-svc: a BFF that took a project reference on a
/// service would be able to reach its repositories, and the wire is the contract between them. The
/// value is the database's, and the CHECK constraint is what keeps the two honest.
/// </remarks>
public static class DriverPayoutStatuses
{
    public const string PendingVerification = "pending_verification";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

/// <summary>The two evidence slots on an AL-58 payout profile, as the officer's lightbox labels them.</summary>
public static class DriverPayoutDocumentKinds
{
    /// <summary>A bank statement or a passbook first page — one column, so the label is the slot.</summary>
    public const string ProofOfAccount = "proof_of_account";

    /// <summary>AL-59: the driver's own bank-app LankaQR, which a passenger scans to pay them.</summary>
    public const string LankaqrCode = "lankaqr_code";
}
