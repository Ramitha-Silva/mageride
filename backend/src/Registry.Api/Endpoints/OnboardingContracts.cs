using System.Text.Json.Serialization;
using MageRide.Registry.Domain;
using MageRide.Registry.Onboarding;

namespace MageRide.Registry.Endpoints;

/// <summary>Body of <c>PUT /v1/drivers/profile</c> (Profile Setup, SCR-DA/DI-003a).</summary>
/// <param name="NicNo">
/// Sent only when the scan was unclear and the driver typed it. Stored <c>manual</c>/<c>pending</c>
/// and routed to the Verification Officer (US-2.4a, AL-29).
/// </param>
public sealed record UpsertDriverProfileBody(
    string? DriverName,
    string? ProfilePhotoFileId,
    string? LicenseFrontFileId,
    string? LicenseBackFileId,
    string? NicNo = null,
    IReadOnlyList<string>? AllowedVehicleTypes = null,
    string? LicenceNo = null,
    string? LicenceExpiry = null);

/// <summary>
/// One row of <c>_shared.yaml#/components/schemas/ExtractedField</c> — a value with its provenance.
/// </summary>
/// <param name="Value">
/// <see langword="null"/> when nothing could be read. Emitted <b>as</b> null rather than omitted —
/// the shared serialiser drops nulls, and <c>value</c> is in the schema's <c>required</c> list, so
/// a field that failed to extract has to appear as a row the officer can fill.
/// </param>
public sealed record ExtractedFieldResponse(
    string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
    string Source,
    decimal? Confidence,
    string VerifyStatus)
{
    public static ExtractedFieldResponse From(DocumentField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return new ExtractedFieldResponse(
            field.FieldKey, field.FieldValue, field.Source, field.Confidence, field.VerifyStatus);
    }
}

/// <summary>
/// 200 body of <c>GET /v1/drivers/profile</c> — the profile row, without the extracted fields
/// (Δ MCS-05).
/// </summary>
/// <remarks>
/// Not <see cref="DriverProfileResponse"/> with an empty <c>fields</c>: that would say the licence
/// had no extracted fields, which is a different claim from "this read did not go and get them".
/// </remarks>
/// <summary>One of a driver's documents, with a link to its image (Δ MCS-28).</summary>
/// <param name="VehicleId">
/// <see langword="null"/> for the driving licence, which belongs to the person rather than to any
/// vehicle (AL-27). That is also how a client groups this list: the null ones go on SCR-DA/DI-029
/// and the rest onto the card of the vehicle they name.
/// </param>
/// <param name="ImageUrl">
/// A relative, signed, expiring link to the bytes. Resolve it against the gateway origin and follow
/// it as given; it changes between reads and needs no bearer token.
/// </param>
public sealed record DriverDocumentResponse(
    string DocId,
    string? VehicleId,
    string Kind,
    string Status,
    DateTimeOffset? ExpiresAt,
    string ImageUrl)
{
    public static DriverDocumentResponse From(Guid driverId, VehicleDocument document, IDriverPhotoLinks links)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(links);

        return new DriverDocumentResponse(
            document.Id.ToString(),
            document.VehicleId?.ToString(),
            document.Kind,
            document.Status,
            document.ExpiresAt,
            links.CreateDocument(driverId, document.Id, document.FileUrl));
    }
}

/// <summary>200 body of <c>GET /v1/drivers/documents</c> (Δ MCS-28).</summary>
public sealed record DriverDocumentListResponse(IReadOnlyList<DriverDocumentResponse> Items)
{
    public static DriverDocumentListResponse From(
        Guid driverId, IReadOnlyList<VehicleDocument> documents, IDriverPhotoLinks links)
    {
        ArgumentNullException.ThrowIfNull(documents);

        return new DriverDocumentListResponse(
            [.. documents.Select(document => DriverDocumentResponse.From(driverId, document, links))]);
    }
}

