using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// <c>TIMESTAMPTZ</c> ↔ <see cref="DateTimeOffset"/>. Every temporal column in the platform is
/// <c>TIMESTAMPTZ</c> (D-38), and every API timestamp is an ISO-8601 UTC
/// <see cref="DateTimeOffset"/> (D3' §0) — including the Timescale hypertable's
/// <c>sample_ts</c>/<c>received_ts</c> (ADD §9.5).
/// </summary>
/// <remarks>
/// Npgsql 6+ refuses to write a <see cref="DateTimeOffset"/> with a non-zero offset to
/// <c>timestamptz</c>. Rather than surface that as a runtime error deep in a repository, the
/// handler converts to UTC on the way in — the instant is preserved, and the offset was never
/// stored by Postgres anyway.
/// </remarks>
public sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime(),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to {nameof(DateTimeOffset)}."),
    };

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.Value = value.ToUniversalTime();

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
        }
    }
}

/// <summary>Nullable companion — Dapper resolves handlers by exact type.</summary>
public sealed class NullableDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset?>
{
    private static readonly DateTimeOffsetTypeHandler Inner = new();

    public override DateTimeOffset? Parse(object value) => value is null or DBNull ? null : Inner.Parse(value);

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;

            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
            }

            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}
