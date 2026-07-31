using Dapper;
using MageRide.Fare.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Fare.Persistence;

/// <summary>
/// <c>fares.tariffs</c> and <c>fares.peak_windows</c> — the rate card, and when it is surcharged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A tariff is resolved at an instant, never "the current one".</b> Migration 1001 versions the
/// table by <c>effective_from</c> and says why: a completed ride must stay reconcilable against the
/// rate that priced it. So the estimate resolves at the moment of quoting and the settlement
/// resolves at the moment the ride was requested — a rate published mid-trip cannot re-price a
/// journey already under way.
/// </para>
/// <para>
/// <b>Read per request, not cached.</b> Two indexed reads against tables with single-digit row
/// counts, on a path that already crosses the network. A cache here would buy microseconds and cost
/// an invalidation protocol with admin-bff, and the failure it would introduce — quoting yesterday's
/// price after Finance published a new one — is the one thing the versioning exists to make
/// impossible.
/// </para>
/// </remarks>
internal interface ITariffRepository
{
    /// <summary>The tariff in force for a vehicle type at an instant, or <see langword="null"/>.</summary>
    Task<Tariff?> ResolveAsync(string vehicleType, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every vehicle type's tariff at an instant — the rate card a config screen renders.</summary>
    Task<IReadOnlyList<Tariff>> ListAsync(DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The peak and night windows, in Asia/Colombo wall-clock.</summary>
    Task<IReadOnlyList<PeakWindow>> ListWindowsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a new tariff version — the write behind admin-bff's
    /// <c>PUT /v1/admin/fares/tariffs</c> (US-14.4).
    /// </summary>
    /// <remarks>
    /// <b>Insert, never update.</b> Every row given shares one <paramref name="effectiveFrom"/>, so
    /// a rate card is published atomically and a ride quoted a millisecond earlier keeps its price.
    /// Windows are replaced wholesale inside the same transaction: they carry no
    /// <c>effective_from</c> of their own, so "the windows in force" is whatever the table holds,
    /// and a partial update would leave the day tiled by two generations of rows at once.
    /// </remarks>
    Task PublishAsync(
        DateTimeOffset effectiveFrom,
        IReadOnlyCollection<Tariff> tariffs,
        IReadOnlyCollection<PeakWindow>? windows,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITariffRepository"/>
internal sealed class TariffRepository(INpgsqlConnectionFactory connections, IUnitOfWorkFactory unitOfWorkFactory)
    : ITariffRepository
{
    /// <remarks>
    /// The money columns are <c>INTEGER</c> in §9 and the percentages <c>SMALLINT</c>, while the
    /// contract types money as int64 and a percentage as int32. Dapper's constructor binding matches
    /// parameter types <em>exactly</em>, so an un-cast column does not fail to convert — it fails to
    /// materialise the record at all, and the tariff comes back null on a path that would then
    /// answer "no rate configured" for a rate that is right there.
    /// </remarks>
    private const string TariffColumns =
        """
        id, vehicle_type, first_km_minor::bigint AS first_km_minor, per_km_minor::bigint AS per_km_minor,
        peak_surcharge_pct::int AS peak_surcharge_pct, night_surcharge_pct::int AS night_surcharge_pct,
        currency, effective_from
        """;

    private const string WindowColumns =
        "id, kind, start_local, end_local, multiplier_pct::int AS multiplier_pct";

    public async Task<Tariff?> ResolveAsync(
        string vehicleType, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // ix_tariffs_lookup is (vehicle_type, effective_from DESC), so this is one index seek and
        // one row — not a scan of the type's whole rate history.
        return await connection.QuerySingleOrDefaultAsync<Tariff>(new CommandDefinition(
            $"""
             SELECT {TariffColumns}
               FROM fares.tariffs
              WHERE vehicle_type = @VehicleType AND effective_from <= @At
              ORDER BY effective_from DESC
              LIMIT 1;
             """,
            new { VehicleType = vehicleType, At = at },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Tariff>> ListAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<Tariff>(new CommandDefinition(
            $"""
             SELECT {TariffColumns} FROM (
               SELECT DISTINCT ON (vehicle_type) *
                 FROM fares.tariffs
                WHERE effective_from <= @At
                ORDER BY vehicle_type, effective_from DESC) t
              ORDER BY vehicle_type;
             """,
            new { At = at },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<PeakWindow>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PeakWindow>(new CommandDefinition(
            $"SELECT {WindowColumns} FROM fares.peak_windows ORDER BY kind, start_local;",
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task PublishAsync(
        DateTimeOffset effectiveFrom,
        IReadOnlyCollection<Tariff> tariffs,
        IReadOnlyCollection<PeakWindow>? windows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tariffs);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        foreach (var tariff in tariffs)
        {
            // ON CONFLICT on (vehicle_type, effective_from): re-publishing the same version corrects
            // it rather than failing, which is what an admin fixing a typo one minute later means.
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO fares.tariffs
                  (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct,
                   night_surcharge_pct, currency, effective_from)
                VALUES
                  (@VehicleType, @FirstKmMinor::int, @PerKmMinor::int, @PeakSurchargePct::smallint,
                   @NightSurchargePct::smallint, @Currency, @EffectiveFrom)
                ON CONFLICT (vehicle_type, effective_from) DO UPDATE
                  SET first_km_minor = EXCLUDED.first_km_minor,
                      per_km_minor = EXCLUDED.per_km_minor,
                      peak_surcharge_pct = EXCLUDED.peak_surcharge_pct,
                      night_surcharge_pct = EXCLUDED.night_surcharge_pct,
                      currency = EXCLUDED.currency;
                """,
                new
                {
                    tariff.VehicleType,
                    tariff.FirstKmMinor,
                    tariff.PerKmMinor,
                    tariff.PeakSurchargePct,
                    tariff.NightSurchargePct,
                    Currency = string.IsNullOrWhiteSpace(tariff.Currency) ? FareFormula.Currency : tariff.Currency,
                    EffectiveFrom = effectiveFrom,
                },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
        }

        if (windows is not null)
        {
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM fares.peak_windows;", transaction: unitOfWork.Transaction,
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

        await unitOfWork.CommitAsync(cancellationToken);
    }
}
