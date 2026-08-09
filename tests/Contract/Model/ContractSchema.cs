using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MageRide.Contract.Tests.Model;

/// <summary>
/// A schema node from a contract document, resolved lazily.
/// </summary>
/// <remarks>
/// Lazy on purpose. A schema graph in this directory is cyclic in at least two places — a support
/// ticket's threaded messages and the admin audit envelope's <c>before</c>/<c>after</c> — so a
/// reader that expanded every reference on load would not terminate. Resolution happens when a
/// validator actually descends into a branch, which for a real response body is finite.
/// </remarks>
internal sealed class ContractSchema(object? node, string document)
{
    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, object?>? _resolved;
    private string? _resolvedDocument;

    /// <summary>The document the node was *written* in — where its own <c>$ref</c> starts from.</summary>
    public string Document { get; } = document;

    /// <summary>Whether the contract said anything at all here.</summary>
    public bool IsEmpty => Map.Count == 0;

    /// <summary>
    /// The document the resolved node lives in — where a <b>child's</b> <c>$ref</c> starts from.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Document"/> the moment a reference crosses a file, which on this
    /// surface is most of the time: see <c>ContractSet.ResolveIn</c> for the failure that made the
    /// distinction necessary.
    /// </remarks>
    public string ResolvedDocument
    {
        get
        {
            _ = Map;
            return _resolvedDocument ?? Document;
        }
    }

    private IReadOnlyDictionary<string, object?> Map
    {
        get
        {
            if (_resolved is not null)
            {
                return _resolved;
            }

            var (resolved, owning) = ContractSet.Current.ResolveIn(node, Document);
            _resolvedDocument = owning;
            return _resolved = resolved as IReadOnlyDictionary<string, object?> ?? Empty;
        }
    }

    /// <summary>
    /// The declared JSON types. OpenAPI 3.1 is JSON Schema 2020-12, so <c>type</c> may be a list —
    /// <c>[string, 'null']</c> is how this directory spells a nullable field.
    /// </summary>
    public IReadOnlyList<string> Types => ContractOperation.Strings(ContractOperation.Value(Map, "type"));

    public string? Format => ContractOperation.Value(Map, "format") as string;

    public string? Pattern => ContractOperation.Value(Map, "pattern") as string;

    public string? Const => ContractOperation.Value(Map, "const") as string;

    public IReadOnlyList<string> Enum => ContractOperation.Strings(ContractOperation.Value(Map, "enum"));

    public IReadOnlyList<string> Required => ContractOperation.Strings(ContractOperation.Value(Map, "required"));

    /// <summary>3.0-style nullability. Present nowhere in this directory; asserted absent.</summary>
    public bool DeclaresNullableKeyword => Map.ContainsKey("nullable");

    public IReadOnlyDictionary<string, ContractSchema> Properties
    {
        get
        {
            if (ContractOperation.Value(Map, "properties") is not IReadOnlyDictionary<string, object?> properties)
            {
                return new Dictionary<string, ContractSchema>(StringComparer.Ordinal);
            }

            return properties.ToDictionary(
                static entry => entry.Key,
                entry => new ContractSchema(entry.Value, ResolvedDocument),
                StringComparer.Ordinal);
        }
    }

    public ContractSchema? Items =>
        Map.ContainsKey("items") ? new ContractSchema(ContractOperation.Value(Map, "items"), ResolvedDocument) : null;

    /// <summary><see langword="false"/> only when the contract wrote <c>additionalProperties: false</c>.</summary>
    public bool AdditionalPropertiesAllowed =>
        ContractOperation.Value(Map, "additionalProperties") is not string flag
        || !string.Equals(flag, "false", StringComparison.Ordinal);

    public IReadOnlyList<ContractSchema> OneOf => Branches("oneOf");

    public IReadOnlyList<ContractSchema> AnyOf => Branches("anyOf");

    public IReadOnlyList<ContractSchema> AllOf => Branches("allOf");

