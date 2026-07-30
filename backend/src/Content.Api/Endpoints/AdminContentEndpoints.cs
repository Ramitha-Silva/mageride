using MageRide.Content.Domain;
using MageRide.Content.Persistence;
using MageRide.Content.Publishing;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Content.Endpoints;

/// <summary>
/// <c>/v1/admin/content</c> — the D-26 authoring surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admin and Super Admin only.</b> D3' marks the template edit "admin"; AL-06's other four
/// back-office roles have no editorial cell in URD §2.3's content row, and a Verification Officer or
/// a Support CSR editing the text of every push notification on the platform is not a permission any
/// spec grants. The narrower gate is the one that can be widened later without a migration.
/// </para>
/// <para>
/// <b>The approval workflow is two routes because it is two decisions.</b> <c>PUT</c> writes version
/// <c>n+1</c> as a draft; <c>POST …/approve</c> publishes it and records who. Collapsing them would
/// make <c>content.notification_templates.approved_by</c> a column that always names the author —
/// which is the four-eyes property D-35 is after, silently absent. <c>Content:PublishOnEdit</c> is
/// the deployment's escape hatch and the response's <c>status</c> always says which happened.
/// </para>
/// <para>
/// <b>The audit row is not written here.</b> D-35's immutable admin log is <c>audit.events</c> and
/// admin-bff owns it (C065): every one of these calls arrives through that BFF, which is where the
/// actor, the before-image and the after-image are recorded for the whole Admin Portal. Writing a
/// second audit row from this service would double-count every edit and leave the two copies to
/// disagree. What this service does instead is log the actor and the version at information level,
/// and keep the version history queryable — which is the after-image, permanently.
/// </para>
/// </remarks>
public static class AdminContentEndpoints
{
    public static IEndpointRouteBuilder MapAdminContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var admin = endpoints.MapGroup("/v1/admin/content")
            .WithTags("content")
            .RequireMageRideRole(MageRideRoles.Admin, MageRideRoles.SuperAdmin);

        // Literal before template, and the two do not collide: `broadcasts` is a POST on a literal
        // segment, the template routes are PUT/GET/POST on `{key}`. `ContentPublisher` refuses
        // `broadcasts`, `faq` and `cache` as template keys anyway, so no key can shadow a route.
        admin.MapPost("/broadcasts", PublishBroadcastAsync).WithName("publishBroadcast");

        var key = $"/{{key:regex({TemplateKeys.RoutePattern})}}";

        admin.MapGet(key, GetHistoryAsync).WithName("listNotificationTemplateVersions");
        admin.MapPut(key, UpdateTemplateAsync).WithName("updateNotificationTemplate");
        admin.MapPost($"{key}/approve", ApproveTemplateAsync).WithName("approveNotificationTemplate");

        return endpoints;
    }

    private static async Task<Ok<TemplateVersionRefResponse>> UpdateTemplateAsync(
        string key,
        UpdateTemplateBody? body,
        HttpContext context,
        ContentPublisher publisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publisher);

        return TypedResults.Ok(
            await publisher.DraftTemplateAsync(
                key, body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<Ok<TemplateVersionRefResponse>> ApproveTemplateAsync(
        string key,
        ApproveTemplateBody? body,
        HttpContext context,
        ContentPublisher publisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publisher);

        return TypedResults.Ok(
            await publisher.ApproveTemplateAsync(
                key, body, context.User.RequireSubjectId(), cancellationToken));
    }

    private static async Task<Created<BroadcastResponse>> PublishBroadcastAsync(
        PublishBroadcastBody? body,
        HttpContext context,
        ContentPublisher publisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publisher);

        var broadcast = await publisher.PublishBroadcastAsync(
            body, context.User.RequireSubjectId(), cancellationToken);

        // Location points at the read surface the announcement will appear on, which is the only
        // place a client can see it — there is no GET for one broadcast by id, because a banner is a
        // list.
        return TypedResults.Created("/v1/content/broadcasts", broadcast);
    }

    /// <summary>
    /// The version history, grouped one entry per version with its three languages together.
    /// </summary>
    /// <remarks>
    /// Grouped rather than returned row-per-language, because a version is the unit an admin approves
    /// and three rows that have to be reassembled by the portal is three chances to display a
    /// partial one. Newest first: the reason to open this screen is almost always the draft that was
    /// just written.
    /// </remarks>
    private static async Task<Ok<TemplateHistoryResponse>> GetHistoryAsync(
        string key,
        ITemplateRepository templates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var rows = await templates.ReadHistoryAsync(key, cancellationToken);

        if (rows.Count == 0)
        {
            throw new MageRideException(MageRideErrors.NotFound, $"No template '{key}'.");
        }

        var versions = rows
            .GroupBy(static row => row.Version)
            .OrderByDescending(static group => group.Key)
            .Select(static group =>
            {
                var byLanguage = group.ToDictionary(
                    static row => row.Language, static row => row, StringComparer.Ordinal);

                var head = group.First();

                var bodies = new TrilingualTextBody(
                    Body(byLanguage, Languages.Sinhala),
                    Body(byLanguage, Languages.Tamil),
                    Body(byLanguage, Languages.English));

                var titles = group.Any(static row => !string.IsNullOrWhiteSpace(row.Subject))
                    ? new TrilingualTextBody(
                        Subject(byLanguage, Languages.Sinhala),
                        Subject(byLanguage, Languages.Tamil),
                        Subject(byLanguage, Languages.English))
                    : null;

                // Subject *and* body, in that order, so this agrees with what the render path reports
                // for the same version — a template whose title interpolates a variable its body does
                // not would otherwise be described differently by the two surfaces.
                var placeholders = TemplatePlaceholders
                    .Extract(Subject(byLanguage, Languages.English))
                    .Concat(TemplatePlaceholders.Extract(Body(byLanguage, Languages.English)))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return new TemplateVersionResponse(
                    group.Key,
                    head.Status,
                    titles,
                    bodies,
                    placeholders,
                    head.ApprovedAt,
                    head.ApprovedBy,
                    head.CreatedAt);
            })
            .ToArray();

        var current = versions
            .Where(static version => version.Status == TemplateStatuses.Published)
            .Select(static version => (int?)version.Version)
            .FirstOrDefault();

        return TypedResults.Ok(new TemplateHistoryResponse(key, current, versions));
    }

    private static string? Body(Dictionary<string, TemplateHistoryRow> rows, string language) =>
        rows.TryGetValue(language, out var row) ? row.Body : null;

    private static string? Subject(Dictionary<string, TemplateHistoryRow> rows, string language) =>
        rows.TryGetValue(language, out var row) ? row.Subject : null;
}
