using MageRide.Registry.Onboarding;
using MageRide.Registry.Vehicles;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
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

        // Δ AL-58/AL-59 — where a driver's swept earnings go, and the LankaQR a passenger scans to
        // pay them. Replaces D-11's merchant binding, which never existed (AL-57).
        drivers.MapGet("/payout-profile", ReadPayoutProfileAsync).WithName("getDriverPayoutProfile");
        drivers.MapPut("/payout-profile", UpsertPayoutProfileAsync).WithName("upsertDriverPayoutProfile");
        drivers.MapPost("/payout-profile/documents", UploadPayoutDocumentAsync)
            .WithName("uploadDriverPayoutDocument")
            .DisableAntiforgery();

        var vehicles = endpoints.MapGroup("/v1/vehicles")
            .WithTags("vehicles")
            .RequireMageRideRole(MageRideRoles.Driver);

        vehicles.MapPut("/{vehicleId}/onboarding/{step}", SaveStepAsync).WithName("saveVehicleOnboardingStep");
        vehicles.MapGet("/{vehicleId}/onboarding-status", GetStatusAsync).WithName("getVehicleOnboardingStatus");

        return endpoints;
    }

    /// <summary>
    /// <c>GET /v1/drivers/payout-profile</c> — the version the driver is looking at (AL-58).
    /// </summary>
    private static async Task<Ok<DriverPayoutProfileResponse>> ReadPayoutProfileAsync(
        HttpContext context, IDriverPayoutProfileService profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var profile = await profiles.ReadAsync(context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Ok(DriverPayoutProfileResponse.From(profile));
    }

    /// <summary>
    /// <c>PUT /v1/drivers/payout-profile</c> — set or change the bank details (AL-58).
    /// </summary>
    /// <remarks>
    /// Always the caller's own: the subject comes from the token and there is no path parameter, so
    /// there is no route by which one driver could write another's bank account.
    /// </remarks>
    private static async Task<Ok<DriverPayoutProfileResponse>> UpsertPayoutProfileAsync(
        DriverPayoutProfileBody? body,
        HttpContext context,
        IDriverPayoutProfileService profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);

        var saved = await profiles.UpsertAsync(
            context.User.RequireSubjectId(),
            new DriverPayoutDraft(
                body?.Bank?.Trim() ?? string.Empty,
                body?.Branch?.Trim() ?? string.Empty,
                body?.AccountNo?.Trim() ?? string.Empty,
                body?.AccountHolderName?.Trim() ?? string.Empty),
            cancellationToken);

        return TypedResults.Ok(DriverPayoutProfileResponse.From(saved));
    }

    /// <summary>
    /// <c>POST /v1/drivers/payout-profile/documents</c> — proof of account, or the driver's own
    /// LankaQR (AL-58/AL-59).
    /// </summary>
    /// <remarks>
    /// The bytes are written before the <c>docs.uploads</c> row, which is fleet-svc's rule for the
    /// same slots and for the same reason: a crash between them leaves an orphan file that NFR-28's
    /// deadline sweeps, while the other order leaves a profile pointing at a document the officer is
    /// told exists and cannot open.
    /// </remarks>
    private static async Task<Created<DriverPayoutDocumentResponse>> UploadPayoutDocumentAsync(
        HttpContext context,
        IDriverPayoutProfileService profiles,
        IPayoutDocumentStore documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(documents);

        if (!context.Request.HasFormContentType)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["The request must be multipart/form-data carrying `kind` and `file`."],
            });
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var kind = form["kind"].ToString().Trim();
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["file is required and must not be empty."],
            });
        }

        var driverId = context.User.RequireSubjectId();

        await using var content = file.OpenReadStream();

        var uploadId = await documents.WriteAsync(driverId, kind, content, cancellationToken);

        await profiles.AttachAsync(driverId, uploadId, kind, cancellationToken);

        return TypedResults.Created(
            (string?)null, new DriverPayoutDocumentResponse(uploadId.ToString(), kind));
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
