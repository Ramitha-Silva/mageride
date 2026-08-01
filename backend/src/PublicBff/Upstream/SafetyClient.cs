using System.Net.Http.Json;
using MageRide.PublicBff.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Upstream;

/// <summary>
/// What safety-svc recorded and whether a gateway took it.
/// </summary>
/// <remarks>
/// <b>There is no recipient on this type.</b> public-bff sends a token and two coordinates and is
/// told an id and an outcome; the booker's MSISDN is resolved inside safety-svc and returned to
/// nobody. A field for it here would be a field a response could one day carry.
/// </remarks>
public sealed record RaisedWebSos(Guid SosId, DateTimeOffset? DispatchedAt, string SmsStatus);

/// <summary>
/// safety-svc's <c>/v1/internal/safety/sos/web</c> seam (US-25.5, D-33).
/// </summary>
/// <remarks>
/// <para>
/// <b>The alert is recorded where the table is owned.</b> <c>safety.sos_events</c>, its
/// <c>sos.raised</c> outbox event and the dual-gateway dispatch are one transaction and one
/// five-second SLO in safety-svc; writing the row from here would give the platform's most
/// time-critical path two implementations and the admin console two sources for one alert. The C052
/// handoff left this seam named rather than stubbed and this is the caller it named.
/// </para>
/// <para>
/// <b>Nothing on this hop is best-effort.</b> A failure to reach safety-svc is a 503 the page shows,
/// because the alternative — accepting the tap, answering 202 and losing it — is the exact failure
/// safety-svc's own file calls out: an SOS that goes nowhere looks precisely like one that worked.
/// </para>
/// </remarks>
public interface ISafetyClient
{
    /// <summary>Whether <c>PublicBff:Safety:BaseUrl</c> is set at all.</summary>
    bool IsConfigured { get; }

    Task<RaisedWebSos> RaiseWebSosAsync(
        string shareToken, double lat, double lng, string idempotencyKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISafetyClient"/>
internal sealed class SafetyClient(
    IHttpClientFactory clients,
    IOptions<PublicBffOptions> options,
    ILogger<SafetyClient> logger) : ISafetyClient
{
    /// <summary>The named client the timeout is attached to.</summary>
    public const string HttpClientName = "safety-svc";

    /// <summary>Matches safety-svc's <c>InternalSafetyEndpoints.ApiKeyHeader</c>.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => _options.Safety.IsConfigured;

    public async Task<RaisedWebSos> RaiseWebSosAsync(
        string shareToken, double lat, double lng, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "PublicBff:Safety:BaseUrl is not configured, so no alert can be raised. "
                + "The button must not answer 202 for something nobody will see.");
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/safety/sos/web")
        {
            Content = JsonContent.Create(new { shareToken, lat, lng }, options: MageRideJson.Options),
        };

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.Safety.InternalApiKey ?? string.Empty);

        // R-14, and this is the route where it matters most: the first thing somebody does when
        // nothing appears to happen is press the button again. safety-svc's command log dedupes on
        // this key, so a double tap sends one message — which is why the key must not be a nonce
        // minted per call. See PublicIdempotency.
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(exception, "safety-svc did not answer a web SOS.");

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "The alert could not be raised. Call 119.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<RaisedWebSos>(MageRideJson.Options, cancellationToken)
                       ?? throw new MageRideException(
                           MageRideErrors.DependencyUnavailable, "The alert could not be confirmed. Call 119.");
            }

            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "safety-svc refused a web SOS: {Status} {Problem}", (int)response.StatusCode, problem);

            // safety-svc re-checks the token against the table it owns. If it refuses one this
            // service just admitted, the two disagree — and the caller is told the link is dead
            // rather than that the platform is, because that is the actionable half.
            throw new MageRideException(
                (int)response.StatusCode is 404 or 410
                    ? MageRideErrors.TokenExpiredOrRevoked
                    : MageRideErrors.DependencyUnavailable,
                "The alert could not be raised. Call 119.");
        }
    }
}
