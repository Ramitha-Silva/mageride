using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace MageRide.ApiGateway.Tests.Infrastructure;

/// <summary>One operation from <c>backend/contracts/*.yaml</c>.</summary>
/// <param name="Service">Contract file stem, e.g. <c>ride</c>.</param>
/// <param name="Cluster">The YARP cluster that must serve it, derived from <paramref name="Service"/>.</param>
/// <param name="Template">Path as the contract spells it, with <c>{param}</c> placeholders.</param>
/// <param name="RequiresAttestation">The operation declares the D-30 <c>X-Attestation</c> parameter.</param>
internal sealed record ContractOperation(
    string Service,
    string Cluster,
    string Template,
    string Method,
    string OperationId,
    bool RequiresAttestation)
{
    /// <summary>A concrete path the gateway can be asked to route.</summary>
    public string ConcretePath => ContractCatalog.TemplateParameter().Replace(Template, "01JZZZZZZZZZZZZZZZZZZZZZZZ");
}

/// <summary>
/// Reads the OpenAPI contracts C007 produced. The gateway's route table and its attestation policy
/// are both asserted against this rather than against a copy — a contract that grows an endpoint or
/// marks one sensitive fails the gateway build until the edge is updated to match.
/// </summary>
internal static partial class ContractCatalog
{
    /// <summary>
    /// Contract file stem to YARP cluster. Every file is <c>{stem}-svc</c> except the two BFFs,
    /// which are named without the suffix throughout D3'/D7', and <c>version-check</c>, which the
    /// gateway serves itself and therefore has no cluster.
    /// </summary>
    private static readonly Dictionary<string, string?> ClusterByService = new(StringComparer.Ordinal)
    {
        ["admin-bff"] = "admin-bff",
        ["public-bff"] = "public-bff",
        ["version-check"] = null,
    };

    /// <summary>Contracts whose paths the gateway must not forward (mTLS-only, D3' §0).</summary>
    public const string InternalPrefix = "/v1/internal/";

    private static readonly Lazy<IReadOnlyList<ContractOperation>> Cache = new(Load);

    public static IReadOnlyList<ContractOperation> Operations => Cache.Value;

    /// <summary>Absolute path of <c>backend/contracts</c>, found by walking up from the test output.</summary>
    public static string ContractsDirectory { get; } = Locate();

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "contracts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"backend/contracts was not found above {AppContext.BaseDirectory}.");
    }

    private static IReadOnlyList<ContractOperation> Load()
    {
        var deserializer = new DeserializerBuilder().Build();
        var operations = new List<ContractOperation>();

        foreach (var file in Directory.EnumerateFiles(ContractsDirectory, "*.yaml").OrderBy(static f => f, StringComparer.Ordinal))
        {
            var service = Path.GetFileNameWithoutExtension(file);
            if (service.StartsWith('_'))
            {
                // _shared.yaml is a component library; OpenAPI 3.1 lets it declare no paths.
                continue;
            }

            var cluster = ClusterByService.TryGetValue(service, out var mapped) ? mapped : service + "-svc";

            using var reader = new StreamReader(file);
            if (deserializer.Deserialize<Dictionary<string, object>>(reader) is not { } document
                || !document.TryGetValue("paths", out var pathsNode)
                || pathsNode is not Dictionary<object, object> paths)
            {
                continue;
            }

            foreach (var (pathKey, pathValue) in paths)
            {
                if (pathValue is not Dictionary<object, object> pathItem)
                {
                    continue;
                }

                foreach (var (methodKey, methodValue) in pathItem)
                {
                    var method = methodKey.ToString()!;
                    if (!IsHttpMethod(method) || methodValue is not Dictionary<object, object> operation)
                    {
                        continue;
                    }

                    operations.Add(new ContractOperation(
                        service,
                        cluster!,
                        pathKey.ToString()!,
                        method.ToUpperInvariant(),
                        operation.TryGetValue("operationId", out var id) ? id.ToString()! : "(none)",
                        DeclaresAttestation(operation)));
                }
            }
        }

        return operations;
    }

    private static bool IsHttpMethod(string value) =>
        value is "get" or "post" or "put" or "patch" or "delete" or "head" or "options";

    private static bool DeclaresAttestation(Dictionary<object, object> operation) =>
        operation.TryGetValue("parameters", out var node)
        && node is List<object> parameters
        && parameters.OfType<Dictionary<object, object>>().Any(static parameter =>
            parameter.TryGetValue("$ref", out var reference)
            && reference.ToString()!.EndsWith("XAttestation", StringComparison.Ordinal));

    [GeneratedRegex(@"\{[^}]+\}")]
    internal static partial Regex TemplateParameter();
}
