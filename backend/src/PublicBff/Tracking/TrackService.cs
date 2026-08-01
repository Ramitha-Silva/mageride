using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Domain;
using MageRide.PublicBff.Endpoints;
using MageRide.PublicBff.Live;
using MageRide.PublicBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff.Tracking;

/// <summary>
/// What one token is allowed to see, resolved and shaped.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scope decides the shape, and the client cannot ask for a wider one.</b> There is no query
/// parameter, no <c>Accept</c> negotiation and no field on any request that selects a variant —
/// <c>safety.trip_share_tokens.scope</c> is read from the row and dispatched on, so a
/// <c>pickup_confirm</c> holder cannot obtain the package view by asking differently.
/// </para>
/// <para>
/// <b>Each variant is a closed type with no field for what it may not carry.</b> The recipient's
/// snapshot has nowhere to put the sender's number and the rider's has nowhere to put a payment
/// instrument, so P-02/P-09's redaction survives a change to this file.
/// </para>
/// </remarks>
public interface ITrackService
{
    /// <summary>The snapshot behind SCR-WT-002 / 003 / 004, already shaped for the token's scope.</summary>
    Task<object> SnapshotAsync(ShareToken share, CancellationToken cancellationToken);

    /// <summary>
    /// The ride a trip-scoped token names, or a <c>410</c> when there is nothing live behind it.
    /// </summary>
    Task<TrackedRide> RequireRideAsync(ShareToken share, CancellationToken cancellationToken);

    /// <summary>The location request a <c>pickup_confirm</c> token names.</summary>
    Task<TrackedPickupRequest> RequirePickupRequestAsync(ShareToken share, CancellationToken cancellationToken);

    /// <summary>The one fix this surface may draw, or <see langword="null"/> when it is stale or absent.</summary>
    Task<LivePosition?> FreshPositionAsync(TrackedRide ride, CancellationToken cancellationToken);

    /// <summary>The four-step status SCR-WT-002 draws, for a ride and the position it is at.</summary>
    string PackageStatusOf(TrackedRide ride, LivePosition? position);
}

