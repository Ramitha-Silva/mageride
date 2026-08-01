using System.Text.Json;
using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Domain;
using MageRide.PublicBff.Endpoints;
using MageRide.PublicBff.Persistence;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Tracking;

/// <summary>What one poll or one tick of the stream found.</summary>
/// <param name="Closed">
/// There will be nothing more: the journey is over, the request has been answered, or the token
/// died while somebody was watching.
/// </param>
public sealed record TrackDelta(IReadOnlyList<TrackEventResponse> Events, TrackCursor Cursor, bool Closed);

/// <summary>
/// <c>GET /public/track/{token}/live</c> — SSE, and the same answer as a JSON batch for a client
/// that cannot hold a socket open.
/// </summary>
/// <remarks>
/// <para>
/// <b>One diff function, two transports.</b> D6' I-29.1 asks for SSE with a long-poll fallback, and
/// the failure mode of building those separately is a page that behaves differently on a bad
/// connection — which is the connection the fallback exists for. <see cref="PollAsync"/> is one
/// evaluation of the diff and <see cref="StreamAsync"/> is the same evaluation on a timer, so the
/// two cannot disagree about what an event is.
/// </para>
/// <para>
/// <b>The stream re-reads the token on every tick, and that is the only thing that closes it.</b>
/// A no-login page has no session to expire: without the re-read, a link revoked because the trip
/// ended would keep feeding positions to whoever left the tab open. safety-svc's trip-end hook
/// revokes the row; this notices within one tick and sends the client to SCR-WT-006.
/// </para>
/// <para>
/// <b>Nothing is buffered, so nothing can be replayed.</b> See <see cref="TrackCursor"/>.
/// </para>
/// </remarks>
public interface ITrackStream
{
    /// <summary>The poll fallback: what changed since <paramref name="since"/>, and the new cursor.</summary>
    Task<TrackDelta> PollAsync(ShareToken share, TrackCursor since, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <c>text/event-stream</c> frames until the token dies, the journey ends or
    /// <see cref="PublicBffOptions.StreamMaxDuration"/> elapses.
    /// </summary>
    Task StreamAsync(HttpContext context, ShareToken share, TrackCursor since, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackStream"/>
internal sealed class TrackStream(
    ITrackService tracking,
    ITrackTokenGate gate,
    ITrackReadRepository rides,
    IOptions<PublicBffOptions> options,
    TimeProvider clock,
    ILogger<TrackStream> logger) : ITrackStream
{
    /// <summary>`public-bff.yaml#TrackEvent.type`.</summary>
    private const string PositionEvent = "position";

    private const string StatusEvent = "status";

    private const string ResolvedEvent = "resolved";

    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<TrackDelta> PollAsync(
        ShareToken share, TrackCursor since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);

        return share.Scope is ShareTokenScopes.PickupConfirm
            ? await PickupDeltaAsync(share, since, cancellationToken)
            : await RideDeltaAsync(share, since, cancellationToken);
    }

    public async Task StreamAsync(
        HttpContext context, ShareToken share, TrackCursor since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(share);

        var response = context.Response;

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers.Connection = "keep-alive";

        // Reverse proxies buffer by default and a buffered SSE stream arrives all at once when it
        // ends, which is the one thing a live feed must not do. The header is nginx's and the
        // feature call is Kestrel's own; C008's gateway pins HTTP/1.1 for this family already.
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var deadline = clock.GetUtcNow() + _options.StreamMaxDuration;
        var lastWrite = clock.GetUtcNow();
        var cursor = since;

        await response.Body.FlushAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested && clock.GetUtcNow() < deadline)
        {
            // The token, every tick. A revocation has to reach somebody watching, and this is the
            // only thing on this connection that can carry it.
            var live = await gate.PeekAsync(share.Token, cancellationToken);

            if (live is null || !live.IsLiveAt(clock.GetUtcNow()))
            {
                await WriteFrameAsync(
                    response, Closed(cursor, "token-closed"), cursor, cancellationToken);

                return;
            }

            var delta = await PollAsync(live, cursor, cancellationToken);

            foreach (var frame in delta.Events)
            {
                await WriteFrameAsync(response, frame, delta.Cursor, cancellationToken);
                lastWrite = clock.GetUtcNow();
            }

            cursor = delta.Cursor;

            if (delta.Closed)
            {
                return;
            }

            if (clock.GetUtcNow() - lastWrite >= _options.StreamHeartbeat)
            {
                // A comment frame. Two bytes of payload, and the only thing that stops an
                // intermediary reaping a connection carrying a stationary vehicle.
                await response.WriteAsync(":\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);

                lastWrite = clock.GetUtcNow();
            }

            try
            {
                await Task.Delay(_options.StreamPollInterval, clock, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogDebug("A {Scope} stream reached its {Duration} ceiling; the client reconnects with ?since.",
            share.Scope, _options.StreamMaxDuration);
    }

    // -----------------------------------------------------------------------------------------

    private async Task<TrackDelta> RideDeltaAsync(
        ShareToken share, TrackCursor since, CancellationToken cancellationToken)
    {
        var ride = share.TripId is { } tripId
            ? await rides.FindRideAsync(tripId, cancellationToken)
            : null;

        if (ride is null)
        {
            return new TrackDelta([Closed(since, "unavailable")], since, Closed: true);
        }

        var position = await tracking.FreshPositionAsync(ride, cancellationToken);

        // The status a *recipient* watches is the parcel's four steps; the status a proxy rider
        // watches is the ride's own state. Same frame type, and the page knows which it asked for
        // because the token decided that before the stream opened.
        var status = share.Scope is ShareTokenScopes.PackageRecipient
            ? tracking.PackageStatusOf(ride, position)
            : ride.State;

        var events = new List<TrackEventResponse>(3);
        var cursor = new TrackCursor(position?.SampledAt ?? since.PositionAt, status);
        var now = clock.GetUtcNow();

        if (position is not null && position.SampledAt != since.PositionAt)
        {
            events.Add(new TrackEventResponse(
                PositionEvent,
                new TrackedPositionResponse(position.Lat, position.Lng, position.SampledAt),
                Status: null,
                At: now,
                Cursor: cursor.ToString()));
        }

        if (!string.Equals(status, since.Status, StringComparison.Ordinal))
        {
            events.Add(new TrackEventResponse(
                StatusEvent, Position: null, Status: status, At: now, Cursor: cursor.ToString()));
        }

        // The journey being over is a separate frame from the status that says so, because the page
        // does two different things with them: the status advances the tracker, and this is what
        // sends SCR-WT-002 to SCR-WT-005 and tells the client to stop reconnecting.
        var over = share.Scope is ShareTokenScopes.PackageRecipient
            ? RideStates.ParcelDelivered(ride.State)
            : RideStates.JourneyOver(ride.State);

        if (over)
        {
            events.Add(Closed(cursor, ride.State));
        }

        return new TrackDelta(events, cursor, over);
    }

    private async Task<TrackDelta> PickupDeltaAsync(
        ShareToken share, TrackCursor since, CancellationToken cancellationToken)
    {
        var request = share.LocationRequestId is { } id
            ? await rides.FindPickupRequestAsync(id, cancellationToken)
            : null;

        if (request is null)
        {
            return new TrackDelta([Closed(since, "unavailable")], since, Closed: true);
        }

        // **No position frame, ever.** SCR-WT-003 is the screen on which nobody's location has been
        // shared yet, and there is no vehicle: a feed that carried a coordinate here would be
        // carrying one this token was minted to *ask* for. P-02's fence, as a branch that produces
        // no such frame.
        var expired = request.ExpiresAt <= clock.GetUtcNow();
        var status = expired && request.IsOpen ? "Expired" : request.State;
        var cursor = new TrackCursor(since.PositionAt, status);
        var events = new List<TrackEventResponse>(2);
        var now = clock.GetUtcNow();

        if (!string.Equals(status, since.Status, StringComparison.Ordinal))
        {
            events.Add(new TrackEventResponse(
                StatusEvent, Position: null, Status: status, At: now, Cursor: cursor.ToString()));
        }

        var closed = expired || !request.IsOpen;

        if (closed)
        {
            events.Add(Closed(cursor, status));
        }

        return new TrackDelta(events, cursor, closed);
    }

    private TrackEventResponse Closed(TrackCursor cursor, string status) =>
        new(ResolvedEvent, Position: null, Status: status, At: clock.GetUtcNow(), Cursor: cursor.ToString());

    private static async Task WriteFrameAsync(
        HttpResponse response, TrackEventResponse frame, TrackCursor cursor, CancellationToken cancellationToken)
    {
        // `id:` is what an EventSource echoes back as `Last-Event-ID` on its own reconnect, so a
        // browser resumes with no page code at all; `?since` is the same value for a client that
        // rolled its own poll.
        await response.WriteAsync($"id: {cursor}\n", cancellationToken);
        await response.WriteAsync($"event: {frame.Type}\n", cancellationToken);
        await response.WriteAsync(
            $"data: {JsonSerializer.Serialize(frame, MageRideJson.Options)}\n\n", cancellationToken);

        await response.Body.FlushAsync(cancellationToken);
    }
}
