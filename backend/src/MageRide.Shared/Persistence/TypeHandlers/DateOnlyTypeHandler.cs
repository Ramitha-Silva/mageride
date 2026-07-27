using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// <c>DATE</c> ↔ <see cref="DateOnly"/> — the business-date columns ADD §9.1 stores alongside a
/// <c>tz_at</c> audit field (D-38): daily-fee idempotency, Directional Travel's <c>used_date</c>,
/// monthly subscription periods.
/// </summary>
/// <remarks>
/// Dapper has no built-in mapping for <see cref="DateOnly"/>, so without this a business-date
/// parameter fails at execution with "cannot be used as a parameter value".
/// </remarks>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to {nameof(DateOnly)}."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.Value = value;

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Date;
        }
    }
}

/// <summary>Nullable companion — Dapper resolves handlers by exact type.</summary>
public sealed class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    private static readonly DateOnlyTypeHandler Inner = new();

    public override DateOnly? Parse(object value) => value is null or DBNull ? null : Inner.Parse(value);

    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;

            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Date;
            }

            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}