/// <summary>Turns the stored pointer into something a client can actually fetch (Δ MCS-25).</summary>
/// <remarks>
/// <para>
/// One helper for both profile reads, because they answer the same question about the same column
/// and had no business answering it differently.
/// </para>
/// <para>
/// <b>A driver with no photo keeps <see langword="null"/>.</b> Signing a link to a row that has
/// nothing behind it would give an app a URL that always 404s, which reads as a broken image rather
/// than as the absence the field already expresses. This is only reachable for a profile that
/// exists, and AL-27 makes the photo required to create one — but the column is nullable, PDPA
/// erasure sets it to <c>NULL</c> (<c>PdpaRepository</c>), and a link minted for an erased driver
/// would be the one case where this leaked something.
/// </para>
/// </remarks>
internal static class DriverProfilePhotoLink
{
    public static string? For(DriverProfileResult result, IDriverPhotoLinks links) =>
        string.IsNullOrWhiteSpace(result.Profile.PhotoUrl)
            ? null
            : links.Create(result.Profile.DriverId, result.Profile.PhotoUrl);
}

public sealed record DriverProfileSummaryResponse(
    string DriverId,
    string Status,
    string DisplayName,
    string? PhotoUrl,
    string? NicNo,
    IReadOnlyList<string> AllowedVehicleTypes)
{
    /// <param name="links">
    /// Mints the signed, expiring URL that replaces the stored pointer (Δ MCS-25). The column holds
    /// an <c>s3://</c> or <c>file://</c> storage URL, which is this service's to resolve and not a
    /// thing a client can fetch; sending it put a scheme no image loader understands into a field
    /// the contract types as <c>format: uri</c>.
    /// </param>
    public static DriverProfileSummaryResponse From(DriverProfileResult result, IDriverPhotoLinks links)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(links);

        return new DriverProfileSummaryResponse(
            result.Profile.DriverId.ToString(),
            result.Status,
            result.Profile.DisplayName,
            DriverProfilePhotoLink.For(result, links),
            result.Profile.NicNo,
            result.Profile.AllowedVehicleTypes ?? []);
    }
}

/// <summary>200 body of <c>PUT /v1/drivers/profile</c>.</summary>
/// <param name="Status">
/// <c>PENDING</c> while any identity field is waiting on an officer, <c>APPROVED</c> once none is.
/// </param>
public sealed record DriverProfileResponse(
    string DriverId,
    string Status,
    string DisplayName,
    string? PhotoUrl,
    string? NicNo,
    IReadOnlyList<string> AllowedVehicleTypes,
    IReadOnlyList<ExtractedFieldResponse> Fields)
{
    /// <param name="links">
    /// Mints the signed, expiring URL that replaces the stored pointer (Δ MCS-25). The column holds
    /// an <c>s3://</c> or <c>file://</c> storage URL, which is this service's to resolve and not a
    /// thing a client can fetch; sending it put a scheme no image loader understands into a field
    /// the contract types as <c>format: uri</c>.
    /// </param>
    public static DriverProfileResponse From(DriverProfileResult result, IDriverPhotoLinks links)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(links);

        return new DriverProfileResponse(
            result.Profile.DriverId.ToString(),
            result.Status,
            result.Profile.DisplayName,
            DriverProfilePhotoLink.For(result, links),
            result.Profile.NicNo,
            result.Profile.AllowedVehicleTypes ?? [],
            [.. result.Fields.Select(ExtractedFieldResponse.From)]);
    }
}

