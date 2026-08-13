using System.Text.Json;
using System.Text.RegularExpressions;

namespace MageRide.Security.Tests.Rbac;

/// <summary>
/// The C008 edge's own route table and blocked prefixes, read from the files it ships with, so a
/// claim about what the public internet can address is checked against the thing that decides it.
///
/// <para>
/// <b>Order matters, and it matters in one direction only here.</b> The gateway picks the
/// lowest-<c>Order</c> matching route; this class only asks whether <em>any</em> route matches,
/// because for an exposure question a second matching route is not a mitigation. The blocked
/// prefixes are applied first, exactly as <c>BlockedPathMiddleware</c> applies them — ahead of
/// routing, so a <c>/v1/**</c> catch-all cannot reach past them.
/// </para>
/// </summary>
internal static partial class GatewayRouteTable
{
    private static readonly Lazy<Compiled> Table = new(Load);

    /// <summary>The prefixes the edge refuses ahead of routing (<c>Gateway:BlockedPathPrefixes</c>).</summary>
    public static IReadOnlyList<string> BlockedPrefixes => Table.Value.Blocked;

    /// <summary>Every YARP route path pattern the edge serves.</summary>
    public static IReadOnlyList<string> Patterns => Table.Value.Patterns;

    /// <summary>
    /// Whether a caller on the public internet can address this route.
    /// </summary>
    /// <param name="route">
    /// <c>VERB /template</c> with parameter names erased, as <see cref="GuardedEndpoint.Route"/>
    /// spells it. The verb is ignored: every route in <c>gateway-routes.json</c> is verb-agnostic,
    /// and a path that is routed for one method is routed for all of them.
    /// </param>
    public static bool RoutesFromTheInternet(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        var path = route.Contains(' ', StringComparison.Ordinal) ? route.Split(' ', 2)[1] : route;

        foreach (var prefix in BlockedPrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return Table.Value.Matchers.Exists(matcher => matcher.IsMatch(path));
    }

    private static Compiled Load()
    {
        var gateway = Locate();

        using var routes = JsonDocument.Parse(File.ReadAllText(Path.Combine(gateway, "gateway-routes.json")));
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(gateway, "gateway-policy.json")));

        var patterns = new List<string>();

        if (routes.RootElement.TryGetProperty("ReverseProxy", out var proxy)
            && proxy.TryGetProperty("Routes", out var table))
        {
            foreach (var route in table.EnumerateObject())
            {
                if (route.Value.TryGetProperty("Match", out var match)
                    && match.TryGetProperty("Path", out var path)
                    && path.GetString() is { Length: > 0 } value)
                {
                    patterns.Add(value);
                }
            }
        }

        var blocked = new List<string>();

        if (settings.RootElement.TryGetProperty("Gateway", out var section)
            && section.TryGetProperty("BlockedPathPrefixes", out var prefixes))
        {
            blocked.AddRange(prefixes.EnumerateArray()
                .Select(static prefix => prefix.GetString())
                .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(static prefix => prefix!.TrimEnd('/')));
        }

        if (patterns.Count == 0)
        {
            throw new InvalidOperationException(
                $"No routes were read out of {gateway}/gateway-routes.json. Every exposure assertion in this "
                + "suite would pass vacuously.");
        }

        return new Compiled(blocked, patterns, [.. patterns.Select(ToMatcher)]);
    }

    /// <summary>
    /// A YARP path pattern as a regex over normalised templates.
    /// </summary>
    /// <remarks>
    /// Two placeholder forms and they mean different things: <c>{**catchall}</c> swallows the rest
    /// of the path including slashes, an ordinary <c>{param}</c> is one segment. The inventory's
    /// own parameters are already erased to <c>{}</c>, which is one segment too, so a literal
    /// <c>{}</c> in the input matches a <c>{param}</c> in the pattern and nothing else.
    /// </remarks>
    private static Regex ToMatcher(string pattern)
    {
        var expression = "^";

        foreach (var segment in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("{**", StringComparison.Ordinal))
            {
                // `/v1/fare/{**remainder}` must also match `/v1/fare` itself — YARP treats the
                // catch-all as optional, and an exposure test that missed the bare prefix would
                // report a routed path as unroutable.
                expression += "(/.*)?";
                return new Regex(expression + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }

            expression += "/" + (Placeholder().IsMatch(segment) ? "[^/]+" : Regex.Escape(segment));
        }

        return new Regex(expression + "/?$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src", "ApiGateway");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"backend/src/ApiGateway was not found above {AppContext.BaseDirectory}.");
    }

    private sealed record Compiled(IReadOnlyList<string> Blocked, IReadOnlyList<string> Patterns, List<Regex> Matchers);

    [GeneratedRegex(@"^\{[^}]*\}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}
