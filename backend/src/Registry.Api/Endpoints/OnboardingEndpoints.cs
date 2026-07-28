using MageRide.Registry.Onboarding;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Registry.Endpoints;

/// <summary>
/// Profile Setup and the four-step Mode-C onboarding wizard (AL-27, AL-29, AL-30).
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile Setup is not part of vehicle onboarding and does not live under
/// <c>/v1/vehicles</c>.</b> AL-27 splits driver onboarding in two: identity — name, required photo
/// and licence — precedes Home and needs no vehicle, and the four-step wizard is optional and
/// Mode-C only. A driver may sit at Home for a month with a profile and no vehicle, and the route
/// table says so.
/// </para>
/// <para>
/// Every route here requires the <c>driver</c> role, like the rest of <c>/v1/vehicles</c>.
/// </para>
/// </remarks>
public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var drivers = endpoints.MapGroup("/v1/drivers")
            .WithTags("drivers")
            .RequireMageRideRole(MageRideRoles.Driver);

        drivers.MapPut("/profile", UpsertProfileAsync).WithName("upsertDriverProfile");

        var vehicles = endpoints.MapGroup("/v1/vehicles")
            .WithTags("vehicles")
            .RequireMageRideRole(MageRideRoles.Driver);

        vehicles.MapPut("/{vehicleId}/onboarding/{step}", SaveStepAsync).WithName("saveVehicleOnboardingStep");
        vehicles.MapGet("/{vehicleId}/onboarding-status", GetStatusAsync).WithName("getVehicleOnboardingStatus");

        return endpoints;
    }

    private static async Task<Ok<DriverProfileResponse>> UpsertProfileAsync(
        UpsertDriverProfileBody? body,
        HttpContext context,
        IOnboardingService onboarding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);

        var result = await onboarding.UpsertProfileAsync(
            new UpsertDriverProfileCommand(
                context.User.RequireSubjectId(),
                body?.DriverName,
                body?.ProfilePhotoFileId,
                body?.LicenseFrontFileId,
                body?.LicenseBackFileId,
                body?.NicNo,
                body?.AllowedVehicleTypes),
            cancellationToken);

        return TypedResults.Ok(DriverProfileResponse.From(result));
    }

    private static async Task<Ok<SaveOnboardingStepResponse>> SaveStepAsync(
        string vehicleId,
        string step,
        OnboardingStepBody? body,
        HttpContext context,
        IOnboardingService onboarding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);

        var state = await onboarding.SaveStepAsync(
            new SaveOnboardingStepCommand(
                context.User.RequireSubjectId(),
                VehicleEndpoints.RequireVehicleId(vehicleId),
                step,
                body?.RegistrationNumber,
                body?.VehicleType,
                body?.FileId,
                body?.FileIdBack,
                body?.Fields),
            cancellationToken);

        return TypedResults.Ok(SaveOnboardingStepResponse.From(state, step));
    }

    private static async Task<Ok<OnboardingStatusResponse>> GetStatusAsync(
        string vehicleId, HttpContext context, IOnboardingService onboarding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(onboarding);

        var state = await onboarding.GetStateAsync(
            context.User.RequireSubjectId(), VehicleEndpoints.RequireVehicleId(vehicleId), cancellationToken);

        return TypedResults.Ok(OnboardingStatusResponse.From(state));
    }
}
