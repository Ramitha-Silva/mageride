using System.Net;
using System.Net.Http.Json;
using MageRide.PublicBff.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Upstream;

/// <summary>The state the location request ended in.</summary>
public sealed record LocationRequestResolution(Guid RequestId, string State);

/// <summary>
/// ride-svc's <c>/v1/internal/location-requests/**</c> seam — AL-45's web path into the P-02 state
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>public-bff validates the token; ride-svc moves the row.</b> R-01 makes ride-svc the sole
/// writer of its own aggregates, and AL-45 routes the web flow through "the same
/// <c>rides.location_requests</c> state machine" the in-app confirm uses — so this is a command on
/// ride-svc's internal plane and not an <c>UPDATE</c> from here. ride-svc built the pair for this
/// caller and says so at its own declaration ("public-bff validates and burns the
/// <c>pickup_confirm</c> token safety-svc minted, and then has to move a row in
/// <c>rides.location_requests</c>, which is this service's").
/// </para>
/// <para>
/// <b>The decline carries no body, and nothing here could add one.</b> <see cref="DeclineAsync"/>
/// takes no coordinate parameter, sends no content, and the route it calls has no <c>resolved_geo</c>
/// in its <c>SET</c> list. P-02's "declining never sends your GPS" is three properties of three
/// components rather than one reviewer's care.
/// </para>
/// </remarks>
public interface IRideClient
{
    /// <summary>Whether <c>PublicBff:Ride:BaseUrl</c> is set at all.</summary>
    bool IsConfigured { get; }

    Task<LocationRequestResolution> ConfirmAsync(
        Guid requestId, double lat, double lng, double? accuracy, string idempotencyKey,
        CancellationToken cancellationToken);

    Task<LocationRequestResolution> DeclineAsync(
        Guid requestId, string idempotencyKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideClient"/>
internal sealed class RideClient(
    IHttpClientFactory clients,
    IOptions<PublicBffOptions> options,
    ILogger<RideClient> logger) : IRideClient
{
    /// <summary>The named client the timeout is attached to.</summary>
    public const string HttpClientName = "ride-svc";

    /// <summary>Matches ride-svc's <c>InternalRideEndpoints.ApiKeyHeader</c>.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => _options.Ride.IsConfigured;

    public Task<LocationRequestResolution> ConfirmAsync(
        Guid requestId, double lat, double lng, double? accuracy, string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync(requestId, "confirm", new { lat, lng, accuracy }, idempotencyKey, cancellationToken);

    public Task<LocationRequestResolution> DeclineAsync(
        Guid requestId, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(requestId, "decline", body: null, idempotencyKey, cancellationToken);

    private async Task<LocationRequestResolution> SendAsync(
        Guid requestId, string verb, object? body, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            // The route stays mapped and answers 503. A route that disappeared when a setting was
            // absent is a route no fence test enumerates, and SCR-WT-003's Decline would look to
            // the rider like a link that never existed.
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "PublicBff:Ride:BaseUrl is not configured, so a pickup answer cannot reach ride-svc.");
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/internal/location-requests/{requestId}/{verb}");

        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.Ride.InternalApiKey ?? string.Empty);

        // ride-svc's plane is behind the kernel's idempotency middleware (R-14). The key is the
        // browser's when it sent one and derived from the token otherwise, so a retried tap replays
        // rather than being refused as an answer to an already-answered request.
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, idempotencyKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: MageRideJson.Options);
        }

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(exception, "ride-svc did not answer the {Verb} of location request {RequestId}.", verb, requestId);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "The booker could not be reached just now. Try again.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var resolved = await response.Content.ReadFromJsonAsync<LocationRequestResolution>(
                    MageRideJson.Options, cancellationToken);

                return resolved ?? new LocationRequestResolution(requestId, "Unknown");
            }

            // **ride-svc's refusal is repeated, not translated.** A request that has already been
            // answered, or whose 300 s ran out between the token check and this call, is a 409 or a
            // 400 from the component that owns the clock — and SCR-WT-003 has a state for each. A
            // blanket 500 here would tell the rider the platform is broken when what happened is
            // that they were too late.
            var problem = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogWarning(
                "ride-svc refused the {Verb} of location request {RequestId}: {Status} {Problem}",
                verb, requestId, (int)response.StatusCode, problem);

            throw new MageRideException(
                response.StatusCode is HttpStatusCode.Conflict
                    ? MageRideErrors.Conflict
                    : MageRideErrors.IllegalTransition,
                "This pickup request can no longer be answered.");
        }
    }
}
