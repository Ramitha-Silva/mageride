using Dapper;
using MageRide.AdminBff.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Persistence;

/// <summary>
/// The three configuration tables admin-bff writes: <c>fares.tariffs</c> + <c>fares.peak_windows</c>
/// (US-14.4), <c>config.operating_cities</c> (AL-27) and <c>config.feature_flags</c> (US-14.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>These three, and no others, because no other service routes them.</b> The daily-fee rates and
/// the bulk-voucher tiers are subscription-svc's (<c>PUT /v1/admin/fees/rates</c>,
/// <c>/v1/admin/voucher-discount-tiers</c>) and the Driver-Level parameters are dispatch-svc's
/// (<c>/v1/admin/drivers/level-config</c>); `gateway-routes.json` sends all three past this service
/// at Order 20, and admin-bff contributes the Configuration nav group that puts them on one screen.
/// What is left over is what is here.
/// </para>
/// <para>
/// <b>A tariff version is inserted, never updated.</b> Migration 1001's
/// <c>ux_tariffs_type_effective</c> is what makes that expressible, and D-10 is why: a completed
/// ride must stay reconcilable against the rate that priced it, so the rate card is append-only and
/// fare-svc resolves "the row in force at this instant". The <c>ON CONFLICT</c> below is the one
/// exception the schema already allows — re-publishing the *same* <c>effective_from</c> corrects a
/// version that has not started yet, which is what an admin fixing a typo one minute later means.
/// </para>
/// </remarks>
public interface IPlatformConfigRepository
{
    /// <summary>The rate card in force at <paramref name="at"/>, for the audit's before-image.</summary>
    Task<IReadOnlyList<TariffRow>> ReadTariffsAsync(DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlyList<PeakWindowRow>> ReadWindowsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a new tariff version and, when <paramref name="windows"/> is given, replaces the
    /// surcharge windows.
    /// </summary>
    /// <remarks>
    /// Null windows leaves them alone; an empty list clears them. The two are different intents and
    /// a PUT that treated them the same would silently drop the night window every time somebody
    /// changed a per-km rate.
    /// </remarks>
    Task PublishTariffsAsync(
        IUnitOfWork unitOfWork,
        DateTimeOffset effectiveFrom,
        IReadOnlyCollection<TariffRow> tariffs,
        IReadOnlyCollection<PeakWindowRow>? windows,
        CancellationToken cancellationToken);

    Task<OperatingCity?> ReadCityAsync(IUnitOfWork unitOfWork, string code, CancellationToken cancellationToken);

    /// <summary>Inserts a launch city. Returns null when the code is already taken.</summary>
    Task<OperatingCity?> InsertCityAsync(IUnitOfWork unitOfWork, OperatingCity city, CancellationToken cancellationToken);

    Task<OperatingCity> UpdateCityAsync(IUnitOfWork unitOfWork, OperatingCity city, CancellationToken cancellationToken);

    Task<IReadOnlyList<FeatureFlag>> ListFlagsAsync(CancellationToken cancellationToken);

    Task<FeatureFlag?> ReadFlagAsync(IUnitOfWork unitOfWork, string key, CancellationToken cancellationToken);

    Task<FeatureFlag> SetFlagAsync(
        IUnitOfWork unitOfWork,
        string key,
        bool enabled,
        string? description,
        Guid actorId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformConfigRepository"/>
internal sealed class PlatformConfigRepository(INpgsqlConnectionFactory connections) : IPlatformConfigRepository
{
    private const string CityColumns =
        """
        code          AS Code,
        name_en       AS NameEn,
        name_si       AS NameSi,
        name_ta       AS NameTa,
        centroid_lat  AS CentroidLat,
        centroid_lng  AS CentroidLng,
        sort_order    AS SortOrder,
        is_active     AS IsActive
        """;

    private const string FlagColumns =
        """
        key         AS Key,
        enabled     AS Enabled,
        description AS Description,
        updated_by  AS UpdatedBy,
        updated_at  AS UpdatedAt
        """;

    public async Task<IReadOnlyList<TariffRow>> ReadTariffsAsync(
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // DISTINCT ON: the table holds every version ever published, and "the rate card" is the
        // newest row per type that has already taken effect.
        var rows = await connection.QueryAsync<TariffRow>(new CommandDefinition(
            """
            -- Widened in the SELECT, not in the table: the contract types every *Minor field
            -- int64 (§0 Money) and the surcharges int32, while 1001 stores INT and SMALLINT.
            -- Dapper matches a record constructor by exact type, so the cast belongs here rather
            -- than in a narrower CLR shape nobody else on the wire uses.
            SELECT vehicle_type              AS VehicleType,
                   first_km_minor::bigint    AS FirstKmMinor,
                   per_km_minor::bigint      AS PerKmMinor,
                   peak_surcharge_pct::int   AS PeakSurchargePct,
                   night_surcharge_pct::int  AS NightSurchargePct,
                   currency                  AS Currency
              FROM (SELECT DISTINCT ON (vehicle_type) *
                      FROM fares.tariffs
                     WHERE effective_from <= @At
                     ORDER BY vehicle_type, effective_from DESC) t
             ORDER BY vehicle_type;
            """,
            new { At = at },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<PeakWindowRow>> ReadWindowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PeakWindowRow>(new CommandDefinition(
            """
            SELECT kind                 AS Kind,
                   start_local          AS StartLocal,
                   end_local            AS EndLocal,
                   multiplier_pct::int  AS MultiplierPct
              FROM fares.peak_windows
             ORDER BY kind, start_local;
            """,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task PublishTariffsAsync(
        IUnitOfWork unitOfWork,
        DateTimeOffset effectiveFrom,
        IReadOnlyCollection<TariffRow> tariffs,
        IReadOnlyCollection<PeakWindowRow>? windows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(tariffs);

        foreach (var tariff in tariffs)
        {
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO fares.tariffs
                  (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct,
                   night_surcharge_pct, currency, effective_from)
                VALUES
                  (@VehicleType, @FirstKmMinor::int, @PerKmMinor::int, @PeakSurchargePct::smallint,
                   @NightSurchargePct::smallint, @Currency, @EffectiveFrom)
                ON CONFLICT (vehicle_type, effective_from) DO UPDATE
                  SET first_km_minor      = EXCLUDED.first_km_minor,
                      per_km_minor        = EXCLUDED.per_km_minor,
                      peak_surcharge_pct  = EXCLUDED.peak_surcharge_pct,
                      night_surcharge_pct = EXCLUDED.night_surcharge_pct,
                      currency            = EXCLUDED.currency;
                """,
                new
                {
                    tariff.VehicleType,
                    tariff.FirstKmMinor,
                    tariff.PerKmMinor,
                    tariff.PeakSurchargePct,
                    tariff.NightSurchargePct,
                    tariff.Currency,
                    EffectiveFrom = effectiveFrom,
                },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
        }

        if (windows is null)
        {
            return;
        }

        // Delete-then-insert, inside the caller's transaction: the windows are a *set*, not a
        // versioned card — migration 1001 gives them no effective_from and fare-svc reads all of
        // them — so "these are the windows now" is the only edit the shape supports.
        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM fares.peak_windows;",
            transaction: unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        foreach (var window in windows)
        {
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO fares.peak_windows (kind, start_local, end_local, multiplier_pct)
                VALUES (@Kind, @StartLocal, @EndLocal, @MultiplierPct::smallint);
                """,
                new { window.Kind, window.StartLocal, window.EndLocal, window.MultiplierPct },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<OperatingCity?> ReadCityAsync(
        IUnitOfWork unitOfWork, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<OperatingCity>(new CommandDefinition(
            $"SELECT {CityColumns} FROM config.operating_cities WHERE code = @Code FOR UPDATE;",
            new { Code = code },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<OperatingCity?> InsertCityAsync(
        IUnitOfWork unitOfWork, OperatingCity city, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(city);

        // DO NOTHING and a null answer rather than letting 23505 escape: the code is UNIQUE and a
        // duplicate is a 409 the operator can act on, not a 500.
        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<OperatingCity>(new CommandDefinition(
            $"""
             INSERT INTO config.operating_cities
               (code, name_en, name_si, name_ta, centroid_lat, centroid_lng, sort_order, is_active)
             VALUES
               (@Code, @NameEn, @NameSi, @NameTa, @CentroidLat, @CentroidLng, @SortOrder, @IsActive)
             ON CONFLICT (code) DO NOTHING
             RETURNING {CityColumns};
             """,
            city,
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<OperatingCity> UpdateCityAsync(
        IUnitOfWork unitOfWork, OperatingCity city, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(city);

        return await unitOfWork.Connection.QuerySingleAsync<OperatingCity>(new CommandDefinition(
            $"""
             UPDATE config.operating_cities
                SET name_en      = @NameEn,
                    name_si      = @NameSi,
                    name_ta      = @NameTa,
                    centroid_lat = @CentroidLat,
                    centroid_lng = @CentroidLng,
                    sort_order   = @SortOrder,
                    is_active    = @IsActive
              WHERE code = @Code
             RETURNING {CityColumns};
             """,
            city,
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FeatureFlag>> ListFlagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FeatureFlag>(new CommandDefinition(
            $"SELECT {FlagColumns} FROM config.feature_flags ORDER BY key;",
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FeatureFlag?> ReadFlagAsync(
        IUnitOfWork unitOfWork, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<FeatureFlag>(new CommandDefinition(
            $"SELECT {FlagColumns} FROM config.feature_flags WHERE key = @Key FOR UPDATE;",
            new { Key = key },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// An upsert, because a flag's first appearance and its next change are the same operator
    /// action: a PUT that 404'd on a key the deploy has started reading would leave the operator
    /// unable to turn on the thing the release shipped.
    /// <para>
    /// <c>description</c> is <c>COALESCE</c>d so a caller who only flips the switch does not erase
    /// the sentence explaining what it does.
    /// </para>
    /// </remarks>
    public async Task<FeatureFlag> SetFlagAsync(
        IUnitOfWork unitOfWork,
        string key,
        bool enabled,
        string? description,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleAsync<FeatureFlag>(new CommandDefinition(
            $"""
             INSERT INTO config.feature_flags (key, enabled, description, updated_by)
             VALUES (@Key, @Enabled, @Description, @ActorId)
             ON CONFLICT (key) DO UPDATE
               SET enabled     = EXCLUDED.enabled,
                   description = COALESCE(EXCLUDED.description, config.feature_flags.description),
                   updated_by  = EXCLUDED.updated_by
             RETURNING {FlagColumns};
             """,
            new { Key = key, Enabled = enabled, Description = description, ActorId = actorId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}
