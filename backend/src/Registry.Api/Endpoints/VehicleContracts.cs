using System.Text.Json.Serialization;
using MageRide.Registry.Domain;
using MageRide.Registry.Onboarding;
using MageRide.Registry.Vehicles;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// Body of <c>POST /v1/vehicles</c>
/// (<c>registry.yaml#/components/schemas/VehicleRegistration</c>).
/// </summary>
/// <param name="InsuranceFileId">
/// Optional, with the three ids that follow. The contract marks all four required and AL-30 in the
/// same specification has the wizard "create a NEW vehicle at Step 1/4" — a vehicle that must
/// arrive with four documents has no Step 2/4 to walk to. Sent, they are onboarded here in one
/// shot; absent, the wizard saves each step. See the C029 handoff.
/// </param>
/// <param name="DriverPhotoFileId">
/// Accepted and ignored. Profile Setup owns the driver's photo (<c>PUT /v1/drivers/profile</c>,
/// AL-27) and per-vehicle overrides are <c>PUT /v1/vehicles/{id}/driver-profile</c> (US-2.12);
/// honouring it here would give the same picture three writers.
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
    public static VerificationResponse From(OnboardingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new VerificationResponse(
            state.StepStatus(OnboardingSteps.Details),
            state.StepStatus(OnboardingSteps.Insurance),
            state.StepStatus(OnboardingSteps.Revenue),
            state.StepStatus(OnboardingSteps.Photos));
    }
}

/// <summary>
/// 201 body of <c>POST /v1/vehicles</c>.
/// </summary>
/// <remarks>
/// <c>ocrJobId</c> is present only when an extraction was actually queued. The contract makes it
/// required; a registration that carried no documents queued nothing, and inventing an identifier
/// no service will ever recognise would leave a client polling it forever. Recorded as a contract
/// gap in the C021 handoff and unchanged by C029.
/// </remarks>
public sealed record RegisterVehicleResponse(
    string VehicleId,
    string Status,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    VerificationResponse Verification,
    string OnboardingStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextStep,
    string? OcrJobId,
    DateTimeOffset CreatedAt)
{
    public static RegisterVehicleResponse From(RegisteredVehicle registered)
    {
        ArgumentNullException.ThrowIfNull(registered);

        var vehicle = registered.Vehicle;

        return new RegisterVehicleResponse(
            vehicle.Id.ToString(),
            vehicle.Status,
            vehicle.RegistrationNumber,
            vehicle.VehicleType,
            vehicle.Mode,
            VerificationResponse.From(registered.Onboarding),
            vehicle.OnboardingStatus,
            // Additive: AL-30's resume point is what the app opens next, and a client that has
            // just registered would otherwise have to call onboarding-status to learn it.
            registered.Onboarding.NextStep,
            registered.Onboarding.OcrJobId?.ToString(),
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
