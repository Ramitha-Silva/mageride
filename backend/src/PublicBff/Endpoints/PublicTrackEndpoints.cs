using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Domain;
using MageRide.PublicBff.Persistence;
using MageRide.PublicBff.Tracking;
using MageRide.PublicBff.Upstream;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Endpoints;

/// <summary>
/// Marks a route as belonging to the token-scoped public surface.
/// </summary>
/// <remarks>
/// Read by <c>PublicBffApplication.GuardTheSurface</c>: a route mapped outside this group has not
/// been through the token gate, and the guard refuses to start rather than let one exist.
/// </remarks>
public sealed class PublicSurfaceMarker;

/// <summary>
/// <c>/public/track/{token}</c> and its five siblings — the whole of public-bff's surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>The path prefix is <c>/public</c> and not <c>/v1</c>, deliberately.</b> The family is
/// versioned by share-token scope rather than by a path segment: a token minted under one scope
/// vocabulary keeps meaning what it meant, and a page reached from an SMS sent months ago has no way
/// to be told to use a different URL. <c>.spectral.yaml</c> admits the two prefixes for exactly this
/// reason and the C008 gateway already forwards <c>/public/{**remainder}</c> here.
/// </para>
/// <para>
/// <b>There is no <c>POST /public/track/{token}/call</c> and there must never be again.</b> AL-48
/// removed the ride-scoped proxy-DID lease, the CPaaS bridge and the confirm-your-number step in
/// full; the snapshot carries <c>driver.phone</c> and SCR-WT-002/004 dial it with a plain
/// <c>tel:</c> link (US-26.3). <c>FenceTests</c> asserts the absence against the running route table
/// so an earlier-dated spec line cannot bring it back.
/// </para>
/// </remarks>
public static class PublicTrackEndpoints
{
    /// <summary>The one prefix this service serves (AL-44).</summary>
    public const string Prefix = "/public/track";

    public static IEndpointRouteBuilder MapPublicTrackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // **AllowAnonymous on the group, not per route.** The token in the path is the credential
        // (D-34, P-09) and there is no authentication scheme registered in this process at all, so
        // the kernel's deny-by-default fallback policy would otherwise 401 every one of the six SCR-WT
        // pages. Stated once, where it is a property of the surface rather than of a handler.
        var track = endpoints.MapGroup(Prefix)
            .WithTags("public-track")
            .AllowAnonymous()
            .WithMetadata(new PublicSurfaceMarker());

        track.MapGet("/{token}", SnapshotAsync).WithName("getPublicTrackSnapshot");
        track.MapGet("/{token}/live", LiveAsync).WithName("streamPublicTrack");

        track.MapPost("/{token}/pickup/confirm", ConfirmPickupAsync).WithName("confirmPublicPickup");
        track.MapPost("/{token}/pickup/decline", DeclinePickupAsync).WithName("declinePublicPickup");

        track.MapPost("/{token}/sos", SosAsync).WithName("raisePublicSos");

        track.MapGet("/{token}/receipt", ReceiptAsync).WithName("getPublicReceipt");

