using MageRide.Registry.Domain;
using MageRide.Registry.Vehicles;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// Body of <c>POST /v1/vehicles</c>
/// (<c>registry.yaml#/components/schemas/VehicleRegistration</c>).
/// </summary>
/// <param name="InsuranceFileId">
/// Accepted and ignored. The four document ids are required by the contract, but this slice has
/// no upload surface to obtain one from and no ocr-svc to hand it to (C029/C054). They are
/// declared so a client written against the contract compiles and its request is not rejected.
/// </param>
public sealed record RegisterVehicleBody(
    string? RegistrationNumber,
    string? VehicleType,
    string? Mode,
    string? DriverName,
    string? InsuranceFileId = null,
    string? RevenueLicenseFileId = null,
    string? VehiclePhotoFrontFileId = null,
    string? VehiclePhotoBackFileId = null,
    string? DriverPhotoFileId = null);

/// <summary>Per-step onboarding verdicts (AL-30).</summary>
public sealed record VerificationResponse(
    string VehicleDetails, string Insurance, string RevenueLicense, string Photos)
{
    /// <summary>
    /// What every registration this slice creates looks like: nothing has been saved, so every
    /// step is <c>PENDING_INPUT</c> and the vehicle is Incomplete. C029 moves them.
    /// </summary>
    public static readonly VerificationResponse NothingSubmitted = new(
        StepVerdicts.PendingInput, StepVerdicts.PendingInput, StepVerdicts.PendingInput, StepVerdicts.PendingInput);
}

/// <summary>
/// 201 body of <c>POST /v1/vehicles</c>.
/// </summary>
/// <remarks>
/// The contract also makes <c>ocrJobId</c> required, and it is absent here: no OCR is queued, so
/// any value would be an identifier no service will ever recognise and a client polling it would
/// wait forever. Recorded as a contract gap in the C021 handoff — <c>ocrJobId</c> belongs to the
/// responses that actually queued a job.
/// </remarks>
public sealed record RegisterVehicleResponse(
    string VehicleId,
    string Status,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    VerificationResponse Verification,
    string OnboardingStatus,
    DateTimeOffset CreatedAt)
{
    public static RegisterVehicleResponse From(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        return new RegisterVehicleResponse(
            vehicle.Id.ToString(),
            vehicle.Status,
            vehicle.RegistrationNumber,
            vehicle.VehicleType,
            vehicle.Mode,
            VerificationResponse.NothingSubmitted,
            vehicle.OnboardingStatus,
            vehicle.CreatedAt);
    }
}

/// <summary>
/// <c>registry.yaml#/components/schemas/VehicleSummary</c>, plus the field US-9.7 needs.
/// </summary>
/// <param name="IsSelected">
/// Whether this is the one vehicle the driver may go live on (US-9.6). Additive to the
/// contract, which has nowhere to carry it — see the C021 handoff.
/// </param>
public sealed record VehicleSummaryResponse(
    string VehicleId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string OnboardingStatus,
    string DispatchState,
    bool IsSelected)
{
    public static VehicleSummaryResponse From(OwnedVehicle owned)
    {
        ArgumentNullException.ThrowIfNull(owned);

        return new VehicleSummaryResponse(
            owned.Vehicle.Id.ToString(),
            owned.Vehicle.RegistrationNumber,
            owned.Vehicle.VehicleType,
            owned.Vehicle.Mode,
            owned.Vehicle.Status,
            owned.Vehicle.OnboardingStatus,
            owned.Vehicle.DispatchState,
            owned.IsSelected);
    }
}

/// <summary>200 body of <c>GET /v1/vehicles/mine</c>.</summary>
public sealed record MyVehiclesResponse(IReadOnlyList<VehicleSummaryResponse> Items);

/// <summary>200 body of <c>POST /v1/vehicles/{vehicleId}/select-live</c>.</summary>
public sealed record LiveSelectionResponse(
    string VehicleId, string RegistrationNumber, string VehicleType, string Mode, DateTimeOffset SelectedAt)
{
    public static LiveSelectionResponse From(LiveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return new LiveSelectionResponse(
            selection.Vehicle.Id.ToString(),
            selection.Vehicle.RegistrationNumber,
            selection.Vehicle.VehicleType,
            selection.Vehicle.Mode,
            selection.SelectedAt);
    }
}

/// <summary>200 body of the dev seed path's approve.</summary>
public sealed record ApproveVehicleResponse(string VehicleId, string Status, string OnboardingStatus)
{
    public static ApproveVehicleResponse From(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return new ApproveVehicleResponse(vehicle.Id.ToString(), vehicle.Status, vehicle.OnboardingStatus);
    }
}
