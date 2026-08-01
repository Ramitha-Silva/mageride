using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// <c>POST /v1/admin/announcements</c> — the broadcast banner and its push (US-14.8, D-26).
/// </summary>
/// <remarks>
/// <para>
/// <b>Forwarded to content-svc, which owns <c>content.broadcasts</c>.</b> The table's
/// <c>ck_broadcasts_trilingual</c> constraint, the audience selector notification-svc interprets
/// and the <c>GET /v1/content/broadcasts</c> read surface are all C054's, and it already exposes
/// <c>POST /v1/admin/content/broadcasts</c>. admin-bff owns a second spelling of the same operation
/// because <c>admin-bff.yaml</c> declares it — so it supplies the RBAC gate and the D-35 row and
/// forwards the write rather than becoming a second author of the same table.
/// </para>
/// <para>
/// <b>The caller's own bearer is forwarded</b>, because content-svc's route is role-gated
/// (Admin, Super Admin) rather than internal-key-gated. Sending the shared key instead would be a
/// bypass of a check that exists.
/// </para>
/// <para>
/// <b>All three languages are validated here as well as there.</b> The constraint is the
/// guarantee; this is so the operator gets a named field back instead of a 500 with a constraint
/// name in it, and so the D-35 audit row is never written for a request that was going to be
/// refused downstream.
/// </para>
/// </remarks>
internal static class AnnouncementEndpoints
{
    /// <summary>The three languages every user-facing string ships in (D-26).</summary>
    private static readonly string[] Languages = ["si", "ta", "en"];

    public static IEndpointRouteBuilder MapAnnouncementEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapPost("/announcements", PublishAsync)
            .WithName("publishAnnouncement")
            .WithSummary("Publish a broadcast announcement in all three languages (US-14.8).")
            .RequireFeature(FeatureAreas.Announcements, PermissionGrant.Write)
            .Audited(AdminAuditActions.AnnouncementPublished, AdminAuditActions.BroadcastEntity);

        return admin;
    }

    private static async Task<Created<AnnouncementResponse>> PublishAsync(
        PublishAnnouncementBody? body,
        HttpContext context,
        IAdminUpstream upstream,
        IAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(audit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var messages = body?.MessageByLang;

        foreach (var language in Languages)
        {
            if (messages is null || !messages.TryGetValue(language, out var text) || string.IsNullOrWhiteSpace(text))
            {
                errors[$"messageByLang.{language}"] =
                    [$"messageByLang.{language} is required: no user-facing string ships in fewer than three languages (D-26)."];
            }
        }

        if (body?.StartsAt is null)
        {
            errors["startsAt"] = ["startsAt is required."];
        }

        if (body?.EndsAt is { } ends && body.StartsAt is { } starts && ends <= starts)
        {
            errors["endsAt"] = ["endsAt must be after startsAt."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        using var request = upstream.Request(
            AdminUpstreams.Content, HttpMethod.Post, "/v1/admin/content/broadcasts");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new
            {
                messageByLang = messages,
                scheduledAt = body!.StartsAt,
                endsAt = body.EndsAt,
                push = body.Push ?? false,
            },
            options: MageRideJson.Options);

        var published = await upstream.SendAsync<ContentBroadcastResponse>(
            AdminUpstreams.Content, request, context, cancellationToken);

        var broadcastId = published.BroadcastId ?? published.Id
            ?? throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "content-svc published the broadcast but returned no id.");

        audit.Record(
            broadcastId,
            after: new { startsAt = body.StartsAt, endsAt = body.EndsAt, push = body.Push ?? false, languages = Languages });

        return TypedResults.Created("/v1/content/broadcasts", new AnnouncementResponse(broadcastId));
    }

    /// <remarks>
    /// Both spellings are accepted for the same reason the ticket mapping accepts two: this response
    /// belongs to another component's contract, and a mapping that breaks when it renames one field
    /// is a mapping that will break.
    /// </remarks>
    private sealed record ContentBroadcastResponse(Guid? BroadcastId, Guid? Id);
}
