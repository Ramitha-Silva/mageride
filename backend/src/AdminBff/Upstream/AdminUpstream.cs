using System.Net;
using System.Text.Json;
using MageRide.AdminBff.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MageRide.AdminBff.Upstream;

/// <summary>The upstreams, named so a client asks for one by identity rather than by URL.</summary>
public static class AdminUpstreams
{
    public const string Safety = "safety-svc";
    public const string Support = "support-svc";
    public const string Content = "content-svc";
    public const string Transit = "transit-svc";

    /// <summary>AL-30's onboarding recompute (C063).</summary>
    public const string Registry = "registry-svc";

    /// <summary>The fleet-org queue and the AL-49/AL-50 decisions (C063).</summary>
    public const string Fleet = "fleet-svc";

    /// <summary>
    /// The ledger seam US-14.11's fee reversal posts through (C046, C065).
    /// </summary>
    /// <remarks>
    /// wallet-svc is the <b>only writer of <c>billing.journal_postings</c></b> and its own file lists
    /// admin-bff by name as the caller entitled to post <c>kind='adjustment'</c> on
    /// <c>POST /v1/internal/wallet/{driverId}/credit</c>. Posting the compensating entry here
    /// instead would give the ledger a second writer for the one movement that is hardest to get
    /// right — the balanced pair, the <c>billing.wallets</c> mirror, the history line, the D-08
    /// cache write-through and the outbox row all happen inside <c>LedgerService.PostAsync</c>.
    /// </remarks>
    public const string Wallet = "wallet-svc";

    /// <summary>
    /// E-05's refund execution — <c>POST /v1/admin/fare/refund</c> (C050, C065).
    /// </summary>
    /// <remarks>
    /// fare-svc owns <c>fares.refunds</c> and the gateway round-trip behind it, and its route is
    /// role-gated on the caller's own bearer rather than sitting on an <c>/v1/internal/**</c> plane
    /// — so this is one of the two upstreams that gets the operator's token forwarded, like
    /// content-svc and transit-svc. In the deployed topology the gateway sends
    /// <c>/v1/admin/fare/**</c> straight to fare-svc at Order 20; what admin-bff adds is the Finance
    /// queue the decision is made from and the D-35 row recording that a human made it.
    /// </remarks>
    public const string Fare = "fare-svc";

    public static readonly IReadOnlyList<string> All =
        [Safety, Support, Content, Transit, Registry, Fleet, Wallet, Fare];
}

/// <summary>
/// Forwards one operator command to the service that owns the rows it changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a BFF forwards rather than writes.</b> CLAUDE.md's outbox rule — "no direct HTTP calls
/// between services for state changes" — is about a *service* reacting to another service's state.
/// This is a back-office front door relaying a human's command, and the two components that built
/// the seams say so in their own files: safety-svc's <c>InternalSafetyEndpoints</c> ("admin-bff is
/// the RBAC-gated, audited front door and the decision itself is made here") and support-svc's
/// <c>InternalSupportEndpoints</c> ("the same split C052 uses"). Writing those rows from here
/// instead would give <c>safety.vehicle_reports</c> two writers and would put US-12.6's
/// three-confirmations-delist rule in two places.
/// </para>
/// <para>
/// <b>Two credentials, because there are two kinds of callee.</b> An <c>/v1/internal/**</c> plane
/// takes the shared key (<c>X-MageRide-Internal-Key</c>, C008) and is told who the human was in the
/// body — it has no bearer to check. content-svc and transit-svc expose ordinary
/// <c>/v1/admin/**</c> routes gated on the nine roles, so the caller's own bearer is forwarded and
/// they re-check it. Forwarding a bearer to an internal plane would be pointless; sending the
/// shared key to a role-gated route would be a bypass.
/// </para>
/// <para>
/// <b>An unconfigured upstream is a 503, not a missing route.</b> Every route stays in the table so
/// the RBAC matrix test and the D-35 start-up guard enumerate it; the failure surfaces where an
/// operator can read it.
/// </para>
/// </remarks>
public interface IAdminUpstream
{
    /// <summary>Whether <paramref name="upstream"/> has somewhere to send a request.</summary>
    bool IsConfigured(string upstream);

    /// <summary>
    /// Sends <paramref name="request"/> and answers the deserialised body, translating the
    /// upstream's problem+json into the same <see cref="MageRideException"/> a local failure raises.
    /// </summary>
    Task<T> SendAsync<T>(
        string upstream, HttpRequestMessage request, HttpContext context, CancellationToken cancellationToken);

    /// <summary>Sends and answers the raw response, for the pass-through proxy.</summary>
    Task<HttpResponseMessage> SendRawAsync(
        string upstream, HttpRequestMessage request, HttpContext context, CancellationToken cancellationToken);

    /// <summary>Builds a request against <paramref name="upstream"/>'s base URL.</summary>
    HttpRequestMessage Request(string upstream, HttpMethod method, string pathAndQuery);
}

