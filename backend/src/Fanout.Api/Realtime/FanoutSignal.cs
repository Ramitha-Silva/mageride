using MageRide.Shared.Realtime;

namespace MageRide.Fanout.Realtime;

/// <summary>The kinds of directed send that travel the control channel.</summary>
public static class FanoutSignalKinds
{
    /// <summary>D-22. A passenger loses a Mode B vehicle, now, on whichever replica holds them.</summary>
    public const string ShareRevoked = "share.revoked";

    /// <summary>The counterpart: a grant was accepted, so an already-connected passenger joins.</summary>
    public const string ShareGranted = "share.granted";

    /// <summary>Every ride-aggregate transition (ADD Appendix B.2).</summary>
    public const string RideStateChanged = "ride.state_changed";

    /// <summary>P-02/P-13's round-trip resolving, to the booker's request group.</summary>
    public const string LocationRequestResolved = "location_request.resolved";

    /// <summary>US-20.7's package handoff progress.</summary>
    public const string PackageStatus = "package.status";
}

/// <summary>
/// One directed send, broadcast over <c>fanout:control</c> so it reaches whichever replica holds the
/// target connection (D6' §5's Redis backplane).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only sends whose audience this replica cannot know travel here.</b> A per-cell position batch
/// does not: every replica reads the cell streams it has members in, so coverage is already complete
/// and re-broadcasting one would deliver a copy per replica. What does travel is everything driven
/// by an <em>event</em> — consumed by exactly one replica, addressed to a connection that could be
/// on any of them.
/// </para>
/// <para>
/// <b>Delivery is best effort and that is the right guarantee.</b> Redis pub/sub drops a message for
/// a subscriber that is down, and every one of these is either re-derivable (a
/// <c>VehicleRemoved</c> the freshness sweep will repeat) or backed by durable state a reconnect
/// re-reads (the entitlement SET, the ride projection). What must not be lost is the <em>fact</em>,
/// and the fact is written to Redis before the signal is published.
/// </para>
/// </remarks>
/// <param name="Kind">One of <see cref="FanoutSignalKinds"/>.</param>
/// <param name="UserId">The passenger a <see cref="FanoutSignalKinds.ShareRevoked"/> is about.</param>
/// <param name="VehicleId">The vehicle a share or a removal is about.</param>
/// <param name="RideId">The ride whose group a state change or package update belongs to.</param>
/// <param name="BookerId">The booker whose <c>booker:{bookerId}:loc-req:{requestId}</c> group to reach.</param>
/// <param name="RideState">The event body of a <see cref="FanoutSignalKinds.RideStateChanged"/>.</param>
/// <param name="LocationRequest">The event body of a <see cref="FanoutSignalKinds.LocationRequestResolved"/>.</param>
/// <param name="Package">The event body of a <see cref="FanoutSignalKinds.PackageStatus"/>.</param>
/// <param name="IssuedAt">
/// When the consuming replica published it. The D-22 budget is measured from here, so a slow hop is
/// visible in <c>mageride.fanout.revocation</c> rather than hidden inside a replica's own timing.
/// </param>
public sealed record FanoutSignal(
    string Kind,
    Guid? UserId = null,
    Guid? VehicleId = null,
    Guid? RideId = null,
    Guid? BookerId = null,
    RideStateChangedEvent? RideState = null,
    LocationRequestResolvedEvent? LocationRequest = null,
    PackageStatusEvent? Package = null,
    DateTimeOffset? IssuedAt = null);
