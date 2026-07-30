using MageRide.Content.Caching;
using MageRide.Content.Configuration;
using MageRide.Content.Domain;
using MageRide.Content.Endpoints;
using MageRide.Content.Persistence;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MageRide.Content.Publishing;

/// <summary>
/// The admin write paths: draft a template version, approve one, publish a broadcast.
/// </summary>
/// <remarks>
/// <para>
/// Every method here ends with an invalidation, and that is the "invalidation path on publish" the
/// C045 deliverable asks for. It is deliberately the <b>last</b> thing each does: purging before the
/// commit would leave every replica reloading the old row and caching it for another TTL, which is
/// worse than not purging at all.
/// </para>
/// <para>
/// Validation is up front and total. A template that reaches the repository has three non-blank
/// bodies whose placeholder sets agree; a broadcast has three non-blank messages, a window that runs
/// forwards and an audience selector this platform can evaluate. The database repeats the
/// trilingual rule (1307's trigger, <c>ck_broadcasts_trilingual</c>) and the window rule
/// (<c>ck_broadcasts_window</c>) because this service is not the only writer those tables can have —
/// but a constraint violation is a 500, and the definition of done asks for a *clear error*, so the
/// field-level rejection lives here.
/// </para>
/// </remarks>
internal sealed class ContentPublisher(
    ITemplateRepository templates,
    IBroadcastRepository broadcasts,
    IContentInvalidator invalidator,
    IOptions<ContentOptions> options,
    TimeProvider clock,
    ILogger<ContentPublisher> logger)
{
    /// <summary>
    /// Template keys the admin routes cannot address, because a literal path segment already means
    /// something else there.
    /// </summary>
    /// <remarks>
    /// <c>PUT /v1/admin/content/broadcasts</c> would be a template called "broadcasts" sitting beside
    /// <c>POST /v1/admin/content/broadcasts</c>, and a reader of either the routes or the audit log
    /// would have to know which. Refused with a reason rather than silently shadowed.
    /// </remarks>
    private static readonly string[] ReservedKeys = ["broadcasts", "faq", "cache"];

    /// <summary>
    /// The four sign-in surfaces (AL-07), as <c>content.yaml</c>'s <c>BroadcastAudience.app</c> enum
    /// lists them.
    /// </summary>
    /// <remarks>
    /// Spelled from the kernel's constants rather than as literals. <c>MageRideApps</c> exposes the
    /// two apps and the two portals as separate sets and no union, because every other caller wants
    /// one or the other; a broadcast can target any of the four.
    /// </remarks>
    private static readonly string[] KnownApps =
        [MageRideApps.Passenger, MageRideApps.Driver, MageRideApps.Admin, MageRideApps.Fleet];

    private readonly ContentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Creates version <c>n+1</c> of an existing template, drafted or published per configuration.
    /// </summary>
    /// <remarks>
    /// <b>An unknown key is a 404 and not a new template</b>, which is what the <c>404</c> on D3''s
    /// edit route means. A template key is only content if some service renders it — C005's own note
    /// on the four seeded keys says "inventing further keys would put strings in the database that no
    /// service resolves" — so a new key ships in a migration beside the code that sends it, and this
    /// route edits the *wording* of one that exists. An admin who could coin keys would be able to
    /// write copy nothing ever sends and see no error anywhere.
    /// </remarks>
    public async Task<TemplateVersionRefResponse> DraftTemplateAsync(
        string key, UpdateTemplateBody? request, Guid author, CancellationToken cancellationToken)
    {
        var templateKey = RequireKey(key);

        if (!await templates.ExistsAsync(templateKey, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.NotFound,
                $"No template '{templateKey}'. This route edits the wording of an existing key; a new "
                + "template key is added by a migration alongside the service that renders it.");
        }

        var body = TrilingualText.Require(request?.BodyByLang?.ToMap(), "bodyByLang");
        TemplatePlaceholders.RequireConsistent(body, "bodyByLang");

        // A title is optional as a whole and trilingual if present. Half a title is the one shape
        // that must not pass: a push notification with a headline in two languages and none in the
        // third is exactly what D-26 exists to prevent, and it is the easiest mistake to make in a
        // form where the field is optional.
        TrilingualText? title = null;

        if (request?.TitleByLang is { } supplied && !IsEmpty(supplied))
        {
            title = TrilingualText.Require(supplied.ToMap(), "titleByLang");
            TemplatePlaceholders.RequireConsistent(title, "titleByLang");
        }

        var outcome = await templates.InsertVersionAsync(
            templateKey, body, title, author, _options.PublishOnEdit, cancellationToken);

        var status = _options.PublishOnEdit ? TemplateStatuses.Published : TemplateStatuses.Draft;

        logger.LogInformation(
            "Template {Key} version {Version} written as {Status} by {Author}.",
            templateKey,
            outcome.Version,
            status,
            author);

        // Only a publish changes what the render path serves, so only a publish purges. A draft that
        // purged would throw away every replica's cache to change nothing.
        if (_options.PublishOnEdit)
        {
            await invalidator.InvalidateAsync([ContentDatasets.Templates], cancellationToken);
        }

        return new TemplateVersionRefResponse(
            templateKey,
            outcome.Version,
            status,
            _options.PublishOnEdit ? outcome.ApprovedAt : null);
    }

    /// <summary>Publishes a drafted version and makes it current.</summary>
    public async Task<TemplateVersionRefResponse> ApproveTemplateAsync(
        string key, ApproveTemplateBody? request, Guid approver, CancellationToken cancellationToken)
    {
        var templateKey = RequireKey(key);

        if (request?.Version is not { } version || version < 1)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["version"] = ["version is required and is the version number to publish."],
            });
        }

        var outcome = await templates.ApproveAsync(templateKey, version, approver, cancellationToken);

        if (outcome is null)
        {
            // Nothing moved, and the two reasons are different answers: a version that is already
            // published is a 409 (somebody approved it first — one approver per version), a version
            // that does not exist is a 404.
            var status = await templates.ReadVersionStatusAsync(templateKey, version, cancellationToken);

            throw status is null
                ? new MageRideException(
                    MageRideErrors.NotFound,
                    $"'{templateKey}' has no version {version}.")
                : new MageRideException(
                    MageRideErrors.Conflict,
                    $"Version {version} of '{templateKey}' is already {status}; only a draft can be published.");
        }

        logger.LogInformation(
            "Template {Key} version {Version} published by {Approver} ({Rows} languages).",
            templateKey,
            version,
            approver,
            outcome.Rows);

        await invalidator.InvalidateAsync([ContentDatasets.Templates], cancellationToken);

        return new TemplateVersionRefResponse(
            templateKey, version, TemplateStatuses.Published, outcome.ApprovedAt);
    }

    /// <summary>Publishes an in-app announcement (US-14.8).</summary>
    public async Task<BroadcastResponse> PublishBroadcastAsync(
        PublishBroadcastBody? request, Guid author, CancellationToken cancellationToken)
    {
        var message = TrilingualText.Require(request?.MessageByLang?.ToMap(), "messageByLang");

        var startsAt = request?.StartsAt ?? clock.GetUtcNow();
        var endsAt = request?.EndsAt;

        if (endsAt is { } end && end <= startsAt)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["endsAt"] = ["endsAt must be after startsAt. A window that runs backwards shows nothing."],
            });
        }

        var audience = RequireAudience(request?.Audience);

        var row = await broadcasts.InsertAsync(message, audience, startsAt, endsAt, author, cancellationToken);

        logger.LogInformation(
            "Broadcast {BroadcastId} published by {Author} for {Audience}, live {StartsAt} to {EndsAt}.",
            row.Id,
            author,
            audience.IsEveryone ? "everybody" : $"role={audience.Role ?? "*"} app={audience.App ?? "*"}",
            startsAt,
            endsAt);

        await invalidator.InvalidateAsync([ContentDatasets.Broadcasts], cancellationToken);

        return new BroadcastResponse(
            row.Id,
            message[Languages.English],
            row.StartsAt,
            row.EndsAt,
            audience.IsEveryone ? null : new BroadcastAudienceBody(audience.Role, audience.App));
    }

    /// <summary>
    /// Validates the audience selector: only <c>role</c> and <c>app</c>, and only real values.
    /// </summary>
    /// <remarks>
    /// A role or app that does not exist is refused rather than stored. Stored, it would target
    /// nobody — a banner an admin believes is live and no user can see, with nothing anywhere saying
    /// why.
    /// </remarks>
    private static BroadcastAudience RequireAudience(BroadcastAudienceBody? audience)
    {
        if (audience is null || (audience.Role is null && audience.App is null))
        {
            return BroadcastAudience.Everyone;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var role = Trim(audience.Role);
        var app = Trim(audience.App);

        if (role is not null && !MageRideRoles.IsKnown(role))
        {
            errors["audience.role"] =
                [$"'{role}' is not one of the nine canonical roles (AL-06): {string.Join(", ", MageRideRoles.All)}."];
        }

        if (app is not null && Array.IndexOf(KnownApps, app) < 0)
        {
            errors["audience.app"] =
                [$"'{app}' is not a sign-in surface: {string.Join(", ", KnownApps)}."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(
                errors,
                "The audience selector must be evaluable against a bearer token, or the announcement "
                + "would be published to an audience nothing can match.");
        }

        return new BroadcastAudience(role, app);
    }

    private static string RequireKey(string? key)
    {
        var trimmed = key?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["key"] = ["A template key is required."],
            });
        }

        if (Array.IndexOf(ReservedKeys, trimmed) >= 0)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["key"] = [$"'{trimmed}' is reserved: /v1/admin/content/{trimmed} already names another surface."],
                });
        }

        // The contract's own pattern. Enforced here as well as in the route constraint so a caller
        // reaching this service directly cannot create a key the gateway's clients could not address.
        if (!TemplateKeys.IsValid(trimmed))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["key"] = ["A template key is lower-case alphanumerics separated by single underscores, e.g. ride_offer."],
            });
        }

        return trimmed;
    }

    private static bool IsEmpty(TrilingualTextBody body) =>
        string.IsNullOrWhiteSpace(body.Si)
        && string.IsNullOrWhiteSpace(body.Ta)
        && string.IsNullOrWhiteSpace(body.En);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