/// <inheritdoc cref="ITrackService"/>
internal sealed class TrackService(
    ITrackReadRepository rides,
    ILivePositionReader positions,
    IDeliveryCodeReader deliveryCodes,
    IOptions<PublicBffOptions> options,
    TimeProvider clock) : ITrackService
{
    private readonly PublicBffOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<object> SnapshotAsync(ShareToken share, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);

        return share.Scope switch
        {
            ShareTokenScopes.PackageRecipient => await PackageAsync(share, cancellationToken),
            ShareTokenScopes.ProxyRider => await ProxyRideAsync(share, cancellationToken),
            ShareTokenScopes.PickupConfirm => await PickupConfirmAsync(share, cancellationToken),

            // Unreachable: the gate refuses every other scope before this is called. Kept as a
            // throw rather than a default arm that invents a shape.
            _ => throw new MageRideException(MageRideErrors.TokenUnknown, "No such tracking link."),
        };
    }

    public async Task<TrackedRide> RequireRideAsync(ShareToken share, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);

        var ride = share.TripId is { } tripId
            ? await rides.FindRideAsync(tripId, cancellationToken)
            : null;

        // **A 410 rather than a 404 or a half-filled snapshot.** A trip-scoped token whose ride has
        // gone, or that was minted before a driver was assigned, has nothing live to show — and the
        // contract's driver block is required, so the alternative is emitting a name and a plate
        // that are empty strings. SCR-WT-006's dead end is the honest destination.
        if (ride is null || ride.DriverName is null || ride.RegistrationNumber is null)
        {
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This link no longer has anything to show.");
        }

        return ride;
    }

    public async Task<TrackedPickupRequest> RequirePickupRequestAsync(
        ShareToken share, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);

        var request = share.LocationRequestId is { } id
            ? await rides.FindPickupRequestAsync(id, cancellationToken)
            : null;

        if (request is null || string.IsNullOrWhiteSpace(request.BookerFirstName))
        {
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This link no longer has anything to show.");
        }

        // The 300 s deadline is the request row, not the token: ride-svc's sweep and this page have
        // to agree about when the window closed, and `issued_at + ttl_seconds` is the durable fact
        // both read (0609 (c)). A request that has already been answered is likewise over — a rider
        // who shared and then reopened the SMS gets the dead end, not a second chance to move a pin
        // the booker has already used.
        if (!request.IsOpen || request.ExpiresAt <= clock.GetUtcNow())
        {
            throw new MageRideException(
                MageRideErrors.TokenExpiredOrRevoked, "This request has already been answered or has expired.");
        }

        return request;
    }

    public async Task<LivePosition?> FreshPositionAsync(TrackedRide ride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ride);

        if (ride.VehicleId is not { } vehicleId)
        {
            return null;
        }

        var position = await positions.ReadAsync(vehicleId, cancellationToken);

        // Stale is omitted, never drawn. The person watching is not in the vehicle and has no other
        // way to tell that the marker stopped moving twenty minutes ago.
        return position is not null && clock.GetUtcNow() - position.SampledAt <= _options.PositionMaxAge
            ? position
            : null;
    }

    public string PackageStatusOf(TrackedRide ride, LivePosition? position)
    {
        ArgumentNullException.ThrowIfNull(ride);

        if (RideStates.ParcelDelivered(ride.State))
        {
            return PackageStatuses.Delivered;
        }

        if (!RideStates.ParcelAboard(ride.State))
        {
            return PackageStatuses.PickupPending;
        }

        // `PickedUp` and `InTransit` are one ride state (invariant 6: a package adds none), and the
        // fact that separates them is whether the vehicle has left the sender. With no position the
        // answer is the conservative one: the parcel is aboard, and claiming it is on its way with
        // nothing to show for that would be a guess told as a fact.
        if (position is null)
        {
            return PackageStatuses.PickedUp;
        }

        var metresFromPickup = GeoMath.DistanceM(ride.Pickup, new GeoPoint(position.Lat, position.Lng));

        return metresFromPickup >= _options.PickupDepartureRadiusM
            ? PackageStatuses.InTransit
            : PackageStatuses.PickedUp;
    }

    // -----------------------------------------------------------------------------------------
    // The three shapes
    // -----------------------------------------------------------------------------------------

    private async Task<PackageSnapshotResponse> PackageAsync(ShareToken share, CancellationToken cancellationToken)
    {
        var ride = await RequireRideAsync(share, cancellationToken);
        var position = await FreshPositionAsync(ride, cancellationToken);

        // Only while the parcel is aboard. A code shown before pickup is a code the driver has not
        // been issued yet, and one shown after delivery is a live credential on a finished job.
        var deliveryOtp = RideStates.ParcelAboard(ride.State)
            ? await deliveryCodes.ReadAsync(ride.RideId, cancellationToken)
            : null;

        return new PackageSnapshotResponse(
            Kind: "package",
            Status: PackageStatusOf(ride, position),
            Driver: DriverOf(ride),
            Position: PositionOf(position),
            DeliveryOtp: deliveryOtp,

            // `rides.rides` stores a `dropoff_geo` and no address (0601): the drop-off is a pin the
            // sender dropped, not a line somebody typed. The object is omitted rather than filled
            // with coordinates the schema has no field for. Recorded as a spec gap in the handoff.
            Dropoff: null,

            // The sender's *display name*, which is what the schema's own description asks for
            // despite the field being called `masked`. Their number is not on this type at all —
            // P-09's fence is structural here, not a redaction step.
            SenderNameMasked: ride.SenderName);
    }

    private async Task<ProxyRideSnapshotResponse> ProxyRideAsync(ShareToken share, CancellationToken cancellationToken)
    {
        var ride = await RequireRideAsync(share, cancellationToken);
        var position = await FreshPositionAsync(ride, cancellationToken);

        return new ProxyRideSnapshotResponse(
            Kind: "ride",
            State: ride.State,
            Driver: DriverOf(ride),
            Position: PositionOf(position),
            EtaMin: EtaMinutes(ride, position),

            // **Absent, and nothing on the platform can fill it.** `ride.yaml` and ride-svc's own
            // `RideContracts` say a rider start OTP is "accepted and ignored in this build: no
            // endpoint issues one" — the two OTP columns on `rides.rides` are package-only and
            // `ck_rides_package_complete` requires them for `kind = 2` alone. Emitting four digits
            // here would mean inventing a code no driver could be asked to check. Spec gap, raised
            // in the C066 handoff.
            StartOtp: null,

            Route: RouteOf(ride),
            Fare: FareOf(ride));
    }

    private async Task<PickupConfirmSnapshotResponse> PickupConfirmAsync(
        ShareToken share, CancellationToken cancellationToken)
    {
        var request = await RequirePickupRequestAsync(share, cancellationToken);
        var remaining = request.ExpiresAt - clock.GetUtcNow();

        return new PickupConfirmSnapshotResponse(
            Kind: "pickup_confirm",

            // First name only — enough to recognise who is asking, and no more. The booker's number,
            // their account and even their surname are absent, because a rider being asked for their
            // live position is not owed an identity file on the person asking (P-02).
            BookerFirstName: request.BookerFirstName!,
            SuggestedPin: request.SuggestedPin is { } pin ? new GeoPointResponse(pin.Latitude, pin.Longitude) : null,
            ExpiresAt: request.ExpiresAt,
            TtlRemainingSec: (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds)));
    }

    // -----------------------------------------------------------------------------------------

    private static PublicDriverResponse DriverOf(TrackedRide ride) =>
        new(ride.DriverName!, ride.DriverPhotoUrl, ride.VehicleType, ride.RegistrationNumber!, ride.DriverPhone);

    private static TrackedPositionResponse? PositionOf(LivePosition? position) =>
        position is null ? null : new TrackedPositionResponse(position.Lat, position.Lng, position.SampledAt);

    /// <summary>
    /// The straight line between the two ends of the journey.
    /// </summary>
    /// <remarks>
    /// <b>An origin and a destination, not a road path.</b> ADD §7.6 puts routing (OSRM/Valhalla,
    /// snap-to-road) in Phase 3, so there is no road network on the platform to trace — and
    /// <c>rides.rides</c> stores two points and no geometry. Two vertices are what the aggregate
    /// knows; a page that drew them as a route would be drawing what dispatch-svc and query-svc also
    /// only have.
    /// </remarks>
    private static RouteResponse? RouteOf(TrackedRide ride) =>
        EncodedPolyline.Encode([ride.Pickup, ride.Dropoff]) is { Length: > 0 } polyline
            ? new RouteResponse(polyline)
            : null;

    /// <summary>
    /// How much, and who settles it — never with what.
    /// </summary>
    /// <remarks>
    /// <c>cash_due</c> is US-8.21's notice: the booker chose cash, so the money is owed by whoever
    /// is in the car at the end, which on a proxy ride is the person reading this page. Every other
    /// method settles against the booker's own instrument, and <b>which</b> instrument is not said
    /// (P-09). A ride with no quote carries no fare block rather than a zero — <c>Rs 0.00</c> on a
    /// tracking page reads as "this is free".
    /// </remarks>
    private static PublicFareResponse? FareOf(TrackedRide ride) =>
        ride.FareEstimateMinor is { } totalMinor
            ? new PublicFareResponse(
                totalMinor,
                ride.Currency,
                ride.PaymentMethod is "cash" or "cod" ? FarePayers.CashDue : FarePayers.Booker)
            : null;

    /// <summary>
    /// Minutes until the driver reaches whoever is waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A straight line with a detour factor, and it says so</b> — the same method and the same
    /// caveat as query-svc's <c>EtaEstimator</c> (US-7.11): ADD §7.6 puts routing in Phase 3, so a
    /// routed estimate is not something any component can compute yet. The two implementations are a
    /// deliberate duplication rather than a shared one: that estimator lives inside query-svc with
    /// its own per-vehicle-type speed table, and a passenger-facing pod taking a
    /// <c>ProjectReference</c> on another service to reach it would be the worse coupling. Promotion
    /// to the kernel is proposed in the C066 handoff.
    /// </para>
    /// <para>
    /// <b>The target moves with the ride.</b> Before <c>InProgress</c> the rider is waiting at the
    /// pickup and the ETA is to them; after it they are in the car and the ETA is to the drop-off.
    /// Once the journey is over there is nothing to estimate.
    /// </para>
    /// </remarks>
    private int? EtaMinutes(TrackedRide ride, LivePosition? position)
    {
        if (position is null || RideStates.JourneyOver(ride.State))
        {
            return null;
        }

        var target = RideStates.ParcelAboard(ride.State) ? ride.Dropoff : ride.Pickup;
        var roadM = GeoMath.DistanceM(new GeoPoint(position.Lat, position.Lng), target) * _options.EtaDetourFactor;

        // A vehicle stopped at a light reports ~0 m/s and dividing by that gives an ETA of hours.
        // Below the floor the configured city average is used instead — an average *including*
        // stops, which is why it is a fraction of ADD §12.6's anti-spoof ceilings: those are the
        // speeds above which a fix is a lie, this is the speed a vehicle crosses Colombo at.
        var speedMps = position.SpeedMps is { } reported && reported >= MinUsableSpeedMps
            ? reported
            : _options.EtaAssumedSpeedKph * 1000d / 3600d;

        if (speedMps <= 0)
        {
            return null;
        }

        var minutes = roadM / speedMps / 60d;

        // "Arriving in 94 minutes" is arithmetically sound and not a thing anybody plans around; an
        // absent field is a truer statement than a number at the limit of the method.
        return minutes > _options.MaxEta.TotalMinutes ? null : (int)Math.Round(minutes);
    }

    /// <summary>
    /// Below this the reported speed is treated as "stopped" rather than as a rate.
    /// </summary>
    /// <remarks>
    /// 1.5 m/s is walking pace. Not a setting: it is a property of what a GPS reports at a
    /// standstill, not a tuning knob, and query-svc's estimator draws the line in the same place.
    /// </remarks>
    private const double MinUsableSpeedMps = 1.5;
}
