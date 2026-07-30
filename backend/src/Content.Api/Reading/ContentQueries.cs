using MageRide.Content.Caching;
using MageRide.Content.Configuration;
using MageRide.Content.Domain;
using MageRide.Content.Endpoints;
using MageRide.Content.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Content.Reading;

/// <summary>
/// Every read this service serves, cached and language-resolved.
/// </summary>
/// <remarks>
/// One place, so the fences hold once each: only active cities are served, only published template
/// versions are resolved, and a language that has no row falls back rather than 404s.
/// </remarks>
internal sealed class ContentQueries(
    ContentCache cache,
    IReferenceDataRepository reference,
    ITemplateRepository templates,
    IFaqRepository faq,
    IBroadcastRepository broadcasts,
    IOptions<ContentOptions> options,
    TimeProvider clock,
    ILogger<ContentQueries> logger)
{
    private readonly ContentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The launch-city list (AL-27), with its validator.</summary>
    public Task<CachedDocument<CitiesResponse>> CitiesAsync(CancellationToken cancellationToken) =>
        cache.GetOrLoadAsync(
            ContentDatasets.Cities,
            string.Empty,
            async token =>
            {
                var rows = await reference.ReadActiveCitiesAsync(token);

                return new CitiesResponse(
                [
                    .. rows.Select(row => new OperatingCityResponse(
                        row.Code,
                        row.NameEn,
                        row.NameSi,
                        row.NameTa,
                        new CentroidResponse(row.CentroidLat, row.CentroidLng),
                        row.SortOrder)),
                ]);
            },
            cancellationToken);

    /// <summary>The AL-28 carousel for one audience, with its validator.</summary>
    public Task<CachedDocument<OnboardingResponse>> OnboardingAsync(
        string audience, CancellationToken cancellationToken) =>
        cache.GetOrLoadAsync(
            ContentDatasets.Onboarding,
            audience,
            async token =>
            {
                var rows = await reference.ReadSlidesAsync(audience, token);

                if (rows.Count == 0)
                {
                    // Not a 404: the audience is valid and the carousel is presentation. An empty
                    // list draws the language and city selectors with nothing above them, which is
                    // the screen as it was before AL-28. Logged because an empty carousel in
                    // production means migration 1903's seed was reverted, and nothing else would
                    // say so.
                    logger.LogWarning(
                        "No active onboarding slides for audience {Audience}: the AL-28 carousel will be "
                        + "blank on that app's first-run screen.",
                        audience);
                }

                return new OnboardingResponse(
                [
                    .. rows.Select(row => new OnboardingSlideResponse(
                        row.Slot,
                        Illustration(row.IllustrationRef),
                        ToBody(row.Title),
                        ToBody(row.Body))),
                ]);
            },
            cancellationToken);

    /// <summary>
    /// The current published version of one template in one language, or <see langword="null"/> when
    /// the key has no published version at all.
    /// </summary>
    /// <remarks>
    /// <b>An absent language falls back and says so.</b> The response carries the language actually
    /// served, so notification-svc can compare it with what it asked for. Migration 1307's trigger
    /// means the publish path cannot create an incomplete key, so reaching the fallback at all
    /// implies a row written around this service — which is why it is logged at warning level rather
    /// than silently resolved. Refusing to serve would be worse: an undelivered ride offer is a
    /// driver who never learns about a fare, and D3' promises the fallback in as many words.
    /// </remarks>
    public async Task<NotificationTemplateResponse?> TemplateAsync(
        string key, string? language, CancellationToken cancellationToken)
    {
        var document = await cache.GetOrLoadAsync(
            ContentDatasets.Templates,
            key,
            token => templates.ReadPublishedAsync(key, token),
            cancellationToken);

        if (document.Payload is not { } set)
        {
            return null;
        }

        var requested = Languages.Resolve(language);

        if (!set.ByLanguage.TryGetValue(requested, out var text))
        {
            var served = Languages.FallbackOrder.FirstOrDefault(set.ByLanguage.ContainsKey);

            if (served is null)
            {
                return null;
            }

            logger.LogWarning(
                "Template {Key} has no {Requested} row; serving {Served}. Every template exists in all "
                + "three languages (D-26) unless a row was written outside content-svc.",
                key,
                requested,
                served);

            requested = served;
            text = set.ByLanguage[served];
        }

        return new NotificationTemplateResponse(
            key,
            requested,
            text.Version,
            text.Subject,
            text.Body,
            [.. TemplatePlaceholders.Extract(text.Subject).Concat(TemplatePlaceholders.Extract(text.Body)).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>FAQ articles in the resolved language (US-16.1).</summary>
    /// <remarks>
    /// <b>The cache is keyed by language alone and the category is applied to the cached rows.</b>
    /// Keying it by <c>(language, category)</c> — which is what the query does — would put a
    /// caller-supplied string in a cache key: `?category=*` and "no category" would collide on any
    /// sentinel a category could also contain, poisoning the unfiltered answer for a whole TTL, and a
    /// loop over random categories would grow the cache without bound. Three entries hold every FAQ
    /// article on the platform, so there is nothing to gain by splitting them further.
    /// </remarks>
    public async Task<FaqResponse> FaqAsync(
        string? language, string? category, CancellationToken cancellationToken)
    {
        var resolved = Languages.Resolve(language);
        var normalisedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        var document = await cache.GetOrLoadAsync(
            ContentDatasets.Faq,
            resolved,
            async token =>
            {
                // One over the cap, so a full page can be told from a truncated one without a second
                // count query. No silent caps.
                var rows = await faq.ReadAsync(resolved, _options.MaxFaqItems + 1, token);

                if (rows.Count > _options.MaxFaqItems)
                {
                    logger.LogWarning(
                        "FAQ read for {Language} hit Content:MaxFaqItems ({Max}); the answer is truncated.",
                        resolved,
                        _options.MaxFaqItems);

                    rows = [.. rows.Take(_options.MaxFaqItems)];
                }

                return new FaqResponse(
                    resolved,
                    [
                        .. rows.Select(row => new FaqArticleResponse(
                            row.Id, row.Category, row.Title, row.Body, row.SortOrder)),
                    ]);
            },
            cancellationToken);

        if (normalisedCategory is null)
        {
            return document.Payload;
        }

        return new FaqResponse(
            document.Payload.Language,
            [
                .. document.Payload.Items.Where(
                    item => string.Equals(item.Category, normalisedCategory, StringComparison.Ordinal)),
            ]);
    }

    /// <summary>
    /// The announcements in force for one caller, in the resolved language (US-14.8).
    /// </summary>
    /// <remarks>
    /// The cache holds the rows; the window and the audience are applied per request. Caching the
    /// filtered answer would hold a scheduled banner back for up to one TTL after its start time and
    /// would need a cache entry per role/app combination.
    /// </remarks>
    public async Task<BroadcastsResponse> BroadcastsAsync(
        string? language,
        IReadOnlyCollection<string> roles,
        string? app,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var resolved = Languages.Resolve(language);
        var now = clock.GetUtcNow();

        var document = await cache.GetOrLoadAsync(
            ContentDatasets.Broadcasts,
            string.Empty,
            async token =>
            {
                // The load reaches one TTL into the future, so a banner scheduled to start inside the
                // life of this entry is already here when it does — and no further, so a batch
                // scheduled for next month cannot fill the limit and push today's banner out.
                var rows = await broadcasts.ReadLiveAsync(
                    now, now + _options.CacheTtl, _options.MaxBroadcasts + 1, token);

                if (rows.Count > _options.MaxBroadcasts)
                {
                    logger.LogWarning(
                        "More than Content:MaxBroadcasts ({Max}) announcements are live or start within "
                        + "the cache TTL; the oldest are dropped from the banner list.",
                        _options.MaxBroadcasts);

                    rows = [.. rows.Take(_options.MaxBroadcasts)];
                }

                return rows;
            },
            cancellationToken);

        var live = document.Payload
            .Where(row => row.IsLiveAt(now) && row.Audience.Matches(roles, app))
            .Take(_options.MaxBroadcasts)
            .Select(row => new BroadcastResponse(
                row.Id,
                row.Message[resolved],
                row.StartsAt,
                row.EndsAt,
                row.Audience.IsEveryone ? null : new BroadcastAudienceBody(row.Audience.Role, row.Audience.App)))
            .ToArray();

        return new BroadcastsResponse(live);
    }

    /// <summary>
    /// Prefixes a stored illustration reference with <c>Content:AssetBaseUrl</c> when one is set.
    /// </summary>
    /// <remarks>
    /// A ref that is already absolute is left alone, so a deployment can move one slide's artwork to
    /// a CDN without moving the rest.
    /// </remarks>
    private string Illustration(string reference)
    {
        if (string.IsNullOrWhiteSpace(_options.AssetBaseUrl)
            || Uri.IsWellFormedUriString(reference, UriKind.Absolute))
        {
            return reference;
        }

        return $"{_options.AssetBaseUrl.TrimEnd('/')}/{reference.TrimStart('/')}";
    }

    private static TrilingualTextBody ToBody(TrilingualText text) =>
        new(text.Sinhala, text.Tamil, text.English);
}
