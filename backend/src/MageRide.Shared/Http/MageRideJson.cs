using System.Text.Json;
using System.Text.Json.Serialization;

namespace MageRide.Shared.Http;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> every service serialises with — camelCase
/// System.Text.Json per D3' §0.
/// </summary>
/// <remarks>
/// The idempotent-replay guarantee (R-14) depends on this being stable: a replayed response is
/// the bytes captured on the first call, so the two only agree if both were produced by the same
/// options instance. Changing anything here changes the wire format for every service at once.
/// </remarks>
public static class MageRideJson
{
    /// <summary>Options for request/response bodies. Immutable once first used.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>
    /// Options for values stored as Postgres <c>jsonb</c> (outbox payloads, command-log bodies).
    /// Identical to <see cref="Options"/> — kept separate so a future wire-format change cannot
    /// silently rewrite what is already at rest.
    /// </summary>
    public static readonly JsonSerializerOptions StorageOptions = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
        };

        // Enums cross the wire as their camelCase name (D3' payloads use "cash", "three_wheeler",
        // "Accepted"); each service declares [JsonStringEnumMemberName] where the spec spells a
        // value differently from the C# member.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        // populateMissingResolver installs the reflection-based resolver. Without it, freezing
        // the instance throws: a Web-defaults JsonSerializerOptions has no resolver until first
        // use. The services are not trimmed or AOT-compiled, so reflection is the right resolver.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
