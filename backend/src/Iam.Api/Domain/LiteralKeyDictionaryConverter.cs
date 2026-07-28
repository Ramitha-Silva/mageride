using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Shared.Http;

namespace MageRide.Iam.Domain;

/// <summary>
/// The per-type notification switches of US-10.7, stored in <c>iam.users.notif_prefs</c>.
/// </summary>
public static class NotificationPreferences
{
    /// <summary>Empty — the column's <c>DEFAULT '{}'</c>.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Empty =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>
    /// <c>MageRideJson.StorageOptions</c> plus <see cref="LiteralKeyDictionaryConverter"/>. The
    /// column is read and written through this and nothing else, so a key survives a round trip.
    /// </summary>
    public static readonly JsonSerializerOptions Json = Build();

    /// <summary>Reads the column. A malformed document reads as "no preferences", never as a 500.</summary>
    /// <remarks>
    /// The column is <c>NOT NULL DEFAULT '{}'</c> and only this service writes it, so this is a
    /// belt to the braces — but the failure it guards is a user unable to open their own profile.
    /// </remarks>
    public static IReadOnlyDictionary<string, bool> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyDictionary<string, bool>>(json, Json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Write(IReadOnlyDictionary<string, bool> preferences) =>
        JsonSerializer.Serialize(preferences, Json);

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(MageRideJson.StorageOptions);
        options.Converters.Add(new LiteralKeyDictionaryConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// Serialises a string-keyed dictionary with its keys <b>exactly as they are</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>MageRideJson</c> sets <c>DictionaryKeyPolicy = CamelCase</c>, which is right for a
/// dictionary whose keys are property-like and wrong for one whose keys are <em>data</em>. The
/// notification switches of US-10.7 are <c>SCHEDULED_REMINDER</c>, <c>LOW_BALANCE</c>,
/// <c>SOS_TRIGGERED</c> — template keys owned by <c>content.notification_templates</c> — and the
/// policy would rewrite the first one as <c>sCHEDULED_REMINDER</c> on the way out while reading
/// it back verbatim on the way in. That corrupts a key once, silently, and the mute the user set
/// stops matching the notification it was for.
/// </para>
/// <para>
/// Applied to both the <c>jsonb</c> at rest and the wire shape, so the two cannot disagree.
/// <b>notification-svc's <c>PUT /v1/notify/preferences</c> (C061) writes the same column and will
/// need the same treatment</b> — noted in the C027 handoff.
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
                throw new JsonException("Expected a notification type name.");
            }

            var key = reader.GetString()!;

            if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
            {
                throw new JsonException($"'{key}' must be true or false.");
            }

            values[key] = reader.TokenType == JsonTokenType.True;
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
            writer.WriteBoolean(key, enabled);
        }

        writer.WriteEndObject();
    }
}
