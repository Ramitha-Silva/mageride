using System.Text.Json;
using System.Text.Json.Serialization;

namespace MageRide.Shared.Http;

/// <summary>
/// Serialises a string-keyed dictionary with its keys <b>exactly as they are</b>.
/// </summary>
/// <typeparam name="TValue">The value type. Read and written with the ambient options.</typeparam>
/// <remarks>
/// <para>
/// <see cref="MageRideJson"/> sets <c>DictionaryKeyPolicy = CamelCase</c>, which is right for a
/// dictionary whose keys are property-like and wrong for one whose keys are <em>data</em>. The
/// platform has several of the latter and they all fail the same way, silently and once:
/// </para>
/// <list type="bullet">
///   <item>the US-10.7 notification switches — <c>LOW_BALANCE</c> answered as <c>loW_BALANCE</c>,
///     so a client that sends back what it was given holds a key matching no notification type;</item>
///   <item>the P-12 location-request outcome tally — <c>Declined</c> answered as <c>declined</c>,
///     so an admin screen filtering on the value <c>safety.location_request_audit.decision</c>
///     actually holds finds nothing.</item>
/// </list>
/// <para>
/// <b>Promoted into the kernel by C052</b>, out of iam-svc's C027 original — whose own remarks
/// predicted the second and third instances — following the same rule C024 used for
/// <c>KafkaTopicConsumer</c>: the third copy is where a pattern becomes shared code. iam-svc keeps
/// its non-generic version because that one is also applied to a <c>jsonb</c> column and is C027's
/// to change.
/// </para>
/// </remarks>
public sealed class LiteralKeyDictionaryConverter<TValue> : JsonConverter<IReadOnlyDictionary<string, TValue>>
{
    public override IReadOnlyDictionary<string, TValue> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new Dictionary<string, TValue>(StringComparer.Ordinal);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object of {typeof(TValue).Name} values.");
        }

        var values = new Dictionary<string, TValue>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return values;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property name.");
            }

            var key = reader.GetString() ?? throw new JsonException("A dictionary key cannot be null.");

            reader.Read();

            values[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
        }

        throw new JsonException("Unterminated object.");
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyDictionary<string, TValue> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        foreach (var (key, item) in value)
        {
            // WritePropertyName takes the name verbatim; the naming policy is not consulted, which
            // is the whole reason this converter exists.
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndObject();
    }
}
