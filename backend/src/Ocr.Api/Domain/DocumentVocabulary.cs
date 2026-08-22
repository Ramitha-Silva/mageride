using System.Collections.Frozen;

namespace MageRide.Ocr.Domain;

/// <summary>
/// The <c>registry.documents.kind</c> / <c>docs.uploads.kind</c> values this service extracts from.
/// </summary>
/// <remarks>
/// Deliberately a copy of registry-svc's <c>DocumentKinds</c> rather than a shared type. The two
/// services agree on these strings over HTTP, and a shared constant would make the wire contract
/// look like a compile-time one — the sort of coupling that turns a rename in one bounded context
/// into a silent behaviour change in another. The set is asserted against registry-svc's in
/// <c>DocumentVocabularyTests</c>.
/// </remarks>
public static class DocumentKinds
{
    /// <summary>Driver identity, captured at Profile Setup (AL-27). Vehicle-less.</summary>
    public const string DrivingLicense = "driving_license";

    /// <summary>Mode-C step 4/4's plated front and back photos, and AL-50's CR-book slot.</summary>
    public const string Registration = "registration";

    /// <summary>AL-50: Mode A passenger transport legally requires a route permit.</summary>
    public const string Permit = "permit";

    public const string Insurance = "insurance";
    public const string RevenueLicense = "revenue_license";

    /// <summary>Every kind with an extractor behind it (AL-50: fleet kinds reuse the same pipeline).</summary>
    public static readonly string[] All =
        [DrivingLicense, Registration, Permit, Insurance, RevenueLicense];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? kind) => kind is not null && Known.Contains(kind);
}

/// <summary>Which face of a two-sided capture an upload is (AL-43's scanner posts one per side).</summary>
public static class DocumentSides
{
    public const string Front = "front";
    public const string Back = "back";

    /// <summary>Normalises what a caller sent. Anything unrecognised reads as "unspecified".</summary>
    public static string? Normalise(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        Front => Front,
        Back => Back,
        _ => null,
    };
}

/// <summary>
/// The <c>registry.document_fields.field_key</c> vocabulary (ADD §9.1, AL-29, I-25.1/I-25.2).
/// </summary>
/// <remarks>
/// The first eleven are registry-svc's, spelled identically because registry-svc's verdict rules
/// look them up by name; the three permit keys are this service's, for AL-50's Fleet-Portal slot
/// that no spec gives field names to (raised in the C054 handoff).
/// </remarks>
public static class DocumentFieldKeys
{
    // Driving licence (Profile Setup, AL-29).
    public const string LicenceNo = "licence_no";
    public const string LicenceExpiry = "licence_expiry";
    public const string NicNo = "nic_no";
    public const string AllowedVehicleTypes = "allowed_vehicle_types";

    // Insurance (Mode-C step 2/4). D5' §14.1a: VERIFIED when the expiry extracts with confidence.
    public const string InsuranceExpiry = "insurance_expiry";
    public const string InsurancePolicyNo = "insurance_policy_no";
    public const string Insurer = "insurer";

    // Revenue licence (step 3/4). D5' §14.1a: number AND expiry.
    public const string RevenueNo = "revenue_no";
    public const string RevenueExpiry = "revenue_expiry";

    // Vehicle photos (step 4/4). I-25.2 names `reg_no_match`; `plate_text` is what the plate was
    // read as, kept so a mismatch can be shown to an officer and re-judged if the plate is edited.
    public const string RegNoMatch = "reg_no_match";
    public const string PlateText = "plate_text";

    // Route permit (AL-50, D6' I-27.3 "permit no + route/validity"). No spec names the keys.
    public const string PermitNo = "permit_no";
    public const string PermitRoute = "permit_route";
    public const string PermitExpiry = "permit_expiry";

