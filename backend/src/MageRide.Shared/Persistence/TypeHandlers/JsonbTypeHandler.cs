using System.Data;
using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Shared.Persistence.TypeHandlers;

/// <summary>
/// Serialises <typeparamref name="T"/> into a Postgres <c>jsonb</c> column with the platform's
/// storage options, so a document written by one service reads back identically in another.
/// </summary>
/// <remarks>
/// Register per concrete type: <c>SqlMapper.AddTypeHandler(new JsonbTypeHandler&lt;MyPayload&gt;())</c>.
/// Do not use it for values that must survive round-tripping unchanged — <c>jsonb</c> normalises
/// whitespace and reorders object keys.
/// </remarks>
public sealed class JsonbTypeHandler<T>(JsonSerializerOptions? options = null) : SqlMapper.TypeHandler<T?>
{
    private readonly JsonSerializerOptions _options = options ?? MageRideJson.StorageOptions;

    public override T? Parse(object value) => value switch
    {
        null or DBNull => default,
        string json => JsonSerializer.Deserialize<T>(json, _options),
        _ => throw new DataException($"Cannot deserialise {value.GetType().Name} as jsonb."),
    };

    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value, _options);

        if (parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }
    }
}
