using System.Net.Http.Headers;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http;

namespace MageRide.Fleet.Subscriptions;

/// <summary>
/// The Epic 23 proxies: the org-scoped spelling of subscription-svc's <c>/v1/mode-b/**</c>
/// (D3' Δ 2026-06-21 items 15, 16, 17; SCR-FP-011/012).
/// </summary>
/// <remarks>
/// <para>
/// <b>A proxy, not a second implementation.</b> D3' gives the Fleet Portal
/// <c>/v1/fleets/{fleetId}/vehicles/{vehicleId}/…</c> over the same rows a driver reaches at
/// <c>/v1/mode-b/{vehicleId}/…</c>, and subscription-svc's own CLAUDE.md says both spellings
/// "resolve to the same rows and the same checks; the proxy adds the org scope". Re-implementing
/// the roster or the payment ledger here would give Epic 23 two writers of
/// <c>subscription.payments</c> that could disagree about a month's due date.
/// </para>
/// <para>
/// <b>What this hop adds is the org scope, and it adds it before anything leaves the process.</b>
/// The vehicle in the path must be on the caller's roster, checked through the RLS-scoped view —
/// so "that vehicle is not in your fleet" is answered here, in the vocabulary the Fleet Portal
/// renders, rather than arriving from another service as a 403 about vehicle ownership.
/// </para>
/// <para>
/// <b>The caller's own bearer is forwarded, never a service credential.</b> subscription-svc
/// resolves what the caller may do <em>against the vehicle</em> — owner, org Owner, org Manager or
/// assigned driver, computed in the same query that fetches it (C048's
/// <c>ModeBRegistryRepository</c>) — so forwarding the operator's token keeps that check where it
/// is, and means this hop can grant nothing the operator did not already have. In particular the
/// owner-only rules (mark cash received, override a fare, delete a subscriber, confirm a slip)
/// stay owner-only without this service restating them.
/// </para>
/// <para>
/// <b>No retry, and the upstream status is passed through untouched.</b> A proxy must not invent
/// retries its caller did not ask for — a retried <c>accept</c> is a second grant — and an operator
/// has to see <c>409 conflict</c> from the request queue rather than a 502. The body is streamed
/// back as it arrived, so the RFC 7807 <c>type</c> the portal branches on is the one
/// subscription-svc chose.
/// </para>
/// </remarks>
public interface ISubscriptionProxy
{
    /// <summary>
    /// Forwards one request to subscription-svc's <c>/v1/mode-b</c> surface.
    /// </summary>
    /// <param name="modeBPath">
    /// The path under <c>/v1/mode-b</c>, e.g. <c>{vehicleId}/subscribers</c>. Built by the endpoint
    /// from route values this service has already parsed, never from raw request text.
    /// </param>
    Task ForwardAsync(
        HttpContext context,
        Guid fleetId,
        Guid? vehicleId,
        HttpMethod method,
        string modeBPath,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISubscriptionProxy"/>
internal sealed class SubscriptionProxy(
    IHttpClientFactory clients,
    IFleetScopedReader scopedReader,
    IFleetVehicleRepository vehicles,
    ILogger<SubscriptionProxy> logger) : ISubscriptionProxy
{
    /// <summary>The named client, so the base address and the timeout live in one place.</summary>
    public const string HttpClientName = "subscription-svc";

    /// <summary>Headers that describe <em>this</em> hop and must not be copied onto the next one.</summary>
    private static readonly string[] HopByHopHeaders =
    [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host",
    ];

    public async Task ForwardAsync(
        HttpContext context,
        Guid fleetId,
        Guid? vehicleId,
        HttpMethod method,
        string modeBPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeBPath);

        if (vehicleId is { } vehicle)
        {
            _ = await scopedReader.ReadAsync(
                fleetId,
                (connection, transaction) => vehicles.FindAsync(
                    connection, transaction, fleetId, vehicle, cancellationToken),
                cancellationToken)
                ?? throw new MageRideException(
                    MageRideErrors.VehicleNotFound, "This vehicle is not in the organisation's fleet.");
        }

        var bearer = context.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(bearer))
        {
            throw new MageRideException(
                MageRideErrors.Unauthorized, "This route forwards the caller's own bearer and there is none.");
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, $"v1/mode-b/{modeBPath}{context.Request.QueryString}");

        request.Headers.TryAddWithoutValidation("Authorization", bearer);

        // `Idempotency-Key` is forwarded when the client sent one, and one is minted when it did
        // not: subscription-svc's kernel requires it on every POST, and a proxy that dropped the
        // header would turn a well-behaved client's retry into a second accept.
        var idempotencyKey = context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString();

        if (method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation(
                MageRideHeaders.IdempotencyKey,
                string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey);
        }

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContentLength is not null
            || HasBody(context))
        {
            // The buffered body, rewound. `EnableBuffering` is the idempotency middleware's doing,
            // which is what makes the stream seekable at all — the same trap subscription-svc's own
            // wallet forwarder records: keying on `Content-Length` drops the body of every call made
            // by a .NET client, which sends chunked.
            context.Request.Body.Position = 0;

            var body = new StreamContent(context.Request.Body);

            if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
            {
                body.Headers.ContentType = contentType;
            }

            request.Content = body;
        }

        using var response = await SendAsync(client, request, cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        // Kestrel sets its own; a copied one from the upstream would disagree the moment the body
        // is re-chunked.
        context.Response.Headers.Remove("Content-Length");

        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception, "subscription-svc could not be reached for {Method} {Path}.", request.Method, request.RequestUri);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "The Mode B subscription surface is unavailable. Nothing was changed.");
        }
    }

    private static bool HasBody(HttpContext context) =>
        context.Request.Body.CanSeek && context.Request.Body.Length > 0;
}
