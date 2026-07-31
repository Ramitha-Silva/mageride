using System.Net.Http.Headers;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Wallet;

/// <summary>
/// Re-issues a driver's own request against wallet-svc, carrying their bearer.
/// </summary>
/// <remarks>
/// <para>
/// D3' Part 2 spells the bulk-voucher purchase and the whole credit-transfer flow twice — once under
/// subscription-svc (<c>/v1/vouchers/purchase</c>, <c>/v1/transfers/driver</c>,
/// <c>/v1/subscriptions/credit-transfer/*</c>) and once under wallet-svc (<c>/v1/wallet/**</c>) — and
/// C046 landed the working half, because <c>billing.*</c> has one writer and ADD §11.6 draws
/// subscription-svc calling wallet-svc for the balance check and the movement. Reimplementing them
/// here would be a second writer of the same money with a second copy of the discount arithmetic, the
/// not-self rule and the <c>PENDING</c> claim. So the D3'-spelled routes exist and forward.
/// </para>
/// <para>
/// <b>The caller's bearer is forwarded, never a service credential.</b> wallet-svc scopes every one of
/// these operations to the token's subject — a transfer that is not the caller's is a 404 there — so
/// forwarding the driver's own token keeps that check where it is and means this hop can grant nothing
/// the driver did not already have. A service-to-service key would turn a proxy into a privilege
/// escalation.
/// </para>
/// <para>
/// <b><c>Idempotency-Key</c> and <c>X-Attestation</c> travel with it</b>, because both are part of the
/// operation D3' declares: dropping the first would let a retry become a second transfer at the far
/// end, and dropping the second would make the D-30 gate unsatisfiable for the three money routes that
/// require it.
/// </para>
/// <para>
/// The response is streamed back verbatim — status, <c>content-type</c> and body. A rewritten problem
/// document would give a client two different shapes for one failure depending on which spelling of
/// the route it called.
/// </para>
/// </remarks>
internal interface IWalletForwarder
{
    /// <summary>Whether wallet-svc's base address is configured. False ⇒ the routes are not mapped.</summary>
    bool IsConfigured { get; }

    /// <summary>Forwards the current request to <paramref name="walletPath"/> and copies the answer back.</summary>
    Task ForwardAsync(HttpContext context, HttpMethod method, string walletPath, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWalletForwarder"/>
internal sealed class WalletForwarder(
    IHttpClientFactory clients,
    IOptions<SubscriptionOptions> options,
    ILogger<WalletForwarder> logger) : IWalletForwarder
{
    /// <summary>
    /// The named client for the forwarded routes. <b>No resilience pipeline</b>: a proxy must not
    /// invent retries a caller did not ask for, and the far end is already idempotent on the
    /// <c>Idempotency-Key</c> this hop carries through.
    /// </summary>
    public const string HttpClientName = "wallet-forward";

    private readonly SubscriptionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Headers that describe <em>this</em> hop rather than the operation, and must not be copied.
    /// </summary>
    /// <remarks>
    /// <c>Host</c> would address the wrong service; the payload headers are set by the copied content
    /// itself and setting them twice throws; <c>Connection</c> and the transfer encodings belong to the
    /// socket underneath. Everything else — <c>Authorization</c>, <c>Idempotency-Key</c>,
    /// <c>X-Attestation</c>, <c>Accept-Language</c>, the trace headers — is the caller's request and
    /// travels.
    /// </remarks>
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Connection", "Transfer-Encoding", "Keep-Alive",
        "Upgrade", "Proxy-Authorization", "TE", "Trailer",
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.WalletBaseUrl);

    public async Task ForwardAsync(
        HttpContext context, HttpMethod method, string walletPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, walletPath);

        foreach (var header in context.Request.Headers)
        {
            if (!HopHeaders.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
            }
        }

        // Seekability, not Content-Length, is the test for "there is a body here". A chunked request
        // carries no Content-Length at all — which is exactly what .NET's own JsonContent sends, so
        // keying on the header drops the body of every call made by a .NET client and hands wallet-svc
        // an empty object. What makes the stream seekable is the idempotency middleware's
        // EnableBuffering, which every forwarded POST passes through; a GET never does, and its body is
        // empty anyway.
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);

            if (buffer.Length > 0)
            {
                request.Content = new ByteArrayContent(buffer.ToArray());

                if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
                {
                    request.Content.Headers.ContentType = contentType;
                }
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "wallet-svc did not answer {Method} {Path}.", method, walletPath);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "wallet-svc did not answer. This operation is served by wallet-svc; try "
                + $"{walletPath} directly if this route stays unavailable.");
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            if (response.Content.Headers.ContentType is { } responseType)
            {
                context.Response.ContentType = responseType.ToString();
            }

            // The replay marker is part of the answer: a client that retried needs to know wallet-svc
            // replayed rather than re-executed, and the header is the only place that is said.
            if (response.Headers.TryGetValues(MageRideHeaders.IdempotencyKey, out var echoed))
            {
                context.Response.Headers[MageRideHeaders.IdempotencyKey] = echoed.ToArray();
            }

            if (response.Headers.TryGetValues("X-Idempotent-Replay", out var replay))
            {
                context.Response.Headers["X-Idempotent-Replay"] = replay.ToArray();
            }

            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }
}
