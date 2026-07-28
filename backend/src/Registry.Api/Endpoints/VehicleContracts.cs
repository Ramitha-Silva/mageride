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
/// <c>registry.yaml#/components/schemas/VehicleSummary</c>, plus the two fields US-9.7 and
/// US-13.9 need.
/// </summary>
/// <param name="IsSelected">
/// Whether this is the one vehicle the driver may go live on (US-9.6). Additive to the
/// contract, which has nowhere to carry it — see the C021 handoff.
/// </param>
/// <param name="Source">
/// <c>owned</c> or <c>assigned</c>. Also additive: US-13.9 renders the assigned ones in a
/// separate "Temporarily assigned to me" group, and the contract's <c>VehicleSummary</c> cannot
/// say which is which. See the C028 handoff.
/// </param>
/// <param name="FleetId">The assigning fleet, which US-13.9's group header shows. Null when owned.</param>
public sealed record VehicleSummaryResponse(
    string VehicleId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Status,
    string OnboardingStatus,
    string DispatchState,
    bool IsSelected,
    string Source,
    string? FleetId,
    bool IsGoLiveEligible)
{
    public static VehicleSummaryResponse From(DriverVehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        var entitlement = vehicle.Entitlement;

        return new VehicleSummaryResponse(
            entitlement.VehicleId.ToString(),
            entitlement.RegistrationNumber,
            entitlement.VehicleType,
            entitlement.Mode,
            entitlement.Status,
            entitlement.OnboardingStatus,
            entitlement.DispatchState,
            vehicle.IsSelected,
            entitlement.Source,
            entitlement.FleetId?.ToString(),
            entitlement.IsGoLiveEligible);
    }
}

/// <summary>
/// 200 body of <c>GET /v1/vehicles/mine</c> — US-2.8's list and US-13.9's second group.
/// </summary>
/// <param name="Items">
/// Everything the driver may operate, owned first. The contract's only field; a client that
/// ignores the grouping still gets a correct list.
/// </param>
/// <param name="Assigned">
/// The "Temporarily assigned to me" group, split out so the app does not have to filter
/// <paramref name="Items"/> to render the header US-13.9 asks for. Additive — see the C028 handoff.
/// </param>
public sealed record MyVehiclesResponse(
    IReadOnlyList<VehicleSummaryResponse> Items, IReadOnlyList<VehicleSummaryResponse> Assigned)
{
    public static MyVehiclesResponse From(IReadOnlyList<DriverVehicle> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);

        var items = vehicles.Select(VehicleSummaryResponse.From).ToArray();

        return new MyVehiclesResponse(
            items,
            [.. items.Where(item => item.Source == EligibilitySources.Assigned)]);
    }
}

/// <summary>200 body of <c>POST /v1/vehicles/{vehicleId}/select-live</c>.</summary>
/// <param name="ReleasedVehicleId">
/// The vehicle this selection replaced, if any. US-9.6 makes the release the point of the call,
/// and naming it lets the app update both rows without re-listing.
/// </param>
public sealed record LiveSelectionResponse(
    string VehicleId,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string Source,
    string? ReleasedVehicleId,
    DateTimeOffset SelectedAt)
{
    public static LiveSelectionResponse From(LiveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return new LiveSelectionResponse(
            selection.Vehicle.VehicleId.ToString(),
            selection.Vehicle.RegistrationNumber,
            selection.Vehicle.VehicleType,
            selection.Vehicle.Mode,
            selection.Vehicle.Source,
            selection.ReleasedVehicleId?.ToString(),
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
