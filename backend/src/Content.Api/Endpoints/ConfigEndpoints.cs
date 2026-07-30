using System.Globalization;
using MageRide.Content.Caching;
using MageRide.Content.Configuration;
using MageRide.Content.Reading;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MageRide.Content.Endpoints;

/// <summary>
/// <c>GET /v1/config/cities</c> — the launch-city list (AL-27, Change 6/22).
/// </summary>
/// <remarks>
/// <para>
/// <b>Public, and that is a requirement rather than an oversight.</b> The list is drawn on
/// SCR-DA/DI-002, the first-run language and city screen, which precedes phone-OTP sign-in
/// entirely — there is no token to present. D3' marks the operation "Auth: none (public),
/// read-only" and <c>content.yaml</c> declares <c>security: []</c>.
/// </para>
/// <para>
/// <b>Cacheable, because the point of AL-27 is that launching a city needs no app release.</b> That
/// only pays off if the app re-reads the list cheaply, so the answer carries a strong ETag and a
/// <c>max-age</c> equal to this service's own cache TTL — an intermediary then caches for the window
/// the service does rather than a guess of its own (the rule iam-svc's JWKS endpoint follows).
/// </para>
/// <para>
/// <b>The active filter is not here.</b> It is <c>WHERE is_active</c> inside the query
/// (<c>ReferenceDataRepository</c>), so "only active operating cities are served publicly" is a
/// property of what can be read rather than of what is done with it.
/// </para>
/// </remarks>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/v1/config/cities", GetCitiesAsync)
            .AllowAnonymous()
            .WithTags("config")
            .WithName("listOperatingCities");

        return endpoints;
    }

    private static async Task<Results<Ok<CitiesResponse>, StatusCodeHttpResult>> GetCitiesAsync(
        HttpContext context,
        ContentQueries content,
        IOptions<ContentOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var document = await content.CitiesAsync(cancellationToken);

        return CacheHeaders.Conditional(context, document, options.Value.CacheTtl);
    }
}

/// <summary>
/// The validator and freshness headers the two public reads share.
/// </summary>
/// <remarks>
/// Shared by <c>/v1/config/cities</c> and <c>/v1/content/onboarding/{audience}</c> because they are
/// the same kind of answer to the same screen, and a difference between them would show up as one of
/// the two halves of that screen going stale for a different length of time.
/// </remarks>
internal static class CacheHeaders
{
    /// <summary>
    /// Answers <c>304</c> when the caller's <c>If-None-Match</c> already names this payload, and
    /// <c>200</c> with the validator otherwise.
    /// </summary>
    /// <remarks>
    /// The ETag is written on both paths — RFC 9110 requires a 304 to carry the validator that
    /// matched, and a client that received a bare 304 would have nothing to revalidate with next
    /// time.
    /// </remarks>
    public static Results<Ok<T>, StatusCodeHttpResult> Conditional<T>(
        HttpContext context, CachedDocument<T> document, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);

        var seconds = (int)maxAge.TotalSeconds;

        context.Response.Headers.CacheControl =
            string.Create(CultureInfo.InvariantCulture, $"public, max-age={seconds}");
        context.Response.Headers[HeaderNames.ETag] = document.ETag;

        // `If-None-Match: *` matches any existing representation (RFC 9110 §13.1.2), and a list
        // endpoint always has one.
        //
        // The comparison is the **weak** one RFC 9110 §13.1.2 requires for this header, which means
        // `W/"abc"` matches `"abc"`. That is not pedantry: an intermediary that re-encodes the body
        // (gzip is the common case) is required to weaken the validator it passes on, and a strict
        // compare would leave every client behind such a proxy silently refetching the whole list for
        // ever — the caching AL-27 depends on, quietly off.
        var presented = context.Request.Headers[HeaderNames.IfNoneMatch];

        foreach (var candidate in presented)
        {
            if (candidate is null)
            {
                continue;
            }

            foreach (var tag in candidate.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tag == "*" || string.Equals(Strip(tag), document.ETag, StringComparison.Ordinal))
                {
                    return TypedResults.StatusCode(StatusCodes.Status304NotModified);
                }
            }
        }

        return TypedResults.Ok(document.Payload);
    }

    /// <summary>Drops the <c>W/</c> prefix of a weak validator, leaving the opaque tag.</summary>
    private static string Strip(string tag) =>
        tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
