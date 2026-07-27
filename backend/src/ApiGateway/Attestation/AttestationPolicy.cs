using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// Decides whether a request is one of the D-30 sensitive operations.
/// <para>
/// Matching is method + route template rather than path prefix, because sensitivity is per
/// operation: <c>GET /v1/wallet/{userId}</c> is a balance read while
/// <c>POST /v1/wallet/topup/onepay</c> moves money, and a prefix rule would either attest both or
/// neither.
/// </para>
/// </summary>
internal sealed class AttestationPolicy
{
    private readonly IOptionsMonitor<AttestationOptions> _options;
    private CompiledPolicy _compiled;

    public AttestationPolicy(IOptionsMonitor<AttestationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _compiled = Compile(options.CurrentValue);
        options.OnChange(changed => _compiled = Compile(changed));
    }

    public AttestationOptions Current => _options.CurrentValue;

    /// <summary>The compiled operation set, for diagnostics and tests.</summary>
    public IReadOnlyList<SensitiveOperation> Operations => _compiled.Source;

    public bool IsSensitive(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsSensitive(request.Method, request.Path);
    }

    public bool IsSensitive(string method, PathString path)
    {
        var compiled = _compiled;
        var values = new RouteValueDictionary();

        foreach (var entry in compiled.Entries)
        {
            if (entry.Methods.Count > 0 && !entry.Methods.Contains(method))
            {
                continue;
            }

            values.Clear();
            if (entry.Matcher.TryMatch(path, values))
            {
                return true;
            }
        }

        return false;
    }

    private static CompiledPolicy Compile(AttestationOptions options)
    {
        var entries = new List<Entry>(options.SensitiveOperations.Count);

        foreach (var operation in options.SensitiveOperations)
        {
            if (string.IsNullOrWhiteSpace(operation.Path))
            {
                throw new InvalidOperationException(
                    "Gateway:Attestation:SensitiveOperations contains an entry with no Path.");
            }

            var template = TemplateParser.Parse(operation.Path);
            var methods = new HashSet<string>(
                operation.Methods.Select(static m => m.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);

            entries.Add(new Entry(methods, new TemplateMatcher(template, [])));
        }

        return new CompiledPolicy([.. options.SensitiveOperations], entries);
    }

    private sealed record CompiledPolicy(IReadOnlyList<SensitiveOperation> Source, List<Entry> Entries);

    private sealed record Entry(HashSet<string> Methods, TemplateMatcher Matcher);
}