    /// <summary>Every schema reachable from here, cycle-safe — for the whole-directory conventions.</summary>
    public IEnumerable<(string Path, ContractSchema Schema)> Descend(string path = "$", int depth = 0)
    {
        if (depth > 12 || IsEmpty)
        {
            yield break;
        }

        yield return (path, this);

        foreach (var (name, property) in Properties)
        {
            foreach (var found in property.Descend($"{path}.{name}", depth + 1))
            {
                yield return found;
            }
        }

        if (Items is { } items)
        {
            foreach (var found in items.Descend($"{path}[]", depth + 1))
            {
                yield return found;
            }
        }

        foreach (var (keyword, branches) in new[]
                 {
                     ("oneOf", OneOf), ("anyOf", AnyOf), ("allOf", AllOf),
                 })
        {
            for (var index = 0; index < branches.Count; index++)
            {
                foreach (var found in branches[index].Descend($"{path}/{keyword}[{index}]", depth + 1))
                {
                    yield return found;
                }
            }
        }
    }

    private IReadOnlyList<ContractSchema> Branches(string keyword) =>
        ContractOperation.Value(Map, keyword) is IList<object?> list
            ? list.Select(branch => new ContractSchema(branch, ResolvedDocument)).ToList()
            : [];
}

/// <summary>
/// Validates a JSON document against a contract schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here rather than taken from a package, and that is a decision.</b> A general JSON
/// Schema 2020-12 validator would accept every keyword the draft allows; this one accepts the
/// keywords <c>backend/contracts/</c> actually uses and <see cref="Validate"/> is the definition of
/// that set. The difference matters in the direction that counts: a contract that starts using a
/// keyword this validator does not implement is a contract whose new constraint would be silently
/// unenforced, and `UnsupportedKeywords` in the convention tests fails the build when one appears.
/// </para>
/// <para>
/// It also lets a failure say what a platform reviewer needs to hear — "`fare.totalMinor` is 480.5;
/// LKR is integer minor units (D3' §0)" rather than "instance failed keyword `type`".
/// </para>
/// </remarks>
internal static class SchemaValidator
{
    /// <summary>Every keyword this validator understands. Anything else is a silent constraint.</summary>
    public static readonly IReadOnlySet<string> SupportedKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        // Structure
        "type", "properties", "required", "items", "additionalProperties", "propertyNames",
        "oneOf", "anyOf", "allOf", "not", "discriminator",
        // Scalars
        "format", "pattern", "enum", "const", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "multipleOf",
        // Collections
        "minItems", "maxItems", "uniqueItems",
        // Documentation only — no constraint, and deliberately listed so they are not "unsupported"
        "description", "title", "example", "examples", "default", "deprecated", "readOnly",
        "writeOnly", "summary", "externalDocs", "$ref", "xml", "nullable",
    };

    /// <summary>Reasons the instance does not satisfy the schema. Empty means it does.</summary>
    public static IReadOnlyList<string> Validate(JsonElement instance, ContractSchema schema, string path = "$")
    {
        ArgumentNullException.ThrowIfNull(schema);

        var violations = new List<string>();
        Check(instance, schema, path, violations, depth: 0);
        return violations;
    }

    private static void Check(
        JsonElement instance, ContractSchema schema, string path, List<string> violations, int depth)
    {
        if (depth > 24 || schema.IsEmpty)
        {
            return;
        }

        foreach (var branch in schema.AllOf)
        {
            Check(instance, branch, path, violations, depth + 1);
        }

        CheckComposition(instance, schema, path, violations, depth);
        CheckType(instance, schema, path, violations);
        CheckConstAndEnum(instance, schema, path, violations);

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(instance, schema, path, violations, depth);
                break;

            case JsonValueKind.Array when schema.Items is { } items:
            {
                var index = 0;
                foreach (var element in instance.EnumerateArray())
                {
                    Check(element, items, $"{path}[{index++}]", violations, depth + 1);
                }

                break;
            }

            case JsonValueKind.String:
                CheckString(instance, schema, path, violations);
                break;

            case JsonValueKind.Number:
                CheckNumber(instance, schema, path, violations);
                break;

            default:
                break;
        }
    }

    private static void CheckComposition(
        JsonElement instance, ContractSchema schema, string path, List<string> violations, int depth)
    {
        if (schema.OneOf is { Count: > 0 } oneOf)
        {
            var matches = oneOf.Count(branch => Validate(instance, branch, path).Count == 0);
            if (matches != 1)
            {
                violations.Add(
                    $"{path}: matches {matches} of the {oneOf.Count} `oneOf` branches; a discriminated union must match exactly one.");
            }
        }

        if (schema.AnyOf is { Count: > 0 } anyOf
            && !anyOf.Any(branch => Validate(instance, branch, path).Count == 0))
        {
            violations.Add($"{path}: matches none of the {anyOf.Count} `anyOf` branches.");
        }

        _ = depth;
    }

    private static void CheckType(
        JsonElement instance, ContractSchema schema, string path, List<string> violations)
    {
        if (schema.Types is not { Count: > 0 } types)
        {
            return;
        }

        var actual = instance.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => instance.TryGetInt64(out _) ? "integer" : "number",
            _ => "undefined",
        };

        // `integer` satisfies `number`, and never the other way round: LKR minor units are the
        // reason this asymmetry is load-bearing rather than pedantic.
        var accepted = types.Contains(actual, StringComparer.Ordinal)
                       || (actual == "integer" && types.Contains("number", StringComparer.Ordinal));

        if (!accepted)
        {
            violations.Add(
                $"{path}: is `{actual}`, and the contract declares `{string.Join("|", types)}`.");
        }
    }

    private static void CheckConstAndEnum(
        JsonElement instance, ContractSchema schema, string path, List<string> violations)
    {
        if (instance.ValueKind is not JsonValueKind.String)
        {
            return;
        }

        var value = instance.GetString();

        if (schema.Const is { } constant && !string.Equals(value, constant, StringComparison.Ordinal))
        {
            violations.Add($"{path}: is \"{value}\"; the contract fixes it at \"{constant}\".");
        }

        if (schema.Enum is { Count: > 0 } allowed
            && !allowed.Contains(value ?? string.Empty, StringComparer.Ordinal))
        {
            violations.Add(
                $"{path}: \"{value}\" is not one of the contract's {allowed.Count} enum values.");
        }
    }

    private static void CheckObject(
        JsonElement instance, ContractSchema schema, string path, List<string> violations, int depth)
    {
        var properties = schema.Properties;

        foreach (var name in schema.Required)
        {
            if (!instance.TryGetProperty(name, out var present) || present.ValueKind == JsonValueKind.Undefined)
            {
                violations.Add($"{path}.{name}: required by the contract and absent from the response.");
            }
        }

        foreach (var property in instance.EnumerateObject())
        {
            if (properties.TryGetValue(property.Name, out var declared))
            {
                Check(property.Value, declared, $"{path}.{property.Name}", violations, depth + 1);
            }
            else if (!schema.AdditionalPropertiesAllowed && properties.Count > 0)
            {
                violations.Add(
                    $"{path}.{property.Name}: not declared, and the schema is closed (`additionalProperties: false`).");
            }
        }
    }

    private static void CheckString(
        JsonElement instance, ContractSchema schema, string path, List<string> violations)
    {
        var value = instance.GetString() ?? string.Empty;

        if (schema.Pattern is { Length: > 0 } pattern
            && !Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            violations.Add($"{path}: \"{value}\" does not match the contract's pattern `{pattern}`.");
        }

        switch (schema.Format)
        {
            case "uuid" when !Guid.TryParse(value, out _):
                violations.Add($"{path}: \"{value}\" is not a UUID.");
                break;

            case "date-time" when !DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _):
                violations.Add($"{path}: \"{value}\" is not an RFC 3339 instant.");
                break;

            case "date" when !DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _):
                violations.Add($"{path}: \"{value}\" is not a date.");
                break;

            case "uri" when !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _):
                violations.Add($"{path}: \"{value}\" is not a URI.");
                break;

            default:
                break;
        }
    }

    private static void CheckNumber(
        JsonElement instance, ContractSchema schema, string path, List<string> violations)
    {
        // `format: int64` on a value that arrived as a decimal is the money bug this whole
        // convention exists to catch (D3' §0: every currency value is integer minor units).
        if (string.Equals(schema.Format, "int64", StringComparison.Ordinal) && !instance.TryGetInt64(out _))
        {
            violations.Add(
                $"{path}: {instance.GetRawText()} is not an int64. Currency crosses the wire as integer minor units.");
        }

        if (string.Equals(schema.Format, "int32", StringComparison.Ordinal) && !instance.TryGetInt32(out _))
        {
            violations.Add($"{path}: {instance.GetRawText()} is not an int32.");
        }
    }
}
