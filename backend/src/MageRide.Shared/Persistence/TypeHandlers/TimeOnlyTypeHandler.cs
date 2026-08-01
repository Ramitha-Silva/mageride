using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// <c>TIME</c> ↔ <see cref="TimeOnly"/> — the recurring daily windows the platform keeps in
/// Asia/Colombo wall-clock rather than as instants.
/// </summary>
/// <remarks>
/// <para>
/// The columns are <c>fares.peak_windows.start_local</c> / <c>.end_local</c> (migration 1001): a
/// peak or night surcharge is "22:00 to 05:00, every day", not a moment, and D-38 keeps that
/// distinction because a <c>TIMESTAMPTZ</c> would be wrong twice a year in a jurisdiction with a
/// clock change and is wrong here for a simpler reason — it names a day nobody meant.
/// </para>
/// <para>
/// Dapper has no built-in mapping for <see cref="TimeOnly"/>, exactly as it has none for
/// <see cref="DateOnly"/>: without this a parameter fails at execution with "cannot be used as a
/// parameter value". Added by C062, which is the first component to write one.
/// </para>
/// </remarks>
public sealed class TimeOnlyTypeHandler : SqlMapper.TypeHandler<TimeOnly>
{
    public override TimeOnly Parse(object value) => value switch
    {
        TimeOnly time => time,
        TimeSpan span => TimeOnly.FromTimeSpan(span),
        DateTime dateTime => TimeOnly.FromDateTime(dateTime),
        string text => TimeOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new DataException($"Cannot convert {value?.GetType().Name ?? "null"} to {nameof(TimeOnly)}."),
    };

    public override void SetValue(IDbDataParameter parameter, TimeOnly value)
    {
        parameter.Value = value;

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Time;
        }
    }
}

/// <summary>Nullable companion — Dapper resolves handlers by exact type.</summary>
public sealed class NullableTimeOnlyTypeHandler : SqlMapper.TypeHandler<TimeOnly?>
{
    private static readonly TimeOnlyTypeHandler Inner = new();

    public override TimeOnly? Parse(object value) => value is null or DBNull ? null : Inner.Parse(value);

    public override void SetValue(IDbDataParameter parameter, TimeOnly? value)
    {
        if (value is null)
        {
            parameter.Value = DBNull.Value;

            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Time;
            }

            return;
        }

        Inner.SetValue(parameter, value.Value);
    }
}
