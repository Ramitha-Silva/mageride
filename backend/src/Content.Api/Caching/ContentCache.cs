using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MageRide.Content.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Content.Caching;

/// <summary>
/// The five datasets this service caches. One name per cache, because a purge names what changed.
/// </summary>
/// <remarks>
/// The strings are the <c>datasets</c> enum in
/// <c>backend/contracts/content.yaml#/paths/~1v1~1internal~1content~1cache~1purge</c> and the
/// payload of <c>RedisKeys.ContentInvalidationChannel</c>. Three consumers of one vocabulary, so it
/// is declared once.
/// </remarks>
internal static class ContentDatasets
{
    public const string Cities = "cities";
    public const string Templates = "templates";
    public const string Faq = "faq";
    public const string Broadcasts = "broadcasts";
    public const string Onboarding = "onboarding";

    public static readonly string[] All = [Cities, Templates, Faq, Broadcasts, Onboarding];

    public static bool IsKnown(string? dataset) =>
        dataset is not null && Array.IndexOf(All, dataset) >= 0;
}

/// <summary>A cached payload and the strong validator that identifies it.</summary>
/// <remarks>
/// The ETag is computed once per cache *load*, not per request: it is a digest of the serialised
/// payload, so it changes if and only if a byte a client would receive changes. Deriving it from
/// <c>max(updated_at)</c> and a row count would be cheaper and would miss a change that reused a
/// timestamp; deriving it per request would hash the same bytes on every call.
/// </remarks>
/// <param name="Payload">The response body.</param>
/// <param name="ETag">Quoted strong validator, ready for the <c>ETag</c> header.</param>
internal sealed record CachedDocument<T>(T Payload, string ETag);

/// <summary>
/// The in-process read cache in front of Postgres, with a TTL and an explicit purge.
/// </summary>
/// <remarks>
/// <para>
/// <b>In process rather than in Redis, deliberately.</b> Every dataset here is small (a dozen FAQ
/// rows, three cities, six slides, a handful of template keys) and the template read is on the
/// hottest cold path the platform has — E-01 pushes a ride offer to every candidate driver of every
/// ride, and each push renders a template. A Redis lookup per render would replace a 1 ms query with
/// a 1 ms network hop; a local dictionary replaces it with a pointer dereference. What Redis is used
/// for is the *purge*, which is the part that has to cross replicas.
/// </para>
/// <para>
/// <b>A miss is a load, not a lock.</b> Two concurrent misses both query and the second write wins;
/// the loads are pure reads of the same rows, so the only cost is a duplicate query at start-up.
/// Serialising them would put a lock on the hot path to save a query that runs twice every five
/// minutes.
/// </para>
/// <para>
/// Expiry is measured on <see cref="TimeProvider"/> rather than a timer, so nothing sweeps and a
/// test can advance the clock across the TTL the definition of done names.
/// </para>
/// </remarks>
internal sealed class ContentCache(IOptions<ContentOptions> options, TimeProvider clock, ILogger<ContentCache> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ContentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>How many entries are held. For diagnostics and for the tests.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Serves <paramref name="dataset"/>/<paramref name="key"/> from cache, or loads and caches it.
    /// </summary>
    /// <param name="dataset">One of <see cref="ContentDatasets"/> — what a purge names.</param>
    /// <param name="key">Varies the entry within the dataset, e.g. a language or a template key.</param>
    /// <param name="load">Reads the payload from Postgres. Called only on a miss.</param>
    public async Task<CachedDocument<T>> GetOrLoadAsync<T>(
        string dataset,
        string key,
        Func<CancellationToken, Task<T>> load,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);

        var entryKey = $"{dataset}:{key}";
        var now = _clock.GetUtcNow();

        if (_options.CacheEnabled
            && _entries.TryGetValue(entryKey, out var cached)
            && cached.ExpiresAt > now
            && cached.Document is CachedDocument<T> hit)
        {
            return hit;
        }

        var payload = await load(cancellationToken).ConfigureAwait(false);
        var document = new CachedDocument<T>(payload, ComputeETag(payload));

        if (_options.CacheEnabled && HasRoomFor(entryKey, now))
        {
            _entries[entryKey] = new Entry(document, now + _options.CacheTtl);
        }

        return document;
    }

    /// <summary>
    /// Whether a *new* key may be added, dropping expired entries first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is a backstop, not a working limit: the five datasets need about a dozen entries
    /// between them (one per city list, one per audience, one per language of the FAQ, one per template
    /// key), so `Content:MaxCacheEntries` is three orders of magnitude of headroom and only bites under
    /// abuse. It exists because one key family — the template key — comes from a caller, and a service
    /// whose memory a caller can grow without bound is bounded by a shared secret rather than by
    /// construction.
    /// </para>
    /// <para>
    /// At the ceiling the read still answers; it is just not cached, and the fact is logged once per
    /// full sweep rather than per request. Nothing evicts on a schedule — expired entries are dropped
    /// here, which is the only moment their absence could matter.
    /// </para>
    /// </remarks>
    private bool HasRoomFor(string entryKey, DateTimeOffset now)
    {
        if (_entries.Count < _options.MaxCacheEntries || _entries.ContainsKey(entryKey))
        {
            return true;
        }

        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        if (_entries.Count < _options.MaxCacheEntries)
        {
            return true;
        }

        logger.LogWarning(
            "The content cache is at Content:MaxCacheEntries ({Max}) and none of it has expired, so "
            + "{Key} is being served uncached. Every dataset here needs a dozen entries between them; "
            + "this many means something is asking for template keys that do not exist.",
            _options.MaxCacheEntries,
            entryKey);

        return false;
    }

    /// <summary>Drops every entry of the named datasets; an empty list drops all of them.</summary>
    /// <returns>The dataset names that were actually purged, for the purge endpoint's response.</returns>
    public IReadOnlyList<string> Purge(IReadOnlyCollection<string>? datasets)
    {
        string[] targets = datasets is null || datasets.Count == 0
            ? ContentDatasets.All
            : [.. datasets.Where(ContentDatasets.IsKnown).Distinct(StringComparer.Ordinal)];

        var dropped = 0;

        foreach (var dataset in targets)
        {
            var prefix = $"{dataset}:";

            foreach (var key in _entries.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && _entries.TryRemove(key, out _))
                {
                    dropped++;
                }
            }
        }

        logger.LogDebug(
            "Purged {Dropped} cache entries across {Datasets}.", dropped, string.Join(", ", targets));

        return targets;
    }

    /// <summary>
    /// A quoted strong validator over the payload's serialised bytes.
    /// </summary>
    /// <remarks>
    /// <see cref="MageRideJson.Options"/> — the same options the response is written with, so the
    /// digest covers exactly what the client will receive. SHA-256 truncated to 128 bits: an ETag is
    /// a cache key, not a signature, and 32 characters keeps the header small.
    /// </remarks>
    private static string ComputeETag<T>(T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, MageRideJson.Options);
        var digest = SHA256.HashData(bytes);

        return $"\"{Convert.ToHexStringLower(digest.AsSpan(0, 16))}\"";
    }

    private sealed record Entry(object Document, DateTimeOffset ExpiresAt);
}
