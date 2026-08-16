using System.Net.Security;

namespace MageRide.Contract.Tests.Live;

/// <summary>
/// The second transport this suite's `Runtime/` sweep was always meant to gain (C118's CLAUDE.md,
/// "when the Dockerfiles land (C124/C125)"): a real HTTP client pointed at a DEPLOYED MageRide edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a deployment can catch what composition cannot.</b> `Runtime/RouteTableTests` reads each
/// service's own route table, which is exact and cannot flake — but a route table says nothing about
/// TLS termination, the gateway's cluster addresses, the version gate, the rate limiter, or whether
/// the bearer a caller presents can actually be validated. C126 found the last of those the hard
/// way: `Jwt__JwksUrl` named a path the gateway refuses ahead of routing, so **every authenticated
/// request to either compose stack answered 500** while every in-process suite stayed green. The
/// JWKS fetch happens on the first request that carries a token, and nothing here had ever sent one.
/// </para>
/// <para>
/// <b>Opt-in, and silent otherwise.</b> With no <c>MAGERIDE_LIVE_EDGE</c> the live theories skip, so
/// `dotnet test tests/Contract` and CI behave exactly as before — this transport needs a running
/// replica, which no build agent has. `infra/replica/contract-live-verify.sh` is what sets the three
/// variables and runs it.
/// </para>
/// <para>
/// <b>Bash owns the credential; this owns the assertions.</b> The bearer arrives ready-made in
/// <c>MAGERIDE_LIVE_TOKEN</c> rather than being minted here, because signing in means reading
/// `.env.replica` and knowing which of nine roles the account holds — deployment facts that belong
/// to the deployment's own scripts, not to a contract suite.
/// </para>
/// </remarks>
internal static class LiveEdge
{
    /// <summary>e.g. <c>https://127.0.0.1:443</c> — HAProxy, the surface a phone talks to.</summary>
    public static string? BaseUrl => Env("MAGERIDE_LIVE_EDGE");

    /// <summary>The vhost HAProxy routes on. Without it the edge cannot pick a backend.</summary>
    public static string HostHeader => Env("MAGERIDE_LIVE_HOST") ?? "replica.mageride.lk";

    /// <summary>An Admin bearer, from `contract-live-verify.sh`.</summary>
    public static string? Token => Env("MAGERIDE_LIVE_TOKEN");

    public static bool Configured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// Skips the calling test unless an edge is configured, naming how to configure one.
    /// </summary>
    public static void RequireEdge()
    {
        if (!Configured)
        {
            Assert.Skip(
                "No deployed edge to drive. This transport asserts against a RUNNING replica: "
                + "bash infra/replica/contract-live-verify.sh");
        }
    }

    /// <summary>
    /// One client for the whole run, keep-alive included, because 382 operations over TLS with a
    /// fresh handshake each would spend most of the suite in the handshake.
    /// </summary>
    /// <remarks>
    /// <b>The certificate is not validated, and that is not laziness.</b> The replica's edge is
    /// self-signed by design (C125's smoke suite says the same in the same words): trusting it on
    /// the build host would make the suite pass for a reason no phone enjoys, and refusing it would
    /// mean the suite could only ever run against production.
    /// </remarks>
    public static readonly Lazy<HttpClient> Client = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            // The gateway answers 3xx on two routes (the GTFS zip download's signed-URL redirect is
            // one). Following them would leave the assertion talking about the target rather than
            // the operation, and the signed URL deliberately carries no bearer.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl!, UriKind.Absolute),
            // Generous: a cold service behind the gateway may be doing its first database round
            // trip. Still bounded, so a hung route fails rather than hanging the suite.
            Timeout = TimeSpan.FromSeconds(30),
        };
    });

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
