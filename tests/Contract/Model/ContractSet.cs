using YamlDotNet.Serialization;

namespace MageRide.Contract.Tests.Model;

/// <summary>
/// Every OpenAPI document in <c>backend/contracts/</c>, loaded once and resolved across files.
///
/// <para>
/// C007's CLAUDE.md states the rule this whole suite exists to enforce: "If a service and a
/// contract disagree, the contract wins — fix the service, or file a micro-change-set against
/// `specs/D3_mageride_api_contracts.md` and update both." Everything here reads the documents and
/// nothing here reads a service, so a test written against this model cannot accidentally assert
/// what the code happens to do.
/// </para>
///
/// <para>
/// <b>References are resolved, not followed at assertion time.</b> Every one of the twenty-five
/// service documents <c>$ref</c>s into <c>_shared.yaml</c> — the Problem shape, the error registry,
/// the pagination envelope, the edge headers — so a test that only read one file would be reading
/// about a fifth of the contract. <see cref="Resolve"/> walks a chain of references in either
/// direction and is depth-bounded, because a self-referential schema (a support ticket's threaded
/// replies, for one) is a legal document and an infinite loop in a test runner is not.
/// </para>
/// </summary>
internal sealed class ContractSet
{
    /// <summary>The only file in the directory that is not an OpenAPI document.</summary>
    private const string RulesetFileName = ".spectral.yaml";

    /// <summary>A component library, deliberately declaring no paths (OpenAPI 3.1 permits it).</summary>
    public const string SharedDocument = "_shared";

    private static readonly Lazy<ContractSet> Instance = new(Load);

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> _documents;

    private ContractSet(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> documents)
    {
        _documents = documents;
        Operations = [];
    }

    /// <summary>Loaded once per test run: twenty-six YAML documents is not a per-test cost.</summary>
    public static ContractSet Current => Instance.Value;

    /// <summary>
    /// Every operation in every document, in file then declaration order.
    /// </summary>
    /// <remarks>
    /// Assigned once, immediately after construction, because reading an operation needs a set that
    /// can already resolve <c>$ref</c>s into <c>_shared.yaml</c> — and a constructor cannot hand
    /// itself to the reader it is calling.
    /// </remarks>
    public IReadOnlyList<ContractOperation> Operations { get; private set; }

    /// <summary>The document stems, `_shared` included.</summary>
    public IEnumerable<string> Documents => _documents.Keys;

    /// <summary>Absolute path of <c>backend/contracts</c>, found by walking up from the test output.</summary>
    public static string Directory { get; } = Locate();

    /// <summary>The service documents, `_shared` excluded — the twenty-five with paths.</summary>
    public IEnumerable<string> ServiceDocuments =>
        _documents.Keys.Where(static name => !name.StartsWith('_')).Order(StringComparer.Ordinal);

    /// <summary>A raw node out of one document, by <c>/</c>-separated pointer.</summary>
    public object? Node(string document, params string[] pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);

        if (!_documents.TryGetValue(document, out var root))
        {
            return null;
        }

        object? node = root;

        foreach (var segment in pointer)
        {
            node = Resolve(node, document) is IReadOnlyDictionary<string, object?> map
                && map.TryGetValue(segment, out var child)
                    ? child
                    : null;

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>
    /// Follows <c>$ref</c> until the node is something other than a reference.
    /// </summary>
    /// <param name="node">Any node from any document.</param>
    /// <param name="document">
    /// The document the node came from — a same-file <c>#/components/…</c> reference means nothing
    /// without it, and half the references in this directory are same-file.
    /// </param>
    public object? Resolve(object? node, string document) => ResolveIn(node, document).Node;

    /// <summary>
    /// <see cref="Resolve"/>, and <b>the document the answer came out of</b>.
    /// </summary>
    /// <remarks>
    /// This is the half a naive reader gets wrong, and it is silent when it does. A response in
    /// <c>iam.yaml</c> references <c>./_shared.yaml#/components/schemas/Money</c>; that schema's own
    /// <c>currency</c> property references <c>#/components/schemas/Currency</c> — a <b>same-file</b>
    /// pointer, and the file is now <c>_shared.yaml</c>. A reader that kept resolving in
    /// <c>iam.yaml</c> looks for a schema that is not there, finds nothing, and reports an empty
    /// schema — which every assertion then passes, because an empty schema constrains nothing.
    /// </remarks>
    public (object? Node, string Document) ResolveIn(object? node, string document)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (node is not IReadOnlyDictionary<string, object?> map
                || !map.TryGetValue("$ref", out var reference)
                || reference is not string pointer)
            {
                return (node, document);
            }

            var hash = pointer.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0)
            {
                return (null, document);
            }

            var file = pointer[..hash].Trim();
            var target = file.Length == 0
                ? document
                : Path.GetFileNameWithoutExtension(file);

            node = Node(target, pointer[(hash + 1)..].Trim('/').Split('/'));
            document = target;
        }

        throw new InvalidOperationException(
            "A `$ref` chain in backend/contracts/ is more than 32 links long, or is a cycle.");
    }

    /// <summary>The document a reference resolves *in*, for a follow-up resolution from that node.</summary>
    public static string DocumentOf(string pointer, string fallback)
    {
        ArgumentNullException.ThrowIfNull(pointer);

        var hash = pointer.IndexOf('#', StringComparison.Ordinal);
        if (hash <= 0)
        {
            return fallback;
        }

        return Path.GetFileNameWithoutExtension(pointer[..hash].Trim());
    }

    /* --------------------------------------------------------------------------------------- */

    private static ContractSet Load()
    {
        var deserializer = new DeserializerBuilder().Build();
        var documents = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var file in System.IO.Directory
                     .EnumerateFiles(Directory, "*.yaml")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) == RulesetFileName)
            {
                continue;
            }

            using var reader = new StreamReader(file);
            if (Normalise(deserializer.Deserialize<object?>(reader)) is IReadOnlyDictionary<string, object?> root)
            {
                documents[Path.GetFileNameWithoutExtension(file)] = root;
            }
        }

        var set = new ContractSet(documents);
        set.Operations = ContractOperation.ReadAll(set);
        return set;
    }

    /// <summary>
    /// YamlDotNet's untyped graph, with map keys as strings.
    /// </summary>
    /// <remarks>
    /// The default deserializer produces <c>Dictionary&lt;object, object&gt;</c> and every scalar as
    /// a <see cref="string"/>. Both are fine — a contract is read, never round-tripped — but the
    /// object keys make every lookup a cast, so they are normalised here once. Scalars stay strings
    /// deliberately: <c>type: integer</c>, <c>const: LKR</c> and <c>minItems: 1</c> are all read as
    /// text by the assertions that care, and a numeric conversion here would only introduce a
    /// culture-sensitive parse into the middle of a contract reader.
    /// </remarks>
    private static object? Normalise(object? node)
    {
        switch (node)
        {
            case IDictionary<object, object> map:
            {
                var normalised = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
                foreach (var (key, value) in map)
                {
                    normalised[key.ToString() ?? string.Empty] = Normalise(value);
                }

                return normalised;
            }

            case IList<object> list:
                return list.Select(Normalise).ToList();

            default:
                return node;
        }
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "contracts");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"backend/contracts was not found above {AppContext.BaseDirectory}.");
    }
}