        return endpoints;
    }

    // -----------------------------------------------------------------------------------------
    // The snapshot (SCR-WT-001 → 002 / 003 / 004)
    // -----------------------------------------------------------------------------------------

    private static async Task<IResult> SnapshotAsync(
        string token,
        HttpContext context,
        ITrackTokenGate gate,
        ITrackService tracking,
        IOptions<PublicBffOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(options);

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);

        // `Results.Json` rather than a typed result: the contract's 200 is a `oneOf` over three
        // shapes discriminated by `kind`, and the scope decides which — the client cannot ask.
        return Results.Json(
            await tracking.SnapshotAsync(share, cancellationToken), MageRideJson.Options, statusCode: 200);
    }

    // -----------------------------------------------------------------------------------------
    // The live feed
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// SSE, or a JSON batch when the client asks with <c>?since</c>.
    /// </summary>
    /// <remarks>
    /// <b>The presence of <c>since</c> selects the transport, which is what the contract says</b>
    /// ("a client that cannot hold an SSE connection open falls back to polling with
    /// <c>?since=&lt;cursor&gt;</c>"). A browser's <c>EventSource</c> sends <c>Last-Event-ID</c>
    /// instead and stays on the stream: same cursor, same resume, no query parameter.
    /// </remarks>
    private static async Task<IResult> LiveAsync(
        string token,
        string? since,
        HttpContext context,
        ITrackTokenGate gate,
        ITrackStream stream,
        IOptions<PublicBffOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);

        if (since is not null)
        {
            var delta = await stream.PollAsync(share, TrackCursor.Parse(since), cancellationToken);

            return Results.Json(
                new TrackEventBatchResponse(delta.Events, delta.Cursor.ToString()),
                MageRideJson.Options,
                statusCode: 200);
        }

        var resume = TrackCursor.Parse(context.Request.Headers["Last-Event-ID"].ToString());

        await stream.StreamAsync(context, share, resume, cancellationToken);

        // The body is already written and flushed; nothing further may touch the response.
        return Results.Empty;
    }

    // -----------------------------------------------------------------------------------------
    // The pickup pair (SCR-WT-003, AL-45)
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<PickupResolutionResponse>> ConfirmPickupAsync(
        string token,
        GeoPointWithAccuracyBody? body,
        HttpContext context,
        ITrackTokenGate gate,
        ITrackService tracking,
        IShareTokenRepository tokens,
        IRideClient rides,
        IOptions<PublicBffOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(rides);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (body?.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat must be a latitude between -90 and 90."];
        }

        if (body?.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng must be a longitude between -180 and 180."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);
        var request = await RequirePickupScopeAsync(share, tracking, cancellationToken);

        // **The token is burned before the coordinate is forwarded.** BR-29.1 makes a
        // `pickup_confirm` token single-use, and the order is what makes it so under a double tap:
        // the loser of the burn never reaches ride-svc. It is also the safe order to fail in — a
        // burn followed by an unreachable ride-svc costs the rider a retry through the app path,
        // whereas the reverse would leave a live token on an answered request.
        if (!await tokens.BurnAsync(share.Token, clock.GetUtcNow(), cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This request has already been answered.");
        }

        var resolved = await rides.ConfirmAsync(
            request.RequestId,
            body!.Lat!.Value,
            body.Lng!.Value,
            body.Accuracy,
            IdempotencyKeyOf(context, PublicIdempotency.ForPickup(share.Token, "confirm")),
            cancellationToken);

        return TypedResults.Ok(new PickupResolutionResponse(resolved.State));
    }

    /// <summary>
    /// SCR-WT-003's Decline.
    /// </summary>
    /// <remarks>
    /// <b>No body parameter, and that is P-02's fence in this component.</b> The handler cannot read
    /// a coordinate because it takes none; <see cref="IRideClient.DeclineAsync"/> sends no content;
    /// and ride-svc's statement has no <c>resolved_geo</c> in its <c>SET</c> list. Three components,
    /// three properties of the code, and no reviewer has to remember.
    /// </remarks>
    private static async Task<Ok<PickupResolutionResponse>> DeclinePickupAsync(
        string token,
        HttpContext context,
        ITrackTokenGate gate,
        ITrackService tracking,
        IShareTokenRepository tokens,
        IRideClient rides,
        IOptions<PublicBffOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(tracking);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(rides);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);
        var request = await RequirePickupScopeAsync(share, tracking, cancellationToken);

        if (!await tokens.BurnAsync(share.Token, clock.GetUtcNow(), cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This request has already been answered.");
        }

        var resolved = await rides.DeclineAsync(
            request.RequestId,
            IdempotencyKeyOf(context, PublicIdempotency.ForPickup(share.Token, "decline")),
            cancellationToken);

        return TypedResults.Ok(new PickupResolutionResponse(resolved.State));
    }

    // -----------------------------------------------------------------------------------------
    // Web SOS (SCR-WT-004, US-25.5)
    // -----------------------------------------------------------------------------------------

    private static async Task<Accepted<PublicSosResponse>> SosAsync(
        string token,
        GeoPointWithAccuracyBody? body,
        HttpContext context,
        ITrackTokenGate gate,
        ISafetyClient safety,
        IOptions<PublicBffOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // The browser's Geolocation API supplies these (D6' I-29.4). There is no server-side
        // fallback to the driver's last known position here: safety-svc's `sos_events.lat/lng` are
        // NOT NULL and the row must say where the *person* said they were, not where the car was.
        if (body?.Lat is not { } lat || lat is < -90 or > 90)
        {
            errors["lat"] = ["lat must be a latitude between -90 and 90."];
        }

        if (body?.Lng is not { } lng || lng is < -180 or > 180)
        {
            errors["lng"] = ["lng must be a longitude between -180 and 180."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);

        var raised = await safety.RaiseWebSosAsync(
            share.Token,
            body!.Lat!.Value,
            body.Lng!.Value,
            IdempotencyKeyOf(
                context,
                PublicIdempotency.ForSos(share.Token, clock.GetUtcNow(), options.Value.SosDedupeWindow)),
            cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new PublicSosResponse(raised.SosId, raised.DispatchedAt, raised.SmsStatus));
    }

    // -----------------------------------------------------------------------------------------
    // Receipt (SCR-WT-005)
    // -----------------------------------------------------------------------------------------

    private static async Task<Ok<ReceiptResponse>> ReceiptAsync(
        string token,
        HttpContext context,
        ITrackTokenGate gate,
        IReceiptService receipts,
        IOptions<PublicBffOptions> options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(options);

        var share = await gate.RedeemAsync(token, ClientIpOf(context, options.Value), cancellationToken);

        return TypedResults.Ok(await receipts.BuildAsync(share, cancellationToken));
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The pickup pair is <c>pickup_confirm</c> scope only — any other token is <c>403</c>.
    /// </summary>
    /// <remarks>
    /// A 403 rather than the family's 404, and the contract says so: the caller holds a token this
    /// surface recognises and is being told this particular door is not theirs. There is no oracle
    /// in that — they already know their link is live, because the snapshot answered.
    /// </remarks>
    private static async Task<TrackedPickupRequest> RequirePickupScopeAsync(
        ShareToken share, ITrackService tracking, CancellationToken cancellationToken)
    {
        if (share.Scope is not ShareTokenScopes.PickupConfirm)
        {
            throw new MageRideException(
                MageRideErrors.Forbidden, "This link cannot answer a pickup request.");
        }

        return await tracking.RequirePickupRequestAsync(share, cancellationToken);
    }

    /// <summary>
    /// The key forwarded to whichever service owns the write.
    /// </summary>
    /// <remarks>
    /// The caller's header when it is well formed, and <paramref name="derived"/> otherwise. Not
    /// validated into a 400: <c>public-bff.yaml</c> marks the header required, but this service owns
    /// no command log to replay from, so a page that omits it loses nothing except its own retry
    /// safety — and refusing a panic button for a missing header would be the worst possible trade.
    /// The derived key is what keeps the retry safe anyway.
    /// </remarks>
    private static string IdempotencyKeyOf(HttpContext context, string derived)
    {
        var presented = context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString().Trim();

        return presented.Length is >= 16 and <= 128 ? presented : derived;
    }

    /// <summary>
    /// Who the per-IP bucket counts.
    /// </summary>
    /// <remarks>
    /// Every request arrives through the C008 gateway, so the socket's peer is the gateway and
    /// counting it would rate-limit the whole internet as one client.
    /// <c>PublicBff:TrustForwardedFor</c> exists so a deployment that does not front this service
    /// with a trusted proxy can turn the trust off — and then the peer address is the honest answer.
    /// </remarks>
    internal static string? ClientIpOf(HttpContext context, PublicBffOptions options)
    {
        if (options.TrustForwardedFor
            && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            // The left-most entry is the originating client; everything after it is a proxy chain.
            var first = forwarded.Split(',')[0].Trim();

            if (first.Length > 0)
            {
                return first;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}
