using System.Collections.Frozen;

namespace MageRide.Registry.Domain;

/// <summary>
/// A row of <c>registry.documents</c> — one uploaded driver or vehicle document with its expiry
/// (E-03, AL-10; migration 0305).
/// </summary>
/// <param name="VehicleId">
/// <see langword="null"/> for a driver-identity document. AL-27 captures the driving licence at
/// Profile Setup, which precedes any vehicle, so those rows are deliberately vehicle-less.
/// </param>
/// <param name="ExpiresAt">
/// What the E-03 sweep reads. <see langword="null"/> when the document carries no expiry, or when
/// extraction could not find one — in which case its step is <c>pending_review</c> anyway.
/// </param>
public sealed record VehicleDocument(
    Guid Id,
    Guid? DriverId,
    Guid? FleetId,
    Guid? VehicleId,
    string Kind,
    string FileUrl,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// A row of <c>registry.document_fields</c> — one extracted or typed value, with where it came
/// from and whether anybody has to look at it (AL-29; migration 0305).
/// </summary>
/// <param name="Confidence">
/// Gemini Flash 3.0's per-field confidence, or <see langword="null"/> for a hand-typed value —
/// <c>ck_document_fields_manual_confidence</c> refuses a confidence on a manual field, because a
/// number invented for a value nobody scanned would read as evidence.
/// </param>
public sealed record DocumentField(
    Guid Id,
    Guid DocumentId,
    string FieldKey,
    string? FieldValue,
    decimal? Confidence,
    string Source,
    string VerifyStatus,
    Guid? ConfirmedBy,
    DateTimeOffset? ConfirmedAt)
{
    /// <summary>Whether this field is waiting on a Verification Officer (SCR-AP-003).</summary>
    public bool IsPending => VerifyStatus == VerifyStatuses.Pending;
}

/// <summary>
/// A row of <c>registry.onboarding_steps</c> — one saved step of the AL-30 wizard (migration 0305).
/// </summary>
/// <param name="Fields">
/// The step's own input as stored JSON: what the driver typed on <c>details</c>, and the upload
/// ids on the document steps. Provenance of the <em>extracted</em> values is
/// <see cref="DocumentField"/>; this is the replay of the request that produced them.
/// </param>
public sealed record OnboardingStepRow(
    Guid VehicleId, string Step, string Status, string? Fields, DateTimeOffset? SavedAt);

/// <summary>The four steps of the Mode-C wizard, in the order the app walks them (AL-30).</summary>
/// <remarks>
/// <b>The order is the contract.</b> <c>nextStep</c> is "the first step that is not
/// <c>verified</c>", so a different order here would resume a driver on a different screen than
/// the one the wizard left them on (US-2.26).
/// </remarks>
public static class OnboardingSteps
{
    /// <summary>Step 1/4 — vehicle type and registration number. Driver-entered, so verified on entry.</summary>
    public const string Details = "details";

    /// <summary>Step 2/4 — the insurance certificate. Verified when its expiry extracts (AL-10).</summary>
    public const string Insurance = "insurance";

    /// <summary>Step 3/4 — the revenue licence. Verified when licence number <em>and</em> expiry extract (AL-10).</summary>
    public const string Revenue = "revenue";

    /// <summary>Step 4/4 — front and back photos. Verified when the plate OCR matches the registration.</summary>
    public const string Photos = "photos";

    /// <summary>All four, in wizard order.</summary>
    public static readonly string[] All = [Details, Insurance, Revenue, Photos];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? step) => step is not null && Known.Contains(step);

    /// <summary>Where a step sits in the wizard. Drives the resume order, so it is not alphabetic.</summary>
    public static int Ordinal(string step) => Array.IndexOf(All, step);

    /// <summary>The document kind a step uploads, or <see langword="null"/> for <c>details</c>.</summary>
    public static string? DocumentKind(string step) => step switch
    {
        Insurance => DocumentKinds.Insurance,
        Revenue => DocumentKinds.RevenueLicense,
        // AL-10 lists "vehicle reg" among the statuses shown on the registration hub, and the
        // Mode-C driver never uploads a CR book — the plated front and back photos are what
        // evidence this vehicle's registration, so they are the `registration` document here.
        // The Fleet Portal's CR-book slot (AL-50) is the same kind for the same reason.
        Photos => DocumentKinds.Registration,
        _ => null,
    };
}

/// <summary>The <c>registry.documents.kind</c> CHECK (0305), spelled once.</summary>
public static class DocumentKinds
{
    /// <summary>Driver identity, captured at Profile Setup. Vehicle-less by AL-27.</summary>
    public const string DrivingLicense = "driving_license";

    public const string Registration = "registration";
    public const string Permit = "permit";
    public const string Insurance = "insurance";
    public const string RevenueLicense = "revenue_license";
}

