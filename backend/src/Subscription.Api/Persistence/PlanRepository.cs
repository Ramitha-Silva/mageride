using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Subscriptions.Persistence;

/// <summary>One row of <c>billing.plans</c> — a vehicle type's daily platform fee.</summary>
/// <param name="Mode">
/// <c>A</c>, <c>B</c> or <c>C</c>. Mode A is free by AL-09 and the seed rates it zero; the column is
/// what makes "bus and train pay nothing" visible on the rates screen rather than implied by a zero.
/// </param>
public sealed record FeePlan(string VehicleType, long DailyFeeMinor, string Mode, string Currency, DateTimeOffset UpdatedAt);

/// <summary>A rate as an admin submits it.</summary>
public sealed record FeePlanInput(string VehicleType, long DailyFeeMinor, string Mode);

/// <summary><c>billing.plans</c> — the seven-tier ladder, admin-configurable (US-14.4).</summary>
internal interface IPlanRepository
{
    Task<IReadOnlyList<FeePlan>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The rate for one vehicle type, or <see langword="null"/> when Finance has configured none.</summary>
    Task<FeePlan?> ForVehicleTypeAsync(string vehicleType, CancellationToken cancellationToken);

    /// <summary>Upserts the submitted rates and returns the whole ladder afterwards.</summary>
    Task<IReadOnlyList<FeePlan>> UpsertAsync(
        IReadOnlyList<FeePlanInput> plans, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlanRepository"/>
internal sealed class PlanRepository(INpgsqlConnectionFactory connections) : IPlanRepository
{
    /// <remarks>
    /// <c>daily_fee_minor</c> is an <c>INTEGER</c> in §10 and a <c>long</c> here, because CLAUDE.md's
    /// "money as minor units" is int64 across every contract and every other money column on the
    /// platform is <c>BIGINT</c>. The cast is not cosmetic: Dapper's constructor binding matches
    /// parameter types <em>exactly</em>, so an <c>Int32</c> column against an <c>Int64</c> parameter
    /// fails to materialise the record at all.
    /// </remarks>
    private const string SelectColumns =
        "vehicle_type, daily_fee_minor::bigint AS daily_fee_minor, mode, currency, updated_at";

    // Cheapest first inside each mode, so the ladder reads the way URD §Daily Platform Fee Structure
    // prints it (Free, then 50 … 300) rather than alphabetically, which would open with 'bus, flex'.
    private const string Order = "ORDER BY mode, daily_fee_minor, vehicle_type";

    public async Task<IReadOnlyList<FeePlan>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FeePlan>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM billing.plans {Order};",
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<FeePlan?> ForVehicleTypeAsync(string vehicleType, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FeePlan>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM billing.plans WHERE vehicle_type = @VehicleType;",
                new { VehicleType = vehicleType },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>An upsert, and deliberately not a replace.</b> A <c>PUT</c> that deleted the rows it was not
    /// sent would let a portal screen that renders six of the eight tiers silently un-configure the other
    /// two — and an un-configured type cannot go online at all (C005's rule for <c>truck</c>). Every
    /// submitted rate is applied; every unmentioned one is left exactly as it was.
    /// </para>
    /// <para>
    /// <b>Nothing here touches <c>billing.daily_fee_charges</c>.</b> That is the whole of "a rate change
    /// applies from the next charge without retro-billing": a charge row records the amount that was
    /// actually taken, the charge path reads <c>billing.plans</c> at the moment it charges, and there is
    /// no code path anywhere in this service that revisits a row already written.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FeePlan>> UpsertAsync(
        IReadOnlyList<FeePlanInput> plans, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plans);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // One statement per row inside one transaction: the ladder is eight rows on its longest day,
        // and an admin who submits a partially invalid set gets none of it rather than half.
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.plans (vehicle_type, daily_fee_minor, mode)
                VALUES (@VehicleType, @DailyFeeMinor, @Mode)
                ON CONFLICT (vehicle_type) DO UPDATE
                   SET daily_fee_minor = EXCLUDED.daily_fee_minor,
                       mode = EXCLUDED.mode;
                """,
                plans,
                transaction,
                cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<FeePlan>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM billing.plans {Order};",
                transaction: transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return [.. rows];
    }
}
