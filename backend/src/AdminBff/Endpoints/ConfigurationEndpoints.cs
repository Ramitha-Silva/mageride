using System.Globalization;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Platform;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// SCR-AP-007 — the Configuration group: tariffs, launch cities, feature flags, trains and
/// announcements (US-14.4, AL-27, US-14.12, US-2.17/2.18, US-14.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three URD §2.3 rows, and the split is the spec's.</b> Tariffs are <b>Platform config —
/// pricing</b> (Admin ⚙, Finance ⚙ rates, Super Admin ✅), cities, flags and trains are
/// <b>Platform config — settings</b> (Admin ◐ subset, Super Admin ✅, everybody else ➖), and
/// announcements are their own row (Admin ✅, Super Admin ✅). A Finance Officer may reprice a ride
/// and may not launch a city; that is exactly what the two rows say, and one shared gate would have
/// to be wrong about one of them.
/// </para>
/// <para>
/// <b>Trains are gated on Platform settings, and that is a resolution rather than a reading.</b>
/// D3' marks <c>POST /v1/admin/trains</c> "admin", but URD §2.3 has no train row — the nearest,
/// "Fleet — org &amp; vehicle onboarding", gives Admin 👁 and would refuse the very role D3' names.
/// Platform settings is the row that yields exactly {Admin, Super Admin}, which is what "train
/// admin-only" means, and D2 puts the screen in the Configuration group beside the GTFS manager.
/// Raised as a gap in the C062 handoff.
/// </para>
/// </remarks>
internal static class ConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapConfigurationEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapPut("/fares/tariffs", UpdateTariffsAsync)
            .WithName("updateFareTariffs")
            .WithSummary("Publish a new Mode C tariff version and the peak/night windows (US-14.4).")
            .RequireFeature(FeatureAreas.PlatformPricing, PermissionGrant.Configure)
            .Audited(AdminAuditActions.TariffsPublished, AdminAuditActions.TariffEntity);

        admin.MapPost("/config/cities", CreateCityAsync)
            .WithName("createOperatingCity")
            .WithSummary("Add a launch city — no app release needed (AL-27).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.CityCreated, AdminAuditActions.CityEntity);

        admin.MapPatch("/config/cities/{cityCode}", UpdateCityAsync)
            .WithName("updateOperatingCity")
            .WithSummary("Edit or deactivate a launch city (AL-27).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.CityUpdated, AdminAuditActions.CityEntity);

        // Δ C062 — URD §2.3 gives feature flags a matrix row and no contract had a route.
        admin.MapGet("/config/feature-flags", ListFlagsAsync)
            .WithName("listFeatureFlags")
            .WithSummary("Every platform feature flag and its current state (US-14.12).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Read);

        admin.MapPut("/config/feature-flags/{key}", SetFlagAsync)
            .WithName("setFeatureFlag")
            .WithSummary("Turn a platform feature flag on or off (US-14.12).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.FeatureFlagSet, AdminAuditActions.FeatureFlagEntity);

        admin.MapPost("/trains", CreateTrainAsync)
            .WithName("createTrain")
            .WithSummary("Register a train — admin-only Mode A (US-2.17).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.TrainCreated, AdminAuditActions.VehicleEntity);

        admin.MapPut("/trains/{trainId:guid}", UpdateTrainAsync)
            .WithName("updateTrain")
            .WithSummary("Edit a train (US-2.18).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.TrainUpdated, AdminAuditActions.VehicleEntity);

        admin.MapDelete("/trains/{trainId:guid}", RetireTrainAsync)
            .WithName("deleteTrain")
            .WithSummary("Retire a train; historical trips keep their reference (US-2.18).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.TrainRetired, AdminAuditActions.VehicleEntity);

        return admin;
    }

    private static async Task<Ok<TariffsResponse>> UpdateTariffsAsync(
        UpdateTariffsBody? body,
        HttpContext context,
        IPlatformConfigService config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        return TypedResults.Ok(await config.PublishTariffsAsync(
            body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<Created<OperatingCityResponse>> CreateCityAsync(
        OperatingCityBody? body,
        HttpContext context,
        IPlatformConfigService config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        var city = await config.CreateCityAsync(body, context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Created($"/v1/admin/config/cities/{city.Code}", city);
    }

    private static async Task<Ok<OperatingCityResponse>> UpdateCityAsync(
        string cityCode,
        UpdateOperatingCityBody? body,
        HttpContext context,
        IPlatformConfigService config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        return TypedResults.Ok(await config.UpdateCityAsync(
            cityCode, body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<Ok<IReadOnlyList<FeatureFlagResponse>>> ListFlagsAsync(
        IPlatformConfigService config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        return TypedResults.Ok(await config.ListFlagsAsync(cancellationToken));
    }

    private static async Task<Ok<FeatureFlagResponse>> SetFlagAsync(
        string key,
        SetFeatureFlagBody? body,
        HttpContext context,
        IPlatformConfigService config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        return TypedResults.Ok(await config.SetFlagAsync(
            key, body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<Created<TrainResponse>> CreateTrainAsync(
        TrainBody? body,
        HttpContext context,
        ITrainService trains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trains);

        var train = await trains.CreateAsync(body, context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.Created($"/v1/admin/trains/{train.TrainId:D}", train);
    }

    private static async Task<Ok<TrainResponse>> UpdateTrainAsync(
        Guid trainId,
        TrainBody? body,
        HttpContext context,
        ITrainService trains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trains);

        return TypedResults.Ok(await trains.UpdateAsync(
            trainId, body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<NoContent> RetireTrainAsync(
        Guid trainId,
        HttpContext context,
        ITrainService trains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trains);

        await trains.RetireAsync(trainId, context.User.RequireSubjectId(), cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Parses <c>HH:mm</c> as the contract spells it. Invariant culture, because a peak window is a
    /// wall-clock string in a contract and not a localised time.
    /// </summary>
    internal static TimeOnly ParseLocalTime(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        errors[field] = [$"{field} must be a 24-hour local time, HH:mm (Asia/Colombo)."];
        return default;
    }
}