/// <summary>
/// Body of <c>PUT /v1/vehicles/{vehicleId}/onboarding/{step}</c>
/// (<c>registry.yaml#/components/schemas/OnboardingStepInput</c>).
/// </summary>
/// <remarks>
/// <para>
/// The shape both arms produce. The JSON arm carries upload ids; the <c>multipart/form-data</c>
/// arm carries the bytes and the endpoint converts them into ids through
/// <see cref="IOnboardingDocumentStore"/> before anything downstream sees a difference.
/// </para>
/// <para>
/// <b>Δ MCS-01 — the multipart arm used to be unmapped, and the reason recorded here was wrong.</b>
/// It said streaming the bytes "would put an unredacted image on this service's disk". They do not
/// touch this service's disk: they go straight to D-36's SSE-KMS bucket through the kernel's
/// <c>IObjectStore</c>, which is where this same file's AL-58 payout upload has been putting them
/// since. The redaction pre-pass guards what reaches the **external model**, and that is still
/// ocr-svc's, which still fetches by <c>storage_url</c>. Leaving the arm unmapped did not protect
/// the perimeter; it left <c>docs.uploads</c> with no writer, which made both onboarding screens
/// unreachable on a real gateway.
/// </para>
/// </remarks>
public sealed record OnboardingStepBody(
    string? RegistrationNumber = null,
    string? VehicleType = null,
    string? FileId = null,
    string? FileIdBack = null,
    IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>200 body of the step save.</summary>
/// <param name="NextStep">
/// Emitted as an explicit null once every step is verified. The shared serialiser drops nulls and
/// the contract types this as <c>OnboardingStep | null</c>, so "there is nothing left to do" has
/// to be a value rather than an absence a client could read as a parse failure.
/// </param>
public sealed record SaveOnboardingStepResponse(
    string StepStatus,
    string OnboardingStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextStep,
    string Status,
    string? OcrJobId)
{
    public static SaveOnboardingStepResponse From(OnboardingState state, string step)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new SaveOnboardingStepResponse(
            state.StepStatus(step),
            state.Vehicle.OnboardingStatus,
            state.NextStep,
            // Additive: AL-27 auto-approves on the fourth verified step, and without the vehicle
            // status here the app would have to poll to discover that saving a photo approved the
            // vehicle. Recorded in the C029 handoff.
            state.Vehicle.Status,
            state.OcrJobId?.ToString());
    }
}

/// <summary>200 body of <c>GET /v1/vehicles/{vehicleId}/onboarding-status</c> (SCR-DA/DI-006).</summary>
/// <inheritdoc cref="SaveOnboardingStepResponse" path="/param[@name='NextStep']"/>
public sealed record OnboardingStatusResponse(
    string Status,
    string OnboardingStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextStep,
    OnboardingStepsResponse Steps,
    IReadOnlyList<ExtractedFieldResponse> Fields)
{
    public static OnboardingStatusResponse From(OnboardingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new OnboardingStatusResponse(
            state.Vehicle.Status,
            state.Vehicle.OnboardingStatus,
            state.NextStep,
            new OnboardingStepsResponse(
                state.StepStatus(OnboardingSteps.Details),
                state.StepStatus(OnboardingSteps.Insurance),
                state.StepStatus(OnboardingSteps.Revenue),
                state.StepStatus(OnboardingSteps.Photos)),
            [.. state.Fields.Select(ExtractedFieldResponse.From)]);
    }
}

/// <summary>All four verdicts. Always all four, even before anything is saved.</summary>
public sealed record OnboardingStepsResponse(string Details, string Insurance, string Revenue, string Photos);

/// <summary>`PUT /v1/drivers/payout-profile` (Δ AL-58).</summary>
public sealed record DriverPayoutProfileBody(
    string? Bank, string? Branch, string? AccountNo, string? AccountHolderName);

/// <summary>`DriverPayoutProfile` — one version of where a driver's earnings go.</summary>
public sealed record DriverPayoutProfileResponse(
    string Bank,
    string Branch,
    string AccountNo,
    string AccountHolderName,
    string? ProofDocId,
    string? LankaqrDocId,
    string Status,
    string? RejectionReason,
    DateTimeOffset? VerifiedAt)
{
    public static DriverPayoutProfileResponse From(MageRide.Registry.Domain.DriverPayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new DriverPayoutProfileResponse(
            profile.Bank,
            profile.Branch,
            profile.AccountNo,
            profile.AccountHolderName,
            profile.ProofUploadId?.ToString(),
            profile.LankaqrUploadId?.ToString(),
            profile.Status,
            profile.RejectionReason,
            profile.VerifiedAt);
    }
}

/// <summary>The 201 of a payout-document upload.</summary>
public sealed record DriverPayoutDocumentResponse(string DocId, string Kind);
