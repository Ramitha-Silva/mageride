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
    IReadOnlyList<string>? AllowedVehicleTypes = null);

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
    public static DriverProfileResponse From(DriverProfileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new DriverProfileResponse(
            result.Profile.DriverId.ToString(),
            result.Status,
            result.Profile.DisplayName,
            result.Profile.PhotoUrl,
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
/// JSON with upload ids only. The contract also offers <c>multipart/form-data</c> carrying the
/// bytes; that variant is not mapped, because the bytes belong in object storage (D-36, NFR-28)
/// and streaming them through registry-svc would put an unredacted image on this service's disk —
/// exactly what the redaction pre-pass exists to avoid. Recorded in the C029 handoff.
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
