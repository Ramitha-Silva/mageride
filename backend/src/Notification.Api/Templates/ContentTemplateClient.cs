using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Templates;

/// <summary>Resolves a template key into one language (D-26, content-svc's render route).</summary>
public interface ITemplateSource
{
    Task<ResolvedTemplate> ResolveAsync(string key, string language, CancellationToken cancellationToken);

    /// <summary>Drops everything cached. The purge hook, and what a test uses between cases.</summary>
    void Invalidate();
}

/// <summary>
/// <c>GET /v1/content/templates/{key}?lang=</c>, with a small in-process cache.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cache is in process, and it is the same argument content-svc makes on its side.</b> A
/// ride offer renders one template per candidate driver, inside a fifteen-second window; a Redis
/// round trip per render would trade a dictionary lookup for a network hop on the platform's
/// hottest path. What has to be shared is invalidation, and content-svc already publishes that on
/// <c>RedisKeys.ContentInvalidationChannel</c> — this client subscribes to it, so an edit is
/// visible immediately rather than within a TTL, and the TTL is the ceiling for the case where the
/// message was missed.
/// </para>
/// <para>
/// <b>Expiry is measured on <see cref="TimeProvider"/> and nothing sweeps.</b> An entry is checked
/// when it is read, so a test can cross a five-minute TTL in a millisecond.
/// </para>
/// <para>
/// <b>A failed lookup is not cached.</b> content-svc being unreachable is a transient condition and
/// the delivery worker will try again; remembering the failure for 300 s would turn a two-second
/// blip into five minutes of unrendered notifications.
/// </para>
/// </remarks>
public sealed class ContentTemplateClient : ITemplateSource
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "content-svc";

    private readonly IHttpClientFactory _clients;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ContentTemplateClient> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public ContentTemplateClient(
        IHttpClientFactory clients,
        IOptions<NotificationOptions> options,
        TimeProvider clock,
        ILogger<ContentTemplateClient> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedTemplate> ResolveAsync(string key, string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var lang = Languages.Normalise(language);
        var cacheKey = $"{key}|{lang}";
        var now = _clock.GetUtcNow();

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Template;
        }

        var template = await FetchAsync(key, lang, cancellationToken);

        if (_options.TemplateCacheTtl > TimeSpan.Zero)
        {
            // The ceiling exists so a caller looping over invented keys cannot grow the process
            // without bound. Clearing rather than evicting one entry: the working set is a hundred
            // entries, so reaching the ceiling means something is wrong, and a full reload is
            // cheaper than an LRU nobody would ever exercise.
            if (_cache.Count >= _options.TemplateCacheMaxEntries)
            {
                _logger.LogWarning(
                    "The template cache reached {Max} entries and was cleared. Either Notification:TemplateCacheMaxEntries "
                    + "is too low or something is resolving keys that do not exist.",
                    _options.TemplateCacheMaxEntries);

                _cache.Clear();
            }

            _cache[cacheKey] = new CacheEntry(template, now + _options.TemplateCacheTtl);
        }

        return template;
    }

    public void Invalidate() => _cache.Clear();

    private async Task<ResolvedTemplate> FetchAsync(string key, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ContentBaseUrl))
        {
            throw new TemplateRenderException(
                "Notification:ContentBaseUrl is not configured, so no template can be resolved and nothing "
                + "user-facing can be sent (D-26).");
        }

        var client = _clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"v1/content/templates/{Uri.EscapeDataString(key)}?lang={language}");

        if (!string.IsNullOrWhiteSpace(_options.ContentInternalApiKey))
        {
            request.Headers.TryAddWithoutValidation(InternalApiKeyHeader, _options.ContentInternalApiKey);
        }

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            // Retryable: the worker will come back. Deliberately *not* a TemplateRenderException,
            // which would fail the notification outright.
            throw new HttpRequestException(
                $"content-svc could not be reached to render '{key}' ({exception.Message}).", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new TemplateRenderException(
                    $"content-svc has no template '{key}'. A key is only content if a migration seeded it beside "
                    + "the code that sends it (content-svc's rule); nothing is sent.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"content-svc answered {(int)response.StatusCode} for template '{key}'.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            TemplatePayload? payload;

            try
            {
                payload = JsonSerializer.Deserialize<TemplatePayload>(text, MageRideJson.Options);
            }
            catch (JsonException exception)
            {
                throw new TemplateRenderException($"content-svc returned an unreadable template for '{key}'.", exception);
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Body))
            {
                throw new TemplateRenderException($"content-svc returned an empty body for template '{key}'.");
            }

            var served = Languages.Normalise(payload.Language);

            if (!string.Equals(served, language, StringComparison.Ordinal))
            {
                // content-svc's own rule: reaching the fallback implies a row written around it,
                // because its publish path cannot create an incomplete key. Loud on this side too —
                // the message still goes out, in the wrong language, and nobody would otherwise know.
                _logger.LogWarning(
                    "Template '{Key}' was asked for in {Asked} and served in {Served}. Every key is supposed to "
                    + "exist in all three languages (D-26); the recipient is getting the wrong one.",
                    key, language, served);
            }

            return new ResolvedTemplate(
                payload.Key ?? key,
                served,
                payload.Version,
                string.IsNullOrWhiteSpace(payload.Title) ? null : payload.Title,
                payload.Body,
                payload.Placeholders ?? []);
        }
    }

    /// <summary>content-svc's guard header (its <c>ContentEndpoints.ApiKeyHeader</c>).</summary>
    internal const string InternalApiKeyHeader = "X-MageRide-Internal-Key";

    private sealed record CacheEntry(ResolvedTemplate Template, DateTimeOffset ExpiresAt);

    /// <summary>content-svc's <c>NotificationTemplateResponse</c>, as far as this service reads it.</summary>
    private sealed record TemplatePayload(
        string? Key, string? Language, int Version, string? Title, string? Body, IReadOnlyList<string>? Placeholders);
}
