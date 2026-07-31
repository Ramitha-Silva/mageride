using System.Net;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Geo;

/// <summary>Resolves a shared Google Maps link to a coordinate (AL-20, BR-23.4).</summary>
public interface IMapsLinkResolver
{
    /// <summary>The coordinate, or null when the link could not be read.</summary>
    Task<ParsedLocation?> ResolveAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Follows a short link's redirect and parses the URL it lands on. <b>No Google API.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint fetches a URL a user pasted, so the host allowlist is the whole security
/// story.</b> Without it the route is an authenticated SSRF primitive: paste
/// <c>http://169.254.169.254/latest/meta-data/</c> and the platform fetches the cluster's metadata
/// endpoint on the caller's behalf and hands back whatever it can parse out of the answer. Every
/// hop is re-checked, not just the first — a shortener that redirects off-list is refused
/// mid-chain, which is the case that matters, because the first URL is the one an attacker cannot
/// control the destination of.
/// </para>
/// <para>
/// <b>Redirects are followed by hand rather than by <c>HttpClient</c>.</b> Automatic redirect
/// handling would follow the chain wherever it went and only tell us where it ended, and by then
/// the request to the private address has already been made. `AllowAutoRedirect` is off on the
/// named client for exactly this reason.
/// </para>
/// <para>
/// <b>The budget is BR-23.4's, and it covers the retry.</b> "3 s timeout, 1 retry → pick-on-map":
/// the sheet says "Reading link…" for three seconds and then offers the map, so a per-attempt
/// budget would make the worst case twice what the user was promised.
/// </para>
/// </remarks>
public sealed class MapsLinkResolver : IMapsLinkResolver
{
    /// <summary>The named client. <c>AllowAutoRedirect</c> is off on it — see the remarks.</summary>
    public const string HttpClientName = "maps-link";

    private readonly IHttpClientFactory _clients;
    private readonly TransitOptions _options;
    private readonly ILogger<MapsLinkResolver> _logger;

    public MapsLinkResolver(
        IHttpClientFactory clients, IOptions<TransitOptions> options, ILogger<MapsLinkResolver> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ParsedLocation?> ResolveAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        if (!IsAllowed(uri))
        {
            _logger.LogWarning(
                "A paste-link resolve named {Scheme}://{Host}, which is not a Google Maps host. Refused before "
                + "any request was made.",
                uri.Scheme, uri.Host);

            return null;
        }

        // A full URL carries its coordinate already; there is nothing to fetch, and fetching it
        // would put a network hop on the common case. Anything else on the allowlist is a link
        // whose coordinate is on the other side of a redirect — which is what a short link is.
        //
        // **The allowlist is the only host rule.** An earlier revision also kept a hardcoded
        // "is this a shortener" list, and the two could disagree: a host an operator allowed was
        // then refused by a constant nobody could see. One list, one decision.
        if (MapsLinkParser.Parse(uri.OriginalString) is { } direct)
        {
            return direct;
        }

        // One budget for the whole thing, retry included (BR-23.4).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.MapsLink.Timeout);

        for (var attempt = 0; attempt <= _options.MapsLink.Retries; attempt++)
        {
            try
            {
                if (await FollowAsync(uri, budget.Token) is { } resolved)
                {
                    return resolved;
                }

                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Resolving a short maps link exceeded the {Timeout} budget; the client falls back to "
                    + "pick-on-map (BR-23.4).",
                    _options.MapsLink.Timeout);

                return null;
            }
            catch (HttpRequestException exception) when (attempt < _options.MapsLink.Retries)
            {
                _logger.LogDebug(exception, "Short-link resolve attempt {Attempt} failed; retrying once.", attempt + 1);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "A short maps link could not be resolved.");

                return null;
            }
        }

        return null;
    }

    /// <summary>Walks the redirect chain, checking the allowlist at every hop.</summary>
    private async Task<ParsedLocation?> FollowAsync(Uri uri, CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(HttpClientName);
        var current = uri;

        for (var hop = 0; hop < _options.MapsLink.MaxRedirects; hop++)
        {
            // HEAD, not GET: the answer is in the Location header, and a shortener's landing page
            // is a megabyte of HTML this service has no reason to read. A host that refuses HEAD
            // falls through to GET below.
            using var response = await SendAsync(client, current, cancellationToken);

            if (response.Headers.Location is null)
            {
                // Not a redirect. The final URL is the one we asked for, which we already parsed.
                return MapsLinkParser.Parse(current.OriginalString);
            }

            var next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);

            if (!IsAllowed(next))
            {
                _logger.LogWarning(
                    "A short maps link redirected to {Scheme}://{Host}, which is not a Google Maps host. The "
                    + "chain was abandoned rather than followed.",
                    next.Scheme, next.Host);

                return null;
            }

            if (MapsLinkParser.Parse(next.OriginalString) is { } resolved)
            {
                return resolved;
            }

            current = next;
        }

        _logger.LogWarning("A short maps link exceeded {MaxRedirects} redirects.", _options.MapsLink.MaxRedirects);

        return null;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);

        var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            return response;
        }

        response.Dispose();

        using var get = new HttpRequestMessage(HttpMethod.Get, uri);

        return await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Whether a host is one this service will make a request to.</summary>
    /// <remarks>
    /// Exact match or a subdomain of an allowed host, never a suffix match on the string —
    /// <c>evilgoo.gl</c> ends with <c>goo.gl</c> and is somebody else's domain entirely.
    /// </remarks>
    internal bool IsAllowed(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        return _options.MapsLink.AllowedHosts.Any(allowed =>
            string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }
}