/// <summary>
/// The <c>registry.onboarding_steps.status</c> CHECK (0305) — what is <em>stored</em>.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="StepVerdicts"/>. The database spells these lower-case
/// (<c>pending_input</c>) and <c>registry.yaml</c> spells the same three upper-case
/// (<c>PENDING_INPUT</c>); one set of constants for both would make a rename in either place look
/// harmless. <c>OnboardingService.ToVerdict</c> is the single crossing point.
/// </remarks>
public static class StepStatuses
{
    public const string PendingInput = "pending_input";
    public const string Verified = "verified";
    public const string PendingReview = "pending_review";
}

/// <summary>Which face of a two-sided capture an upload is (AL-43's scanner posts one per side).</summary>
public static class DocumentSides
{
    public const string Front = "front";
    public const string Back = "back";
}

/// <summary>The <c>registry.documents.status</c> lifecycle (0305), driven by the E-03 sweep.</summary>
public static class DocumentStatuses
{
    public const string Valid = "VALID";

    /// <summary>Inside E-03's 30-day window. A driver has been told; dispatch is untouched.</summary>
    public const string Expiring = "EXPIRING";

    /// <summary>Past <c>expires_at</c>. Suspends dispatch on every vehicle the document covers.</summary>
    public const string Expired = "EXPIRED";

    public const string Rejected = "REJECTED";
}

/// <summary>The <c>registry.document_fields.source</c> CHECK — who produced the value (AL-29).</summary>
public static class FieldSources
{
    /// <summary>Extracted by ocr-svc. Carries a confidence.</summary>
    public const string Ai = "ai";

    /// <summary>
    /// Typed by the driver because the scan was unclear. <b>Always pending</b>: BR-25.2 lets them
    /// proceed and trusts the value only after an officer confirms it.
    /// </summary>
    public const string Manual = "manual";
}

/// <summary>The <c>registry.document_fields.verify_status</c> CHECK (AL-29).</summary>
public static class VerifyStatuses
{
    /// <summary>Extracted with confidence at or above threshold. Nobody has to look at it.</summary>
    public const string AutoVerified = "auto_verified";

    /// <summary>Manual, doubtful, or a plate that did not match. Sits in the officer queue (SCR-AP-003).</summary>
    public const string Pending = "pending";

    /// <summary>A Verification Officer confirmed or corrected it (C062). Counts as verified for AL-30.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Whether a field in this state blocks approval. Only <see cref="Pending"/> does.</summary>
    public static bool BlocksApproval(string verifyStatus) => verifyStatus == Pending;
}

/// <summary>
/// The <c>registry.document_fields.field_key</c> vocabulary (ADD §9.1, AL-29, I-25.1/I-25.2).
/// </summary>
/// <remarks>
/// The column is free text on purpose — ocr-svc may return more than these — but the keys the
/// verdict rules name are fixed, because a rename here silently turns a required field into an
/// optional one and the step verifies with nothing in it.
/// </remarks>
public static class DocumentFieldKeys
{
    // Driving licence (Profile Setup, AL-29).
    public const string LicenceNo = "licence_no";
    public const string LicenceExpiry = "licence_expiry";
    public const string NicNo = "nic_no";
    public const string AllowedVehicleTypes = "allowed_vehicle_types";

    // Insurance (Step 2/4). D5' §14.1a: verified when the expiry extracts with confidence.
    public const string InsuranceExpiry = "insurance_expiry";
    public const string InsurancePolicyNo = "insurance_policy_no";
    public const string Insurer = "insurer";

    // Revenue licence (Step 3/4). D5' §14.1a: number AND expiry.
    public const string RevenueNo = "revenue_no";
    public const string RevenueExpiry = "revenue_expiry";

    // Vehicle photos (Step 4/4). I-25.2 names `reg_no_match`; `plate_text` is what it was read
    // as, kept so a mismatch can be shown to the officer and re-judged if the plate is corrected.
    public const string RegNoMatch = "reg_no_match";
    public const string PlateText = "plate_text";

