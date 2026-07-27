using System.Data;
using Dapper;
using MageRide.Shared.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// Maps <see cref="Money"/> to a single <c>BIGINT</c> minor-unit column — the shape D4' uses for
/// every money column (<c>fare_minor</c>, <c>balance_minor</c>, <c>amount_minor</c>, …).
/// </summary>
/// <remarks>
/// The currency is not stored per row; the platform is LKR-only (D3' §0) and the columns are named
/// <c>*_minor</c> with no companion currency column. The handler therefore refuses to write
/// anything but <see cref="Money.Lkr"/> rather than silently drop the code — the day a second
/// currency appears, this throws instead of corrupting the ledger.
/// </remarks>
public sealed class MoneyTypeHandler : SqlMapper.TypeHandler<Money>
{
    public override Money Parse(object value) => value switch
    {
        long minor => Money.FromMinor(minor),
        int minor => Money.FromMinor(minor),
        short minor => Money.FromMinor(minor),
        decimal minor => Money.FromMinor(checked((long)minor)),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to {nameof(Money)}; expected a minor-unit integer column."),
    };

    public override void SetValue(IDbDataParameter parameter, Money value)
    {
        if (!string.Equals(value.Currency, Money.Lkr, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Money columns store {Money.Lkr} minor units only; got {value.Currency}. Add an explicit currency column before storing another currency.");
        }

        parameter.Value = value.AmountMinor;

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Bigint;
        }
    }
}

/// <summary>Nullable companion — Dapper resolves handlers by exact type.</summary>
public sealed class NullableMoneyTypeHandler : SqlMapper.TypeHandler<Money?>
{
    private static readonly MoneyTypeHandler Inner = new();

    public override Money? Parse(object value) => value is null or DBNull ? null : Inner.Parse(value);

    public override void SetValue(IDbDataParameter parameter, Money? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;

            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Bigint;
            }

            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}