    /// <summary>
    /// The fields a document of this kind and side must yield before it counts as read
    /// (D5' §14.1a's verdict table).
    /// </summary>
    /// <remarks>
    /// A required key extraction did not return is <b>still returned</b>, with a null value and
    /// <c>pending</c>, so registry-svc writes a row an officer can fill rather than an absence they
    /// have to notice. That is C029's rule (3), and this is the side of the seam that produces it.
    /// </remarks>
    public static IReadOnlyList<string> RequiredFor(string kind, string? side) => (kind, side) switch
    {
        // A Sri Lankan licence carries its classes on the reverse (AL-29), so which fields are
        // expected depends on which side was scanned.
        //
        // Δ MCS-20 — LicenceExpiry moved FRONT -> BACK. The front's `4a` is the date of ISSUE; the
        // date of expiry is column 11 of the class table on the reverse. Asking the front for "the
        // date of expiry" got the issue date returned confidently, and that value becomes
        // `registry.documents.expires_at` and the input to the E-03 suspension sweep — so it was
        // not a label being wrong, it was the document lifecycle being wrong.
        (DocumentKinds.DrivingLicense, DocumentSides.Back) => [AllowedVehicleTypes, LicenceExpiry],
        (DocumentKinds.DrivingLicense, _) => [LicenceNo],
        (DocumentKinds.Insurance, _) => [InsuranceExpiry],
        (DocumentKinds.RevenueLicense, _) => [RevenueNo, RevenueExpiry],
        (DocumentKinds.Registration, _) => [RegNoMatch],
        (DocumentKinds.Permit, _) => [PermitNo, PermitExpiry],
        _ => [],
    };

    /// <summary>Every field this kind and side can carry — the required ones plus the extras.</summary>
    public static IReadOnlyList<string> AcceptedFor(string kind, string? side) => (kind, side) switch
    {
        // Δ MCS-20 — expiry is a BACK field; see RequiredFor above.
        (DocumentKinds.DrivingLicense, DocumentSides.Back) => [AllowedVehicleTypes, LicenceExpiry],
        (DocumentKinds.DrivingLicense, _) => [LicenceNo, NicNo],
        (DocumentKinds.Insurance, _) => [InsuranceExpiry, InsurancePolicyNo, Insurer],
        (DocumentKinds.RevenueLicense, _) => [RevenueNo, RevenueExpiry],
        (DocumentKinds.Registration, _) => [RegNoMatch, PlateText],
        (DocumentKinds.Permit, _) => [PermitNo, PermitRoute, PermitExpiry],
        _ => [],
    };

    /// <summary>Field keys whose value is a date, so the parser knows to normalise it to ISO.</summary>
    public static bool IsDate(string key) =>
        key is LicenceExpiry or InsuranceExpiry or RevenueExpiry or PermitExpiry;
}

/// <summary>The <c>registry.document_fields.source</c> CHECK — who produced the value (AL-29).</summary>
/// <summary>
/// What the model says the document actually IS, as opposed to what the caller claimed
/// (Δ MCS-21).
/// </summary>
/// <remarks>
/// <para>
/// Until now nothing in this service validated document TYPE at all: the kind arrived as an
/// assertion from registry-svc and was injected into the prompt as fact — "This is a Sri Lankan
/// motor insurance certificate" — so the model was never in a position to disagree. A driver
/// photographing their revenue licence at the insurance step got a confident set of nulls and
/// nothing anywhere could say why.
/// </para>
/// <para>
/// A CLOSED set, because an open one cannot be reasoned about: anything the model says that is
/// not one of these reduces to <see cref="Unclear"/>, which is never acted on.
/// </para>
/// </remarks>
public static class DocumentTypes
{
    public const string DrivingLicence = "driving_licence";
    public const string Insurance = "insurance";
    public const string RevenueLicence = "revenue_licence";
    public const string VehiclePhoto = "vehicle_photo";

    /// <summary>A document, but none of the four. Honest, and on its own never a contradiction.</summary>
    public const string Other = "other";

    /// <summary>Too blurred, too dark or too partial to say. The default for every unknown.</summary>
    public const string Unclear = "unclear";

    /// <summary>The values the prompt offers and the response schema constrains.</summary>
    public static readonly IReadOnlyList<string> All =
        [DrivingLicence, Insurance, RevenueLicence, VehiclePhoto, Other, Unclear];

    /// <summary>
    /// The reported type, or <see cref="Unclear"/> for anything absent or unrecognised.
    /// </summary>
    /// <remarks>
    /// Every "no information" case collapses here — a missing member, an empty string, a
    /// synonym the model preferred, a value from a future version of this list. Reducing them
    /// to <c>unclear</c> rather than to "mismatch" is the whole safety property: this feature
    /// may only ever act on a POSITIVE identification.
    /// </remarks>
    public static string Reduce(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
        {
            return Unclear;
        }

        var trimmed = reported.Trim().ToLowerInvariant().Replace(' ', '_');

        return All.Contains(trimmed, StringComparer.Ordinal) ? trimmed : Unclear;
    }

