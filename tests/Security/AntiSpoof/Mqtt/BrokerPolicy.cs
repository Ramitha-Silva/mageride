using System.Text.RegularExpressions;

namespace MageRide.Security.Tests.AntiSpoof.Mqtt;

/// <summary>
/// The broker policy as <b>deployed</b> — <c>infra/deploy/emqx/emqx.conf</c> and
/// <c>acl.conf</c>, the two files the dev compose stack, the replica and
/// <c>MageRide.TestKit.EmqxFixture</c> all mount.
/// </summary>
/// <remarks>
/// <para>
/// Reading the files is not a substitute for connecting to a broker, and
/// <see cref="CrossVehiclePublishTests"/> does that. It answers a different question: a live test
/// proves the listener it dials, and a listener nobody dialled can lose its rate limit without a
/// single assertion moving. There are three of them and they do not share a settings block.
/// </para>
/// <para>
/// Parsed with regexes over HOCON rather than with a HOCON library, deliberately: the assertions
/// are all of the form "this exact line is present", the failure message quotes the line a reader
/// has to go and change, and a dependency on a config-language parser to assert a security control
/// is a dependency that can disagree with EMQX's own parser.
/// </para>
/// </remarks>
internal static partial class BrokerPolicy
{
    private static readonly Lazy<string> BrokerConf = new(() => Read("emqx.conf"));
    private static readonly Lazy<string> AclConf = new(() => Read("acl.conf"));

    /// <summary>The whole of <c>emqx.conf</c>, comments included.</summary>
    public static string Broker => BrokerConf.Value;

    /// <summary>The whole of <c>acl.conf</c>.</summary>
    public static string Acl => AclConf.Value;

    /// <summary>
    /// Every listener block in <c>emqx.conf</c>, with its body — <c>listeners.tcp.default</c>,
    /// <c>listeners.ssl.default</c>, <c>listeners.wss.default</c>, <c>listeners.ws.default</c>.
    /// </summary>
    /// <remarks>
    /// Only lines outside comments count. A <c>messages_rate</c> in the commented-out
    /// replica/production authenticator block would otherwise satisfy an assertion about a control
    /// nothing enforces — which is exactly the shape of the T-12 CRL finding this component
    /// recorded, and worth not repeating in the tooling that looks for it.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Listeners { get; } = ParseListeners(Uncommented(Broker));

    /// <summary>The configuration with every comment line removed.</summary>
    public static string ActiveBroker { get; } = Uncommented(BrokerConf.Value);

    /// <summary>The ACL rules with every comment line removed.</summary>
    public static string ActiveAcl { get; } = Uncommented(AclConf.Value);

    /// <summary>Whether an uncommented setting is present, ignoring whitespace around <c>=</c>.</summary>
    public static bool Declares(string body, string key, string value) =>
        new Regex($@"^\s*{Regex.Escape(key)}\s*=\s*""?{Regex.Escape(value)}""?\s*(#.*)?$",
                RegexOptions.Multiline, TimeSpan.FromSeconds(2))
            .IsMatch(body);

    private static string Uncommented(string source) => string.Join(
        '\n',
        source.Split('\n').Where(line =>
        {
            var trimmed = line.TrimStart();
            return !trimmed.StartsWith('#') && !trimmed.StartsWith("%%", StringComparison.Ordinal);
        }));

    private static IReadOnlyDictionary<string, string> ParseListeners(string body)
    {
        var listeners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in ListenerHeader().Matches(body))
        {
            var name = match.Groups["name"].Value;
            var open = match.Index + match.Length;
            var depth = 1;
            var index = open;

            while (index < body.Length && depth > 0)
            {
                depth += body[index] switch { '{' => 1, '}' => -1, _ => 0 };
                index++;
            }

            listeners[name] = body[open..Math.Max(open, index - 1)];
        }

        return listeners;
    }

    private static string Read(string file)
    {
        var path = Path.Combine(DeployedConfiguration.RepositoryRoot, "infra", "deploy", "emqx", file);

        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"The deployed broker policy is missing: {path}", path);
    }

    [GeneratedRegex(@"^\s*listeners\.(?<name>[a-z]+)\.default\s*\{", RegexOptions.Multiline, 2000)]
    private static partial Regex ListenerHeader();
}
