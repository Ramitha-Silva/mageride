using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Platform;

/// <summary>
/// The Configuration group's writes: the Mode C rate card, launch cities and feature flags.
/// </summary>
public interface IPlatformConfigService
{
    Task<TariffsResponse> PublishTariffsAsync(
        UpdateTariffsBody? body, Guid actorId, CancellationToken cancellationToken);

    Task<OperatingCityResponse> CreateCityAsync(
        OperatingCityBody? body, Guid actorId, CancellationToken cancellationToken);

    Task<OperatingCityResponse> UpdateCityAsync(
        string cityCode, UpdateOperatingCityBody? body, Guid actorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FeatureFlagResponse>> ListFlagsAsync(CancellationToken cancellationToken);

    Task<FeatureFlagResponse> SetFlagAsync(
        string key, SetFeatureFlagBody? body, Guid actorId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformConfigService"/>
/// <remarks>
/// <para>
/// <b>Validation is exhaustive before anything is written.</b> Every field of a tariff publish is
/// checked into one error dictionary and the whole request is refused together — an operator
/// correcting a rate card one 400 at a time would publish four versions to get one right, and each
/// of those versions is permanent under D-10.
/// </para>
/// <para>
/// <b>A city PATCH is read-modify-write inside one transaction.</b> The row is locked, the sparse
/// body is applied over it, and the before-image the audit row carries is the row that was actually
/// replaced — not the one the caller last saw on their screen.
/// </para>
/// </remarks>
internal sealed class PlatformConfigService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IPlatformConfigRepository repository,
    IAdminAuditContext audit,
    TimeProvider clock,
    ILogger<PlatformConfigService> logger) : IPlatformConfigService
{
    /// <summary>AL-09's canonical vehicle types — the CHECK on <c>registry.vehicles.vehicle_type</c>.</summary>
    /// <remarks>
    /// <c>fares.tariffs.vehicle_type</c> is bare <c>TEXT</c> with no CHECK of its own (migration
    /// 1001 says why: there is no catalog table to point at), so without this an admin could publish
    /// a rate for <c>car</c> — a row that looks configured on the Config screen and prices no ride
    /// ever taken. There is no <c>car</c>: it maps to <c>sedan</c>.
    /// </remarks>
    private static readonly HashSet<string> CanonicalVehicleTypes = new(StringComparer.Ordinal)
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck", "bus", "train",
    };

    /// <summary>The two kinds <c>fares.peak_windows.kind</c> admits (migration 1001).</summary>
    private static readonly HashSet<string> WindowKinds = new(StringComparer.Ordinal) { "peak", "night" };

    /// <summary>§0 Money: every amount on this platform is LKR minor units.</summary>
    private const string Currency = "LKR";

    public async Task<TariffsResponse> PublishTariffsAsync(
        UpdateTariffsBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var now = clock.GetUtcNow();

        var items = body?.Tariffs;

        if (items is null || items.Count == 0)
        {
            errors["tariffs"] = ["tariffs is required and must carry at least one rate."];
            throw new MageRideValidationException(errors);
        }

        var tariffs = new List<TariffRow>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var parsed = ParseTariff(items[index], index, errors);

            if (parsed is not null)
            {
                tariffs.Add(parsed);
            }
        }

        var windows = ParseWindows(body!.PeakWindows, errors);

        // A rate card with two rows for one vehicle type has no defined meaning: the unique index
        // is on (vehicle_type, effective_from), so the second would silently overwrite the first.
        var duplicate = tariffs.GroupBy(static t => t.VehicleType, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);

        if (duplicate is not null)
        {
            errors["tariffs"] = [$"'{duplicate.Key}' appears more than once; one rate per vehicle type."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        // Backdating is refused. D-10 makes a published version permanent and fare-svc resolves
        // "the row in force at this instant", so a version stamped an hour ago would reprice rides
        // that have already been quoted and, for a cash fare settled tomorrow, already been agreed.
        var effectiveFrom = body.EffectiveFrom ?? now;

        if (effectiveFrom < now)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["effectiveFrom"] =
                [
                    "effectiveFrom cannot be in the past: a published tariff version is permanent (D-10) and "
                    + "backdating one would reprice rides that were already quoted.",
                ],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await repository.ReadTariffsAsync(now, cancellationToken);
        var beforeWindows = await repository.ReadWindowsAsync(cancellationToken);

        await repository.PublishTariffsAsync(unitOfWork, effectiveFrom, tariffs, windows, cancellationToken);

        audit.Record(
            entityId: null,
            before: new { tariffs = before, peakWindows = beforeWindows },
            after: new { effectiveFrom, tariffs, peakWindows = windows ?? beforeWindows });

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Tariff version effective {EffectiveFrom} published by {ActorId}: {Count} rate(s), {Windows} window(s).",
            effectiveFrom, actorId, tariffs.Count, windows?.Count ?? beforeWindows.Count);

        return new TariffsResponse(
            effectiveFrom,
            [.. tariffs.Select(static t => new TariffResponse(
                t.VehicleType, t.FirstKmMinor, t.PerKmMinor, t.PeakSurchargePct, t.NightSurchargePct, t.Currency))],
            [.. (windows ?? beforeWindows).Select(static w => new PeakWindowResponse(
                w.Kind, w.StartLocal.ToString("HH:mm"), w.EndLocal.ToString("HH:mm"), w.MultiplierPct))]);
    }

    public async Task<OperatingCityResponse> CreateCityAsync(
        OperatingCityBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var code = body?.Code?.Trim();

        if (string.IsNullOrEmpty(code) || !System.Text.RegularExpressions.Regex.IsMatch(
                code, "^[a-z][a-z0-9_]{1,40}$", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100)))
        {
            errors["code"] = ["code must match ^[a-z][a-z0-9_]{1,40}$ — it is the slug users' accounts point at."];
        }

        // All three languages, always. D-26 is not a preference and `GET /v1/config/cities` serves
        // this row to a first-run screen in whichever language the handset is set to; a city with
        // two names is a city that renders blank for some passengers.
        RequireName(body?.NameEn, "nameEn", errors);
        RequireName(body?.NameSi, "nameSi", errors);
        RequireName(body?.NameTa, "nameTa", errors);

        var centroid = ParseCentroid(body?.Centroid, errors, required: true);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var city = new OperatingCity(
            code!,
            body!.NameEn!.Trim(),
            body.NameSi!.Trim(),
            body.NameTa!.Trim(),
            centroid!.Value.Lat,
            centroid.Value.Lng,
            body.SortOrder ?? 0,
            IsActive: true);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var created = await repository.InsertCityAsync(unitOfWork, city, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.Conflict, $"A launch city with code '{code}' already exists.");

        audit.Record(entityId: null, after: created, entityType: AdminAuditActions.CityEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Launch city {Code} created by {ActorId}.", created.Code, actorId);

        return ToResponse(created);
    }

    public async Task<OperatingCityResponse> UpdateCityAsync(
        string cityCode, UpdateOperatingCityBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cityCode);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var centroid = ParseCentroid(body?.Centroid, errors, required: false);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await repository.ReadCityAsync(unitOfWork, cityCode, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, $"No launch city '{cityCode}'.");

        var after = before with
        {
            NameEn = Coalesce(body?.NameEn, before.NameEn),
            NameSi = Coalesce(body?.NameSi, before.NameSi),
            NameTa = Coalesce(body?.NameTa, before.NameTa),
            CentroidLat = centroid?.Lat ?? before.CentroidLat,
            CentroidLng = centroid?.Lng ?? before.CentroidLng,
            SortOrder = body?.SortOrder ?? before.SortOrder,
            IsActive = body?.Active ?? before.IsActive,
        };

        var updated = await repository.UpdateCityAsync(unitOfWork, after, cancellationToken);

        audit.Record(entityId: null, before: before, after: updated, entityType: AdminAuditActions.CityEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Launch city {Code} updated by {ActorId}; active {WasActive} -> {IsActive}.",
            updated.Code, actorId, before.IsActive, updated.IsActive);

        return ToResponse(updated);
    }