/// <inheritdoc cref="IAdminUpstream"/>
internal sealed class AdminUpstream(
    IHttpClientFactory clients, IOptions<AdminBffOptions> options, ILogger<AdminUpstream> logger) : IAdminUpstream
{
    /// <summary>The header every <c>/v1/internal/**</c> plane on the platform guards itself with (C008).</summary>
    public const string InternalKeyHeader = "X-MageRide-Internal-Key";

    private readonly AdminBffOptions.UpstreamOptions _upstreams =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Upstreams;

    public bool IsConfigured(string upstream) => !string.IsNullOrWhiteSpace(Settings(upstream).BaseUrl);

    public HttpRequestMessage Request(string upstream, HttpMethod method, string pathAndQuery)
    {
        var settings = Settings(upstream);

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw Unavailable(upstream);
        }

        return new HttpRequestMessage(method, new Uri(new Uri(settings.BaseUrl), pathAndQuery));
    }

    public async Task<T> SendAsync<T>(
        string upstream, HttpRequestMessage request, HttpContext context, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(upstream, request, context, cancellationToken).ConfigureAwait(false);

        await ThrowIfFailedAsync(upstream, response, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonSerializer.DeserializeAsync<T>(body, MageRideJson.Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new MageRideException(
                   MageRideErrors.DependencyUnavailable, $"{upstream} answered {(int)response.StatusCode} with no body.");
    }

    public async Task<HttpResponseMessage> SendRawAsync(
        string upstream, HttpRequestMessage request, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var settings = Settings(upstream);

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw Unavailable(upstream);
        }

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            request.Headers.TryAddWithoutValidation(InternalKeyHeader, settings.InternalApiKey);
        }
        else if (context.Request.Headers.Authorization.ToString() is { Length: > 0 } authorization)
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);
        }

        // Carried through so the callee's own logs and its command log record the same request the
        // operator made. Without it a forwarded POST would be a fresh command on every retry of a
        // request the kernel already deduplicated at this edge.
        if (context.Request.Headers.TryGetValue(MageRideHeaders.IdempotencyKey, out var key))
        {
            request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, key.ToString());
        }

        var client = clients.CreateClient(upstream);

        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "{Upstream} could not be reached for {Method} {Path}.",
                upstream, request.Method, request.RequestUri?.PathAndQuery);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                $"{upstream} could not be reached. The action was not performed.");
        }
    }

    /// <summary>
    /// Turns an upstream failure into this surface's failure, keeping the status.
    /// </summary>
    /// <remarks>
    /// <b>The status is preserved and the body is not.</b> A 404 from safety-svc means the report id
    /// does not exist and the operator should be told 404, not 502 — so the status crosses the
    /// boundary. The upstream's <c>type</c> URI and <c>traceId</c> do not: they name another
    /// service's error registry and another service's trace, and pasting them into this response
    /// would send an operator to a document that does not describe the endpoint they called.
    /// A 5xx becomes <c>dependency-unavailable</c>, which is what actually happened from here.
    /// </remarks>
    private static async Task ThrowIfFailedAsync(
        string upstream, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await DetailAsync(response, cancellationToken).ConfigureAwait(false);

        var error = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => MageRideErrors.ValidationFailed,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MageRideErrors.Forbidden,
            HttpStatusCode.NotFound => MageRideErrors.NotFound,
            HttpStatusCode.Conflict => MageRideErrors.Conflict,
            _ => MageRideErrors.DependencyUnavailable,
        };

        throw new MageRideException(
            error, detail is { Length: > 0 } ? detail : $"{upstream} answered {(int)response.StatusCode}.");
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var problem = JsonDocument.Parse(body);

            return problem.RootElement.TryGetProperty("detail", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            // Not problem+json. Nothing to quote, and inventing a message from an HTML error page
            // would be worse than the generic one the caller gets instead.
            return null;
        }
    }

    private static MageRideException Unavailable(string upstream) => new(
        MageRideErrors.DependencyUnavailable,
        $"{upstream} has no configured base URL on this deployment (AdminBff:Upstreams), so this action "
        + "cannot be performed here.");

    private AdminBffOptions.UpstreamService Settings(string upstream) => upstream switch
    {
        AdminUpstreams.Safety => _upstreams.Safety,
        AdminUpstreams.Support => _upstreams.Support,
        AdminUpstreams.Content => _upstreams.Content,
        AdminUpstreams.Transit => _upstreams.Transit,
        AdminUpstreams.Registry => _upstreams.Registry,
        AdminUpstreams.Fleet => _upstreams.Fleet,
        AdminUpstreams.Wallet => _upstreams.Wallet,
        AdminUpstreams.Fare => _upstreams.Fare,
        _ => throw new ArgumentOutOfRangeException(
            nameof(upstream), upstream, $"Not an admin-bff upstream. Known: {string.Join(", ", AdminUpstreams.All)}."),
    };
}
