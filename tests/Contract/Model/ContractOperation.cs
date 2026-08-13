using System.Globalization;

namespace MageRide.Contract.Tests.Model;

/// <summary>Where a parameter sits, as OpenAPI spells it.</summary>
internal enum ParameterLocation
{
    Path,
    Query,
    Header,
    Cookie,
}

/// <summary>One resolved parameter — <c>$ref</c>s into <c>_shared.yaml</c> already followed.</summary>
internal sealed record ContractParameter(
    string Name,
    ParameterLocation In,
    bool Required,
    object? SchemaNode,
    string SchemaDocument)
{
    public ContractSchema Schema => new(SchemaNode, SchemaDocument);
}

/// <summary>One declared response: a status and the media types it is served as.</summary>
/// <param name="Status">
/// The literal key. Almost always three digits; <c>default</c> and <c>4XX</c> are legal OpenAPI and
/// are kept verbatim rather than expanded, so a document that starts using them is visible.
/// </param>
internal sealed record ContractResponse(
    string Status,
    IReadOnlyDictionary<string, ContractSchema> Content)
{
    public bool Matches(int status) =>
        int.TryParse(Status, NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
            ? declared == status
            : string.Equals(Status, "default", StringComparison.Ordinal)
              || (Status.Length == 3
                  && Status[0] == (char)('0' + (status / 100))
                  && Status.AsSpan(1).Equals("XX", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One operation from one contract document, with everything a conformance assertion needs already
/// resolved.
/// </summary>
/// <param name="Document">Contract file stem — <c>ride</c>, <c>admin-bff</c>, …</param>
/// <param name="Template">The path as the contract spells it, <c>{param}</c> placeholders intact.</param>
/// <param name="Security">
/// The scheme names the operation admits. <b>Empty means deliberately public</b> and is a decision
/// the contract had to write down — `.spectral.yaml` requires an explicit `security` on every
/// operation, because "deny-by-default is not something a contract may leave to a default".
/// </param>
internal sealed record ContractOperation(
    string Document,
    string Method,
    string Template,
    string OperationId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Security,
    IReadOnlyList<ContractParameter> Parameters,
    IReadOnlyList<ContractResponse> Responses,
    IReadOnlyList<string> ErrorCodes,
    bool RequestBodyRequired,
    ContractSchema? RequestBody,
    string? IdempotencyExemptReason)
{
    /// <summary>`{Document} {METHOD} {template}` — what a failure message names.</summary>
    public override string ToString() => $"{Document} {Method} {Template}";

    /// <summary>Whether any credential at all is admitted (as opposed to a public operation).</summary>
    public bool IsPublic => Security.Count == 0;

    /// <summary>Whether the operation is on the mTLS-only internal plane (D3' §0).</summary>
    /// <remarks>
    /// <b>Δ C127: two signals, because neither alone is complete.</b> The <c>/v1/internal</c> prefix
    /// is the convention and covers forty-six of the forty-nine mTLS operations; the declaration
    /// <c>security: [{ mtls: [] }]</c> is what the contract actually says, and three operations
    /// carry it without the prefix — <c>calculateFinalFare</c>, <c>renderNotificationTemplate</c>
    /// and <c>lookupUserByPhone</c>. Reading only the prefix is how all three came to be published
    /// at the public edge; reading only the declaration would drop the twelve prefixed operations
    /// that write no <c>security</c> block. `Gateway:BlockedPathPrefixes` names the union.
    /// </remarks>
    public bool IsInternal =>
        Template.StartsWith("/v1/internal/", StringComparison.Ordinal)
        || (Security.Count > 0 && Security.All(static scheme => string.Equals(scheme, "mtls", StringComparison.Ordinal)));

    /// <summary>The declared response for a status, or <see langword="null"/> if it is undocumented.</summary>
    public ContractResponse? ResponseFor(int status) =>
        Responses.FirstOrDefault(response => response.Matches(status));

    /// <summary>Every operation in every service document, in file then declaration order.</summary>
    public static IReadOnlyList<ContractOperation> ReadAll(ContractSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var operations = new List<ContractOperation>();

        foreach (var document in set.ServiceDocuments)
        {
            if (set.Node(document, "paths") is not IReadOnlyDictionary<string, object?> paths)
            {
                continue;
            }

            foreach (var (template, pathNode) in paths)
            {
                if (set.Resolve(pathNode, document) is not IReadOnlyDictionary<string, object?> pathItem)
                {
                    continue;
                }

                // Parameters declared on the path item apply to every operation under it.
                var shared = ReadParameters(set, document, pathItem);

                foreach (var (method, operationNode) in pathItem)
                {
                    if (!IsHttpMethod(method)
                        || set.Resolve(operationNode, document) is not IReadOnlyDictionary<string, object?> operation)
                    {
                        continue;
                    }

                    operations.Add(Read(set, document, template, method, operation, shared));
                }
            }
        }

        return operations;
    }

    /* --------------------------------------------------------------------------------------- */

    private static ContractOperation Read(
        ContractSet set,
        string document,
        string template,
        string method,
        IReadOnlyDictionary<string, object?> operation,
        IReadOnlyList<ContractParameter> inherited)
    {
        var parameters = inherited.Concat(ReadParameters(set, document, operation)).ToList();

        var body = set.Resolve(Value(operation, "requestBody"), document) as IReadOnlyDictionary<string, object?>;
        var bodySchema = body is null ? null : FirstJsonSchema(set, document, body);

        return new ContractOperation(
            Document: document,
            Method: method.ToUpperInvariant(),
            Template: template,
            OperationId: Value(operation, "operationId") as string ?? "(none)",
            Tags: Strings(Value(operation, "tags")),
            Security: ReadSecurity(operation),
            Parameters: parameters,
            Responses: ReadResponses(set, document, operation),
            ErrorCodes: Strings(Value(operation, "x-error-codes")),
            RequestBodyRequired: string.Equals(
                body is null ? null : Value(body, "required") as string, "true", StringComparison.Ordinal),
            RequestBody: bodySchema,
            IdempotencyExemptReason: Value(operation, "x-idempotency-exempt") as string);
    }

    /// <summary>
    /// The scheme names an operation admits.
    /// </summary>
    /// <remarks>
    /// OpenAPI's <c>security</c> is a list of *requirement objects*, each a map of scheme → scopes,
    /// and the list is an OR. Every name across the list is collected: this suite asks "which
    /// credentials does this route recognise", not "which combination", and no MageRide operation
    /// declares a requirement object with two schemes in it.
    /// <para>
    /// A <b>missing</b> <c>security</c> and an <b>empty</b> one mean opposite things in OpenAPI — the
    /// first inherits the document default, the second overrides it to none — but the lint refuses a
    /// missing one, so an empty list here always means "deliberately public". `SecurityIsExplicit`
    /// in the convention tests is what keeps that reading true.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ReadSecurity(IReadOnlyDictionary<string, object?> operation)
    {
        if (Value(operation, "security") is not IList<object?> requirements)
        {
            return [];
        }

        return requirements
            .OfType<IReadOnlyDictionary<string, object?>>()
            .SelectMany(static requirement => requirement.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<ContractParameter> ReadParameters(
        ContractSet set, string document, IReadOnlyDictionary<string, object?> owner)
    {
        if (Value(owner, "parameters") is not IList<object?> declared)
        {
            return [];
        }

        var parameters = new List<ContractParameter>(declared.Count);

        foreach (var node in declared)
        {
            // A parameter `$ref` almost always points into `_shared.yaml`, and the schema *inside*
            // it is then a same-file reference — so the document the schema resolves in is the one
            // the parameter came from, not the one that referenced it.
            var owning = node is IReadOnlyDictionary<string, object?> map
                         && map.TryGetValue("$ref", out var pointer)
                         && pointer is string reference
                ? ContractSet.DocumentOf(reference, document)
                : document;

            if (set.Resolve(node, document) is not IReadOnlyDictionary<string, object?> parameter)
            {
                continue;
            }

            parameters.Add(new ContractParameter(
                Name: Value(parameter, "name") as string ?? string.Empty,
                In: (Value(parameter, "in") as string) switch
                {
                    "query" => ParameterLocation.Query,
                    "header" => ParameterLocation.Header,
                    "cookie" => ParameterLocation.Cookie,
                    _ => ParameterLocation.Path,
                },
                Required: string.Equals(Value(parameter, "required") as string, "true", StringComparison.Ordinal),
                SchemaNode: Value(parameter, "schema"),
                SchemaDocument: owning));
        }

        return parameters;
    }

    private static IReadOnlyList<ContractResponse> ReadResponses(
        ContractSet set, string document, IReadOnlyDictionary<string, object?> operation)
    {
        if (Value(operation, "responses") is not IReadOnlyDictionary<string, object?> responses)
        {
            return [];
        }

        var declared = new List<ContractResponse>(responses.Count);

        foreach (var (status, node) in responses)
        {
            var owning = node is IReadOnlyDictionary<string, object?> map
                         && map.TryGetValue("$ref", out var pointer)
                         && pointer is string reference
                ? ContractSet.DocumentOf(reference, document)
                : document;

            if (set.Resolve(node, document) is not IReadOnlyDictionary<string, object?> response)
            {
                continue;
            }

            var content = new Dictionary<string, ContractSchema>(StringComparer.OrdinalIgnoreCase);

            if (Value(response, "content") is IReadOnlyDictionary<string, object?> media)
            {
                foreach (var (type, entry) in media)
                {
                    if (set.Resolve(entry, owning) is IReadOnlyDictionary<string, object?> body)
                    {
                        content[type] = new ContractSchema(Value(body, "schema"), owning);
                    }
                }
            }

            declared.Add(new ContractResponse(status, content));
        }

        return declared;
    }

    /// <summary>The JSON body schema of a request, ignoring multipart and octet-stream siblings.</summary>
    private static ContractSchema? FirstJsonSchema(
        ContractSet set, string document, IReadOnlyDictionary<string, object?> body)
    {
        if (Value(body, "content") is not IReadOnlyDictionary<string, object?> media)
        {
            return null;
        }

        foreach (var (type, entry) in media)
        {
            if (!type.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (set.Resolve(entry, document) is IReadOnlyDictionary<string, object?> content)
            {
                return new ContractSchema(Value(content, "schema"), document);
            }
        }

        return null;
    }

    internal static object? Value(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;

    internal static IReadOnlyList<string> Strings(object? node) => node switch
    {
        IList<object?> list => list.OfType<string>().ToList(),
        string single => [single],
        _ => [],
    };

    private static bool IsHttpMethod(string value) =>
        value is "get" or "post" or "put" or "patch" or "delete" or "head" or "options";
}
