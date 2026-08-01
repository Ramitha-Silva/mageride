using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Auth;
using MageRide.Shared.Http.Idempotency;
using Microsoft.Net.Http.Headers;

namespace MageRide.AdminBff.Endpoints;

/// <summary>
/// <c>/v1/admin/transit/gtfs/**</c> — the GTFS Dataset Manager, in the Configuration nav group
/// (SCR-AP-016, AL-54).
/// </summary>
/// <remarks>
/// <para>
/// <b>A pass-through, because the lifecycle belongs with the tables it swaps.</b> transit-svc
/// (C057) owns the upload, the validation, the staging load and the one transaction that renames a
/// schema into place, and its own file says "everything here is transit-svc's, and admin-bff
/// proxies it". What this adds is the Admin Portal's front door: one origin for the console, the
/// same AL-06 gate as every other Configuration screen, and a D-35 row saying an operator reached
/// for the dataset manager.
/// </para>
/// <para>
/// <b>In the deployed topology it is shadowed, and that is correct.</b> `gateway-routes.json` sends
/// <c>/v1/admin/transit/**</c> straight to transit-svc at Order 20 — ahead of the Order 90 admin-bff
/// catch-all — because a second hop through this process would double the transfer of a 200 MB feed
/// for nothing. This copy exists so a deployment that talks to admin-bff directly still has a whole
/// Configuration group, and so the RBAC matrix and the audit guard cover the path either way.
/// </para>
/// <para>
/// <b>The caller's own bearer is forwarded and transit-svc re-checks it.</b> Its group is
/// <c>RequireMageRideRole(Admin, SuperAdmin)</c>; the gate here is Platform settings · Configure,
/// which resolves to the same two roles through URD §2.3 rather than by naming them twice.
/// </para>
/// <para>
/// <b>Body and response are streamed.</b> A feed is up to 200 MB (BR-32.1) and buffering it in this
/// process would put the whole file in memory twice — once inbound, once outbound — on a route
/// whose only job is to carry it.
/// </para>
/// </remarks>
internal static class GtfsProxyEndpoints
{
    /// <summary>
    /// Hop-by-hop and content-negotiation headers that must not be copied.
    /// </summary>
    /// <remarks>
    /// <c>Content-Length</c> and <c>Transfer-Encoding</c> are re-derived by the server writing the
    /// response, and copying either can leave a body whose framing disagrees with its header — the
    /// classic proxy failure. <c>Connection</c> and its friends are per-hop by definition (RFC 9110).
    /// </remarks>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Connection,
        HeaderNames.TransferEncoding,
        HeaderNames.KeepAlive,
        HeaderNames.Upgrade,
        HeaderNames.ProxyAuthenticate,
        HeaderNames.ProxyAuthorization,
        HeaderNames.TE,
        HeaderNames.Trailer,
        HeaderNames.ContentLength,
    };

    public static IEndpointRouteBuilder MapGtfsProxyEndpoints(this IEndpointRouteBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        // One route for the whole family: this component does not model the GTFS lifecycle, and a
        // per-operation copy here would be a second, silently drifting description of C057's
        // contract. `transit.yaml` remains the only one.
        admin.MapMethods(
                "/transit/gtfs/{**path}",
                [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete],
                ForwardAsync)
            .WithName("proxyGtfsDatasetManager")
            .WithSummary("Pass-through to transit-svc's GTFS Dataset Manager (AL-54, SCR-AP-016).")
            .RequireFeature(FeatureAreas.PlatformSettings, PermissionGrant.Configure)
            .Audited(AdminAuditActions.GtfsProxied, AdminAuditActions.GtfsEntity)
            // The body is a feed, not a form, and the kernel's replay would buffer 200 MB to hash
            // it — the same argument C057 makes on the upload it forwards to.
            .AllowMissingIdempotencyKey()
            .DisableAntiforgery();

        return admin;
    }

    private static async Task ForwardAsync(
        string? path,
        HttpContext context,
        IAdminUpstream upstream,
        IAdminAuditContext audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(audit);

        var target = $"/v1/admin/transit/gtfs/{path}{context.Request.QueryString}";

        using var request = upstream.Request(
            AdminUpstreams.Transit, new HttpMethod(context.Request.Method), target);

        // Only where there is one. A GET carries no body, and attaching an empty StreamContent to
        // it would send `Content-Length: 0` on a request that should have no entity at all.
        if (context.Request.ContentLength is > 0 ||
            (context.Request.ContentLength is null && context.Request.Headers.TransferEncoding.Count > 0))
        {
            request.Content = new StreamContent(context.Request.Body);

            if (context.Request.ContentType is { Length: > 0 } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
            }
        }

        using var response = await upstream.SendRawAsync(
            AdminUpstreams.Transit, request, context, cancellationToken);

        // Mutations only, and recorded before the body is streamed so the row exists even if the
        // client disconnects halfway through a 200 MB download. Reads are deliberately not audited
        // here: SCR-AP-016's status stepper polls `…/uploads/{id}` every few seconds while a feed
        // validates, and a row per poll would bury the activations somebody actually wants to find.
        // The entity is the feed version when the path names one.
        if (AuditInterceptor.IsMutating(context.Request))
        {
            audit.Record(
                FeedVersionOf(path),
                after: new { method = context.Request.Method, path = target, status = (int)response.StatusCode });
        }

        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(name))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }

        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    /// <summary>
    /// The feed version id out of the forwarded path, when the path names one.
    /// </summary>
    /// <remarks>
    /// The audit row is more useful with an entity than without, and every mutating route in C057's
    /// family carries the id in its path (<c>uploads/{id}/activate</c>, <c>versions/{id}/download</c>).
    /// The one that does not is the upload itself, whose id does not exist until transit-svc mints
    /// it — the dataset-level row transit-svc writes inside its own transaction carries that.
    /// </remarks>
    private static Guid? FeedVersionOf(string? path) =>
        path?.Split('/').Select(static segment => Guid.TryParse(segment, out var id) ? id : (Guid?)null)
            .FirstOrDefault(static id => id is not null);
}