    public async Task<IReadOnlyList<FeatureFlagResponse>> ListFlagsAsync(CancellationToken cancellationToken)
    {
        var flags = await repository.ListFlagsAsync(cancellationToken);

        return [.. flags.Select(static flag => new FeatureFlagResponse(
            flag.Key, flag.Enabled, flag.Description, flag.UpdatedBy, flag.UpdatedAt))];
    }

    public async Task<FeatureFlagResponse> SetFlagAsync(
        string key, SetFeatureFlagBody? body, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (body?.Enabled is not { } enabled)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["enabled"] = ["enabled is required — a PUT that omitted it would leave the flag's state ambiguous."],
            });
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(
                key, "^[a-z][a-z0-9_.-]{1,80}$", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100)))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["key"] = ["key must match ^[a-z][a-z0-9_.-]{1,80}$ (ck_feature_flags_key, migration 0202)."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var before = await repository.ReadFlagAsync(unitOfWork, key, cancellationToken);

        var after = await repository.SetFlagAsync(
            unitOfWork, key, enabled, body.Description?.Trim(), actorId, cancellationToken);

        audit.Record(
            entityId: null,
            before: before is null ? null : new { before.Key, before.Enabled },
            after: new { after.Key, after.Enabled, after.Description });

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Feature flag {Key} set to {Enabled} by {ActorId} (was {Before}).",
            after.Key, after.Enabled, actorId, before?.Enabled.ToString() ?? "unset");

        return new FeatureFlagResponse(after.Key, after.Enabled, after.Description, after.UpdatedBy, after.UpdatedAt);
    }

    private TariffRow? ParseTariff(TariffInput input, int index, Dictionary<string, string[]> errors)
    {
        var type = input?.VehicleType?.Trim();
        var ok = true;

        if (string.IsNullOrEmpty(type) || !CanonicalVehicleTypes.Contains(type))
        {
            errors[$"tariffs[{index}].vehicleType"] =
            [
                $"'{input?.VehicleType}' is not one of AL-09's canonical vehicle types: "
                + $"{string.Join(", ", CanonicalVehicleTypes.Order(StringComparer.Ordinal))}.",
            ];

            ok = false;
        }

        if (input?.FirstKmMinor is not { } firstKm || firstKm < 0)
        {
            errors[$"tariffs[{index}].firstKmMinor"] =
                ["firstKmMinor is required and is an unsigned integer in minor units (Rs × 100)."];
            ok = false;
        }

        if (input?.PerKmMinor is not { } perKm || perKm < 0)
        {
            errors[$"tariffs[{index}].perKmMinor"] =
                ["perKmMinor is required and is an unsigned integer in minor units (Rs × 100)."];
            ok = false;
        }

        if (input?.PeakSurchargePct is < 0)
        {
            errors[$"tariffs[{index}].peakSurchargePct"] = ["peakSurchargePct cannot be negative."];
            ok = false;
        }

        if (input?.NightSurchargePct is < 0)
        {
            errors[$"tariffs[{index}].nightSurchargePct"] = ["nightSurchargePct cannot be negative."];
            ok = false;
        }

        if (input?.Currency is { Length: > 0 } currency && !string.Equals(currency, Currency, StringComparison.Ordinal))
        {
            errors[$"tariffs[{index}].currency"] = [$"currency must be {Currency}."];
            ok = false;
        }

        return ok
            ? new TariffRow(
                type!,
                input!.FirstKmMinor!.Value,
                input.PerKmMinor!.Value,
                // The contract's own defaults (admin-bff.yaml#Tariff), which are also 1001's.
                input.PeakSurchargePct ?? 20,
                input.NightSurchargePct ?? 15,
                Currency)
            : null;
    }

    /// <remarks>
    /// Null in, null out: the caller did not mention the windows and they are left alone. An empty
    /// list clears them, which is a different intent and is honoured as one — see
    /// <c>IPlatformConfigRepository.PublishTariffsAsync</c>.
    /// </remarks>
    private static List<PeakWindowRow>? ParseWindows(
        IReadOnlyList<PeakWindowInput>? inputs, Dictionary<string, string[]> errors)
    {
        if (inputs is null)
        {
            return null;
        }

        var windows = new List<PeakWindowRow>(inputs.Count);

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var kind = input?.Kind?.Trim();

            if (string.IsNullOrEmpty(kind) || !WindowKinds.Contains(kind))
            {
                errors[$"peakWindows[{index}].kind"] = ["kind must be 'peak' or 'night'."];
                continue;
            }

            var start = ConfigurationEndpoints.ParseLocalTime(
                input!.StartLocal, $"peakWindows[{index}].startLocal", errors);
            var end = ConfigurationEndpoints.ParseLocalTime(
                input.EndLocal, $"peakWindows[{index}].endLocal", errors);

            if (input.MultiplierPct is not { } multiplier || multiplier < 0)
            {
                errors[$"peakWindows[{index}].multiplierPct"] = ["multiplierPct is required and cannot be negative."];
                continue;
            }

            // No ordering check. `end_local < start_local` is legal and is how the night window
            // wraps midnight — migration 1001 declines to constrain it for exactly this reason, and
            // "fixing" it here would delete the 22:00–05:00 window every time it was saved.
            windows.Add(new PeakWindowRow(kind, start, end, multiplier));
        }

        return windows;
    }

    private static (double Lat, double Lng)? ParseCentroid(
        GeoPointBody? centroid, Dictionary<string, string[]> errors, bool required)
    {
        if (centroid is null)
        {
            if (required)
            {
                errors["centroid"] = ["centroid is required — it seeds the map for everybody who picks this city."];
            }

            return null;
        }

        if (centroid.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["centroid.lat"] = ["lat must be between -90 and 90."];
        }

        if (centroid.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["centroid.lng"] = ["lng must be between -180 and 180."];
        }

        return errors.Count > 0 ? null : (centroid.Lat!.Value, centroid.Lng!.Value);
    }

    private static void RequireName(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} is required: all three languages ship together (D-26)."];
        }
    }

    private static string Coalesce(string? candidate, string current) =>
        string.IsNullOrWhiteSpace(candidate) ? current : candidate.Trim();

    private static OperatingCityResponse ToResponse(OperatingCity city) => new(
        city.Code,
        city.NameEn,
        city.NameSi,
        city.NameTa,
        new GeoPointBody(city.CentroidLat, city.CentroidLng),
        city.SortOrder,
        city.IsActive);
}
