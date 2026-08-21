using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Query.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Errors;
using MageRide.Shared.Observability;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Query.Geo;

/// <summary>A place Nominatim resolved.</summary>
/// <param name="Point">Where it is.</param>
/// <param name="DisplayName">The full label Nominatim produced.</param>
/// <param name="Line1">Street or building — the first line of an address (AL-26).</param>
/// <param name="City">City or district.</param>
public sealed record GeocodedPlace(GeoPoint Point, string DisplayName, string? Line1, string? City);

/// <summary>Forward and reverse geocoding against the self-hosted Nominatim (D-14, D-15).</summary>
public interface IGeocoder
{
    /// <summary><see langword="true"/> when a Nominatim base URL is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Places matching a search string, biased toward a point when one is given.</summary>
    /// <param name="language">
    /// A normalised <c>si</c>/<c>ta</c>/<c>en</c>, or <see langword="null"/> to let Nominatim
    /// answer in OSM's own <c>name</c>. See <see cref="GeoLanguages"/> for why absent is not
    /// English.
    /// </param>
    Task<IReadOnlyList<GeocodedPlace>> SearchAsync(
        string query, GeoPoint? bias, int limit, string? language, CancellationToken cancellationToken);

    /// <summary>The nearest addressable place to a coordinate, or <see langword="null"/>.</summary>
    /// <param name="language">
    /// A normalised <c>si</c>/<c>ta</c>/<c>en</c>, or <see langword="null"/> for no preference.
    /// </param>
    Task<GeocodedPlace?> ReverseAsync(GeoPoint point, string? language, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeocoder"/>
/// <remarks>
/// <para>
/// <b>There is no Google Places call here and there must never be one.</b> D3' makes every map
/// endpoint <c>[REPLACE]</c> as a hard rule and D6' §7.6 names the replacement: Nominatim on a Sri
/// Lanka OSM extract, its own 8 GB VPS, refreshed weekly by the osm-pipeline CronJob. This type has
/// exactly one downstream and no fallback provider — a fallback is how "no Google Maps SDK" becomes
/// "no Google Maps SDK except when the self-hosted one is slow".
/// </para>
/// <para>
/// <b>Results are cached in Redis, and the cache is why this is affordable at all.</b> Nominatim is
/// the slowest dependency on a passenger's search path — a different machine, a different Postgres,
/// a full-text search over an extract — and the traffic is extremely repetitive: a handful of city
/// names, and pins dropped within metres of each other on the same junctions. The TTL is a day
/// against data that changes weekly (D-15), so a cached answer is never more than a seventh of the
/// way to being stale. A Redis failure costs the cache, never the answer.
/// </para>
/// <para>
/// <b>A reverse lookup is cached against a rounded coordinate.</b> Five decimals is about a metre and
/// would never hit twice; four is about eleven metres, which is finer than consumer GNSS error and
/// coarse enough that dragging a pin across a pavement reuses the answer.
/// </para>
/// <para>
/// <b>The language is part of the cache key, not an afterthought on it.</b> Nominatim answers the
/// same coordinate differently per <c>accept-language</c>, so a key that ignored the language
/// would serve the first caller's script to every caller behind them until the entry expired —
/// a Sinhala passenger reading English, or worse, an English one reading Sinhala, with no request
/// leaving the service to explain it.
/// </para>
/// <para>
/// <b>What a translated answer actually looks like is mixed.</b> OSM carries <c>name:si</c> and
/// <c>name:ta</c> for Sri Lanka's towns, districts, provinces and the country itself, and for very
/// little else; a road, a neighbourhood and a shop come back in Latin whatever is asked for. That
/// is the data rather than the wiring, and it is why nothing here treats a Latin substring in the
/// answer as a failed translation.
/// </para>
/// <para>
/// <b>Unconfigured is a distinct state from failing.</b> With no base URL, forward search still
/// answers from the caller's saved and recent places (BR-23.1 lists those as part of the prediction
/// set) and reverse geocoding has no local answer at all, so it returns <see langword="null"/> and
/// the endpoint maps that to <c>503</c>. Both are said at start-up, because a search box that quietly
/// only ever finds your own saved addresses looks like a working search box with a thin index.
/// </para>
/// </remarks>
public sealed class NominatimClient(
    IHttpClientFactory clients,
    IConnectionMultiplexer redis,
    IOptions<QueryOptions> options,
    ILogger<NominatimClient> logger) : IGeocoder
{
    /// <summary>The named <see cref="HttpClient"/> the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "nominatim";

    private const string SearchCachePrefix = "geo:fwd";
    private const string ReverseCachePrefix = "geo:rev";

    /// <summary>The cache-key segment for a request that asked for no particular language.</summary>
    private const string NoLanguage = "-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.NominatimBaseUrl);

    public async Task<IReadOnlyList<GeocodedPlace>> SearchAsync(
        string query, GeoPoint? bias, int limit, string? language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!IsConfigured)
        {
            return [];
        }

        // The bias is part of the key: "Fort" from Colombo and "Fort" from Galle are different
        // questions and Nominatim answers them differently. So is the language — see the type
        // remarks. `-` rather than an empty segment for "no language asked", so the unasked case
        // has a key of its own instead of colliding with whichever language sorts to empty.
        var scope = language ?? NoLanguage;
        var text = query.Trim().ToLowerInvariant();
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{SearchCachePrefix}:{scope}:{limit}:{Round(bias?.Latitude)}:{Round(bias?.Longitude)}:{text}");

        if (await ReadCacheAsync<GeocodedPlace[]>(cacheKey) is { } cached)
        {
            Count("search", "cache_hit");
            return cached;
        }

        var url = $"search?format=jsonv2&addressdetails=1&limit={limit.ToString(CultureInfo.InvariantCulture)}"
                  + $"&q={Uri.EscapeDataString(query)}";

        if (!string.IsNullOrWhiteSpace(_options.CountryCodes))
        {
            url += $"&countrycodes={Uri.EscapeDataString(_options.CountryCodes)}";
        }

        if (bias is { } point)
        {
            // `viewbox` + `bounded=0` prefers without excluding: a passenger searching for a town
            // 200 km away still finds it, and the nearby match sorts first.
            var box = ViewBox(point);
            url += $"&viewbox={box}&bounded=0";
        }

        url += AcceptLanguage(language);

        var results = await GetAsync<NominatimPlace[]>(url, "search", cancellationToken) ?? [];

        var places = results
            .Select(static result => result.ToPlace())
            .OfType<GeocodedPlace>()
            .ToArray();

        await WriteCacheAsync(cacheKey, places);

        return places;
    }

    public async Task<GeocodedPlace?> ReverseAsync(
        GeoPoint point, string? language, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{ReverseCachePrefix}:{language ?? NoLanguage}:{Round(point.Latitude)}:{Round(point.Longitude)}");

        if (await ReadCacheAsync<GeocodedPlace>(cacheKey) is { } cached)
        {
            Count("reverse", "cache_hit");
            return cached;
        }

        var url = "reverse?format=jsonv2&addressdetails=1"
                  + $"&lat={point.Latitude.ToString("0.######", CultureInfo.InvariantCulture)}"
                  + $"&lon={point.Longitude.ToString("0.######", CultureInfo.InvariantCulture)}"
                  + AcceptLanguage(language);

        var result = await GetAsync<NominatimPlace>(url, "reverse", cancellationToken);
        var place = result?.ToPlace();

        if (place is not null)
        {
            await WriteCacheAsync(cacheKey, place);
        }

        return place;
    }

    /// <summary>
    /// One GET against Nominatim. Anything other than a usable body is an absent answer, not an
    /// exception — the endpoints above decide what an absent answer means for their contract.
    /// </summary>
    private async Task<T?> GetAsync<T>(string relativeUrl, string operation, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.GetAsync(relativeUrl, cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                // Nominatim answers a reverse lookup in the sea with a 404 and a body saying "Unable
                // to geocode". That is a real answer to a real question, not a failure.
                Count(operation, "not_found");
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                Count(operation, "error");

                logger.LogWarning(
                    "Nominatim answered {Status} for a {Operation}; treating it as no result.",
                    (int)response.StatusCode, operation);

                return default;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            Count(operation, "upstream");

            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            Count(operation, "unavailable");

            logger.LogError(
                failure,
                "Nominatim is unreachable for a {Operation}. Geocoding is degraded until it recovers; "
                + "there is no third-party fallback by design (D3' map hard rule).",
                operation);

            throw new MageRideException(
                MageRideErrors.DependencyUnavailable,
                "The geocoding service is unavailable.",
                failure);
        }
    }

    private async Task<T?> ReadCacheAsync<T>(string key)
    {
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);

            return cached.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(cached.ToString(), Json);
        }
        catch (Exception failure) when (failure is RedisException or TimeoutException or JsonException)
        {
            // A cache that cannot be read is a cache miss. Debug rather than warning: the upstream
            // call that follows is the correct behaviour, and one log line per search during a Redis
            // outage is noise on top of the outage already being reported by the live map.
            logger.LogDebug(failure, "Geocode cache read failed for {Key}", key);
            return default;
        }
    }

    private async Task WriteCacheAsync<T>(string key, T value)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(
                key, JsonSerializer.Serialize(value, Json), _options.GeocodeCacheTtl);
        }
        catch (Exception failure) when (failure is RedisException or TimeoutException)
        {
            logger.LogDebug(failure, "Geocode cache write failed for {Key}", key);
        }
    }

    private static void Count(string operation, string outcome) =>
        MageRideDiagnostics.GeocodeRequests.Add(
            1,
            new KeyValuePair<string, object?>("op", operation),
            new KeyValuePair<string, object?>("outcome", outcome));

    private string Round(double? value) =>
        value is { } number
            ? Math.Round(number, _options.ReverseCacheDecimals).ToString(CultureInfo.InvariantCulture)
            : "-";

    /// <summary>
    /// A roughly 20 km box around the bias point, as Nominatim's <c>left,top,right,bottom</c>.
    /// </summary>
    /// <remarks>
    /// Not a setting: it is the "near me" hint for a search box, not a search radius, and the box is
    /// unbounded (<c>bounded=0</c>) so nothing is excluded by it. 0.18° of latitude is ~20 km; the
    /// longitude span is corrected for latitude so the box is square on the ground rather than at the
    /// equator.
    /// </remarks>
    private static string ViewBox(GeoPoint centre)
    {
        const double latSpan = 0.18;
        var lngSpan = latSpan / Math.Max(0.1, Math.Cos(centre.Latitude * Math.PI / 180));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{centre.Longitude - lngSpan:0.####},{centre.Latitude + latSpan:0.####},"
            + $"{centre.Longitude + lngSpan:0.####},{centre.Latitude - latSpan:0.####}");
    }

    /// <summary>
    /// The <c>accept-language</c> query segment, or nothing at all when no language was asked for.
    /// </summary>
    /// <remarks>
    /// The <b>query parameter</b> rather than the HTTP header of the same name. Nominatim accepts
    /// either and they mean the same thing, but the header would be set on a pooled
    /// <see cref="HttpClient"/> shared by every in-flight request, and the parameter travels with
    /// the one request it belongs to. It is also what appears in the Nominatim access log, which
    /// is where anyone debugging "why is this English" will look first.
    /// </remarks>
    private static string AcceptLanguage(string? language) =>
        string.IsNullOrEmpty(language) ? string.Empty : $"&accept-language={Uri.EscapeDataString(language)}";

    /// <summary>Nominatim's <c>format=jsonv2</c> shape, only the fields this service uses.</summary>
    private sealed record NominatimPlace(
        [property: JsonPropertyName("lat")] string? Lat,
        [property: JsonPropertyName("lon")] string? Lon,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] NominatimAddress? Address)
    {
        internal GeocodedPlace? ToPlace()
        {
            if (!double.TryParse(Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)
                || lat is < -90 or > 90
                || lng is < -180 or > 180)
            {
                return null;
            }

            return new GeocodedPlace(
                new GeoPoint(lat, lng),
                DisplayName ?? Name ?? string.Empty,
                Address?.Line1() ?? Name,
                Address?.CityOrDistrict());
        }
    }

    /// <summary>
    /// <c>addressdetails=1</c>'s breakdown, mapped onto AL-26's address lines.
    /// </summary>
    /// <remarks>
    /// OSM tagging is not uniform, so each line takes the first of several plausible keys rather than
    /// insisting on one. A Sri Lankan address commonly has <c>road</c> and <c>suburb</c> and no
    /// <c>house_number</c>; the district arrives as <c>state_district</c> as often as <c>city</c>.
    /// </remarks>
    private sealed record NominatimAddress(
        [property: JsonPropertyName("house_number")] string? HouseNumber,
        [property: JsonPropertyName("road")] string? Road,
        [property: JsonPropertyName("neighbourhood")] string? Neighbourhood,
        [property: JsonPropertyName("suburb")] string? Suburb,
        [property: JsonPropertyName("village")] string? Village,
        [property: JsonPropertyName("town")] string? Town,
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("state_district")] string? StateDistrict,
        [property: JsonPropertyName("state")] string? State)
    {
        internal string? Line1() =>
            (HouseNumber, Road) switch
            {
                ({ Length: > 0 } number, { Length: > 0 } road) => $"{number} {road}",
                (_, { Length: > 0 } road) => road,
                _ => FirstOf(Neighbourhood, Suburb),
            };

        internal string? CityOrDistrict() => FirstOf(City, Town, Village, StateDistrict, State);

        private static string? FirstOf(params string?[] candidates) =>
            candidates.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