    /// <summary>
    /// The fields a document must yield before it counts as read (D5' §14.1a's verdict table).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A required key that extraction did not return is written anyway, with a null value and
    /// <c>verify_status='pending'</c>, so "the insurance expiry could not be read" is a row the
    /// Verification Officer can see and fill rather than an absence they have to infer.
    /// </para>
    /// <para>
    /// <c>details</c> has no entry because it has no document. D5' §14.1a marks it "(entered)" —
    /// the type and registration number the driver typed <em>are</em> the verification, and
    /// treating the driver's own typing as a manual field would make AL-30's auto-approve
    /// unreachable for every vehicle ever onboarded.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RequiredFor(string documentKind, string? side) =>
        (documentKind, side) switch
        {
            // The Sri Lankan licence carries its classes on the reverse (AL-29), so which fields
            // are expected depends on which side was scanned.
            //
            // Δ MCS-20 — LicenceExpiry moved FRONT -> BACK, and this table MUST match ocr-svc's
            // `DocumentVocabulary`. Changing one and not the other does not error: the key simply
            // stops being accepted on the side it now arrives on, and the field goes permanently
            // null-and-pending in the officer queue. The front's `4a` is the date of ISSUE; the
            // expiry is column 11 of the class table on the reverse.
            (DocumentKinds.DrivingLicense, DocumentSides.Back) => [AllowedVehicleTypes, LicenceExpiry],
            (DocumentKinds.DrivingLicense, _) => [LicenceNo],
            (DocumentKinds.Insurance, _) => [InsuranceExpiry],
            (DocumentKinds.RevenueLicense, _) => [RevenueNo, RevenueExpiry],
            (DocumentKinds.Registration, _) => [RegNoMatch],
            _ => [],
        };

    /// <summary>
    /// Every field a document of this kind and side can carry — the required ones plus the extras
    /// ocr-svc returns for it.
    /// </summary>
    /// <remarks>
    /// This is what decides where a driver's correction goes. <c>nic_no</c> is read off the front
    /// of the licence and is not <em>required</em> for it (a licence with an unreadable NIC is
    /// still a licence), so filtering corrections by <see cref="RequiredFor"/> would silently drop
    /// exactly the field US-2.4a exists for.
    /// </remarks>
    public static IReadOnlyList<string> AcceptedFor(string documentKind, string? side) =>
        (documentKind, side) switch
        {
            // Δ MCS-20 — expiry is a BACK field; see RequiredFor above.
            (DocumentKinds.DrivingLicense, DocumentSides.Back) => [AllowedVehicleTypes, LicenceExpiry],
            (DocumentKinds.DrivingLicense, _) => [LicenceNo, NicNo],
            (DocumentKinds.Insurance, _) => [InsuranceExpiry, InsurancePolicyNo, Insurer],
            (DocumentKinds.RevenueLicense, _) => [RevenueNo, RevenueExpiry],
            (DocumentKinds.Registration, _) => [RegNoMatch, PlateText],
            _ => [],
        };

    /// <summary>The expiry field of a document kind, or <see langword="null"/> when it has none.</summary>
    /// <remarks>
    /// This is the seam between AL-29's extraction and E-03's sweep: whatever ocr-svc reads here
    /// becomes <c>registry.documents.expires_at</c>, and a document whose expiry never extracted
    /// has no expiry to sweep — which is why the same missing field also holds its step in
    /// <c>pending_review</c>.
    /// </remarks>
    public static string? ExpiryFieldFor(string documentKind) => documentKind switch
    {
        DocumentKinds.DrivingLicense => LicenceExpiry,
        DocumentKinds.Insurance => InsuranceExpiry,
        DocumentKinds.RevenueLicense => RevenueExpiry,
        _ => null,
    };
}

/// <summary>The event types the onboarding machine and the E-03 sweep write to the outbox.</summary>
/// <remarks>
/// <b>None of the four is in D6' §2.1/§2.2.</b> E-03 names <c>document.expiring</c> and
/// <c>document.expired</c> outright and gives them no envelope; US-2.14's REGISTRATION_RESULT push
/// and US-2.10's officer queue both need a trigger and neither has an event. Raised as a
/// micro-change-set in the C029 handoff; all four go on <c>registry.events</c>, keyed by vehicle
/// like everything else registry-svc publishes.
/// </remarks>
public static class OnboardingEventTypes
{
    /// <summary>D3' <c>POST /v1/vehicles</c> "Side Effects: emits `vehicle.registered`".</summary>
    public const string VehicleRegistered = "vehicle.registered";

    /// <summary>AL-27's auto-approval. Drives the US-2.14 FCM/APNs REGISTRATION_RESULT push.</summary>
    public const string VehicleApproved = "vehicle.approved";

    /// <summary>AL-29/AL-30: a step is holding a pending field and needs an officer (US-2.10, SCR-AP-003).</summary>
    public const string DocumentReviewRequired = "document.review_required";

    /// <summary>E-03, at T−30 d / T−7 d / T−1 d.</summary>
    public const string DocumentExpiring = "document.expiring";

    /// <summary>E-03: past expiry. The vehicle is DISPATCH_SUSPENDED with it.</summary>
    public const string DocumentExpired = "document.expired";

    /// <summary>The counterpart, so a suspended vehicle coming back is visible to the same consumers.</summary>
    public const string DispatchResumed = "vehicle.dispatch_resumed";
}
