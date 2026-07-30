using MageRide.Content.Configuration;
using MageRide.Content.Domain;
using MageRide.Content.Reading;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Content.Endpoints;

/// <summary>
/// <c>/v1/content</c> — the app-facing and internal reads.
/// </summary>
/// <remarks>
/// <para>
/// Three audiences on one prefix, so each route states its own gate: the carousel is public (it is
/// drawn before sign-in), the FAQ and the broadcasts take a bearer, and the template render is
/// internal.
/// </para>
/// <para>
/// <b>The template read is the one internal route on this platform that the gateway forwards.</b>
/// Every other <c>mTLS internal</c> family sits under <c>/v1/internal/**</c>, which
/// <c>gateway-routes.json</c> refuses at the edge — but D3' prints this one as
/// <c>GET /v1/content/templates/{key}</c> under <c>/v1/content</c>, and the gateway's <c>content</c>
/// route forwards that whole prefix. So the guard is in the service:
/// <c>Content:InternalApiKey</c>, checked in fixed time, answering <c>404</c> exactly as the gateway
/// does for the internal prefix. Recorded in the C045 handoff — the path should move under
/// <c>/v1/internal/content/templates/{key}</c> when D3' is next revised.
/// </para>
/// </remarks>
public static class ContentEndpoints
{
    /// <summary>Carries <c>Content:InternalApiKey</c>. Replaced by the mesh peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapContentEndpoints(
        this IEndpointRouteBuilder endpoints, string? internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var content = endpoints.MapGroup("/v1/content").WithTags("content");

        // AllowAnonymous: the carousel sits above the language picker on the first-run screen, so
        // there is no account yet — the same reason /v1/config/cities is public.
        content.MapGet("/onboarding/{audience}", GetOnboardingAsync)
            .AllowAnonymous()
            .WithName("listOnboardingSlides");

        // Bearer, any role. A CSR answering a ticket reads the same FAQ the passenger does.
        content.MapGet("/faq", GetFaqAsync)
            .RequireAuthorization()
            .WithName("listFaqArticles");

        content.MapGet("/broadcasts", GetBroadcastsAsync)
            .RequireAuthorization()
            .WithName("listActiveBroadcasts");

        // AllowAnonymous because the caller is a service with no bearer to present; the filter below
        // is what authenticates it, and the kernel's deny-by-default fallback policy would otherwise
        // 401 before the filter ran. Unset key = open, and said so loudly at start-up: a template
        // body is not a secret and unmapping the route would stop every notification on the platform
        // rendering, with the failure landing on notification-svc.
        content.MapGet($"/templates/{{key:regex({TemplateKeys.RoutePattern})}}", GetTemplateAsync)
            .AllowAnonymous()
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey))
            .WithName("renderNotificationTemplate");

        return endpoints;
    }

    private static async Task<Results<Ok<OnboardingResponse>, StatusCodeHttpResult>> GetOnboardingAsync(
        string audience,
        HttpContext context,
        ContentQueries content,
        IOptions<ContentOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var normalised = OnboardingAudiences.Require(audience);
        var document = await content.OnboardingAsync(normalised, cancellationToken);

        return CacheHeaders.Conditional(context, document, options.Value.CacheTtl);
    }

    private static async Task<Ok<FaqResponse>> GetFaqAsync(
        string? lang,
        string? category,
        ContentQueries content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The contract's own `maxLength: 60`. Enforced rather than assumed: a category is a filter over
        // a small set of keys, and a caller sending kilobytes is not asking a question this surface can
        // answer.
        if (category is { Length: > FaqCategoryMaxLength })
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["category"] = [$"category must be at most {FaqCategoryMaxLength} characters."],
            });
        }

        return TypedResults.Ok(await content.FaqAsync(lang, category, cancellationToken));
    }

    /// <summary><c>content.yaml</c>'s <c>maxLength</c> for the FAQ category filter.</summary>
    private const int FaqCategoryMaxLength = 60;

    private static async Task<Ok<BroadcastsResponse>> GetBroadcastsAsync(
        string? lang,
        HttpContext context,
        ContentQueries content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(content);

        // The whole role set, not the primary role: AL-06 makes effective permissions the union of
        // every role held, so a driver who also books rides sees the driver announcement.
        var roles = context.User.Roles();
        var app = context.User.App();

        return TypedResults.Ok(await content.BroadcastsAsync(lang, roles, app, cancellationToken));
    }

    private static async Task<Ok<NotificationTemplateResponse>> GetTemplateAsync(
        string key,
        string? lang,
        ContentQueries content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var template = await content.TemplateAsync(key, lang, cancellationToken)
                       ?? throw new MageRideException(
                           MageRideErrors.NotFound,
                           $"No published version of template '{key}'.");

        return TypedResults.Ok(template);
    }
}

/// <summary>The two first-run screens the carousel is drawn on (AL-28, US-1.2 / US-1.2a).</summary>
/// <remarks>
/// A closed set in three places that have to agree: <c>ck_onboarding_slides_audience</c> (migration
/// 1307), the path parameter's enum in <c>content.yaml</c>, and this.
/// </remarks>
internal static class OnboardingAudiences
{
    public const string Driver = "driver";
    public const string Passenger = "passenger";

    public static readonly string[] All = [Driver, Passenger];

    /// <summary>
    /// Normalises the path segment, or rejects it with <c>400 validation-failed</c>.
    /// </summary>
    /// <remarks>
    /// A 400 rather than a 404: <c>{audience}</c> is an enumeration, not an identifier, so "there is
    /// no such audience" is a malformed request rather than a missing resource — and an unknown value
    /// almost certainly means a client sent a role (<c>fleet_owner</c>) or a platform (<c>ios</c>)
    /// where an audience belongs, which a 404 would not hint at.
    /// </remarks>
    public static string Require(string? audience)
    {
        var trimmed = audience?.Trim().ToLowerInvariant();

        if (trimmed is not null && Array.IndexOf(All, trimmed) >= 0)
        {
            return trimmed;
        }

        throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["audience"] = [$"audience must be one of: {string.Join(", ", All)}."],
        });
    }
}
