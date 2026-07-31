using System.Text.Json;
using System.Text.Json.Serialization;

namespace MageRide.Notification.Domain;

/// <summary>
/// Serialises a string-keyed dictionary with its keys <b>exactly as they are</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MageRideJson</c> sets <c>DictionaryKeyPolicy = CamelCase</c>, which is right for a dictionary
/// whose keys are property-like and wrong for one whose keys are <em>data</em>. The notification
/// switches of US-10.7 are <c>SCHEDULED_REMINDER</c>, <c>LOW_BALANCE</c>, <c>SOS_TRIGGERED</c> —
/// values owned by <see cref="NotificationCatalogue"/> — and the policy rewrites the first as
/// <c>sCHEDULED_REMINDER</c> on the way out while reading it back verbatim on the way in. The user
/// sets a mute, the response says something else, and the next client to send back what it was given
/// has a key that matches no notification.
/// </para>
/// <para>
/// This is iam-svc's <c>LiteralKeyDictionaryConverter</c> (C027), whose own remarks predicted this
/// service needing the same treatment — "notification-svc's <c>PUT /v1/notify/preferences</c> writes
/// the same column and will need the same treatment". Applied to both directions of the wire shape
/// here; the column is written by <c>Preferences.Write</c>, which builds the document by hand for
/// the same reason.
/// </para>
/// </remarks>
public sealed class LiteralKeyDictionaryConverter : JsonConverter<IReadOnlyDictionary<string, bool>>
{
    public override IReadOnlyDictionary<string, bool> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object of notification-type switches.");
        }

        var values = new Dictionary<string, bool>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return values;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a notification type as a property name.");
            }

            var key = reader.GetString() ?? throw new JsonException("A notification type cannot be null.");

            reader.Read();

            values[key] = reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                _ => throw new JsonException($"The switch for '{key}' must be true or false."),
            };
        }

        throw new JsonException("Unterminated notification-preference object.");
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyDictionary<string, bool> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        foreach (var (key, enabled) in value)
        {
            // WritePropertyName takes the name verbatim; the naming policy is not consulted, which
            // is the whole reason this converter exists.
            writer.WritePropertyName(key);
            writer.WriteBooleanValue(enabled);
        }

        writer.WriteEndObject();
    }
}