    /// <summary>The <see cref="DocumentKinds"/> value a reported type corresponds to.</summary>
    public static string? KindOf(string reported) => reported switch
    {
        DrivingLicence => DocumentKinds.DrivingLicense,
        Insurance => DocumentKinds.Insurance,
        RevenueLicence => DocumentKinds.RevenueLicense,
        VehiclePhoto => DocumentKinds.Registration,
        _ => null,
    };

    /// <summary>
    /// Whether the model POSITIVELY identified this as a different document from the one the
    /// caller claimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False for <see cref="Unclear"/>, for an absent answer and for anything unrecognised. The
    /// asymmetry is deliberate and is already priced into the platform: a MISSED mismatch costs
    /// a Verification Officer one glance at a document they were opening anyway, because queue
    /// membership is "has a pending field" and a wrong document almost always has one. A FALSE
    /// mismatch costs a legitimate driver AL-27's auto-approval, adds a review that would not
    /// otherwise happen, and does it with no reason attached to anything the driver or the
    /// support agent can see.
    /// </para>
    /// <para>
    /// <b><see cref="Other"/> never contradicts a vehicle photo.</b> Step 4/4 of the Mode-C
    /// wizard posts a photograph of a car, and a photograph of a car is a *document* only by
    /// courtesy — <c>other</c> is the honest answer for one. That cell is the highest
    /// false-positive risk in the whole matrix, so it is excluded by construction rather than
    /// by tuning.
    /// </para>
    /// </remarks>
    public static bool Contradicts(string? reported, string claimedKind)
    {
        var reduced = Reduce(reported);

        if (reduced == Unclear)
        {
            return false;
        }

        // `other` is "a document, but not one of the four". On a vehicle photo that is correct
        // rather than contradictory; elsewhere it is not enough to act on either, because it
        // names nothing.
        if (reduced == Other)
        {
            return false;
        }

        var identified = KindOf(reduced);

        return identified is not null && !string.Equals(identified, claimedKind, StringComparison.Ordinal);
    }
}

public static class FieldSources
{
    /// <summary>Extracted here. Everything this service emits is <c>ai</c>.</summary>
    public const string Ai = "ai";

    /// <summary>Typed by the driver because the scan was unclear. Never produced by ocr-svc.</summary>
    public const string Manual = "manual";
}

/// <summary>The <c>registry.document_fields.verify_status</c> CHECK (AL-29).</summary>
public static class VerifyStatuses
{
    /// <summary>Extracted at or above threshold. Nobody has to look at it.</summary>
    public const string AutoVerified = "auto_verified";

    /// <summary>Doubtful, unread, or a plate that did not match. The officer queue (SCR-AP-003).</summary>
    public const string Pending = "pending";

    /// <summary>A Verification Officer confirmed it (C062). Never produced here.</summary>
    public const string Confirmed = "confirmed";
}

/// <summary>The <c>docs.extractions.status</c> CHECK (migration 1301).</summary>
public static class ExtractionStatuses
{
    /// <summary>Queued, not yet run. The row is written when the job completes, so this is rare.</summary>
    public const string Pending = "PENDING";

    /// <summary>Every required field came back at or above threshold.</summary>
    public const string Extracted = "EXTRACTED";

    /// <summary>Read, but something on it needs a Verification Officer (US-2.10).</summary>
    public const string ManualReview = "MANUAL_REVIEW";

    /// <summary>Nothing was read at all — both engines unavailable, or the bytes are not an image.</summary>
    public const string Failed = "FAILED";
}

/// <summary>Which engine produced a result. Reported on the wire and stored on the row.</summary>
public static class ExtractionEngines
{
    /// <summary>Gemini Flash 3.0, on the redacted image (D6' §7.5 primary).</summary>
    public const string Gemini = "gemini";

    /// <summary>On-prem Tesseract (D6' §7.5 fallback, §8.3 "OCR down → Tesseract").</summary>
    public const string Tesseract = "tesseract";

    /// <summary>Neither could run. No fields.</summary>
    public const string None = "none";
}
