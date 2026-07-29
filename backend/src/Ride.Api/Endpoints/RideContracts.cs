using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Ride.Rides;

namespace MageRide.Ride.Endpoints;

/// <summary>D3' <c>Place</c> — a coordinate plus its optional human address.</summary>
public sealed record PlaceBody(double? Lat, double? Lng, string? Address);

/// <summary>The body of <c>POST /v1/rides/request</c> (D3' <c>RideRequest</c>).</summary>
/// <remarks>
/// The proxy and package members are declared so a client written against the contract compiles
/// and so a booking that sets them is refused with a reason rather than silently downgraded to a
/// plain passenger ride. C032/C037 implement them.
/// </remarks>
public sealed record RideRequestBody(
    string? ClientRequestId,
    string? Kind,
    PlaceBody? Pickup,
    PlaceBody? Dropoff,
    string? VehicleType,
    string? FareEstimateToken,
    string? PaymentMethod,
    DateTimeOffset? ScheduledAt,
    bool? IsProxy,
    string? RiderName,
    string? RiderPhone,
    string? PackageSize,
    string? PackageDescription,
    string? RecipientName,
    string? RecipientPhone);

/// <summary>D3' <c>VersionedCommand</c> — every mutation echoes the version it expects.</summary>
public sealed record VersionedCommandBody(long? Version);

/// <summary>The body of <c>POST /v1/rides/{rideId}/start</c>.</summary>
/// <param name="Otp">
/// The rider's start OTP. **Accepted and ignored** in this build: no endpoint issues a start OTP
/// and <c>rides.rides</c> has no column for one — the two OTP hashes it does have are the
/// package pickup/delivery pair (P-07). Recorded as contract gap (f) in the C022 handoff.
/// </param>
public sealed record StartRideBody(long? Version, string? Otp);

/// <summary>The body of <c>POST /v1/rides/{rideId}/offer/{driverId}/accept</c>.</summary>
public sealed record AcceptOfferBody(string? OfferId, long? Version);

/// <summary>The body of <c>POST /v1/rides/{rideId}/offer/{driverId}/decline</c>.</summary>
public sealed record DeclineOfferBody(string? OfferId);

/// <summary>D3' <c>FareEstimate</c>.</summary>
public sealed record FareEstimateResponse(long AmountMinor, string Currency, long SurchargeMinor)
{
    public static FareEstimateResponse? From(RideRow ride) =>
        ride.FareEstimateMinor is { } amount
            ? new FareEstimateResponse(amount, ride.Currency, ride.FareSurchargeMinor)
            : null;
}

/// <summary>The 202 of <c>POST /v1/rides/request</c>.</summary>
/// <param name="PickupOtp">
/// Package bookings only (P-07), and never issued in this build — always absent.
/// </param>
public sealed record RideRequestedResponse(
    Guid RideId, string State, long Version, FareEstimateResponse? EstimatedFare, string? PickupOtp = null)
{
    public static RideRequestedResponse From(RideRow ride) =>
        new(ride.Id, ride.State, ride.Version, FareEstimateResponse.From(ride));
}

/// <summary>D3' <c>RideStateChange</c>.</summary>
public sealed record RideStateChangeResponse(Guid RideId, string State, long Version)
{
    public static RideStateChangeResponse From(RideRow ride) => new(ride.Id, ride.State, ride.Version);
}

/// <summary>The 200 of <c>POST /v1/rides/{rideId}/complete</c> — a state change plus the fare.</summary>
public sealed record RideCompletedResponse(Guid RideId, string State, long Version, FareEstimateResponse? Fare)
{
    public static RideCompletedResponse From(RideRow ride) =>
        new(ride.Id, ride.State, ride.Version, FareEstimateResponse.From(ride));
}

/// <summary>The 200 of <c>GET /v1/rides/{rideId}/state</c> — the cheap poll behind the countdown.</summary>
public sealed record RideStateResponse(string State, long Version, DateTimeOffset? OfferExpiresAt)
{
    public static RideStateResponse From(RideRow ride) => new(ride.State, ride.Version, ride.OfferExpiresAt);
}

/// <summary>D3' <c>RideDriver</c>.</summary>
public sealed record RideDriverResponse(
    Guid DriverId, string Name, string? PhotoUrl, string VehicleType, string RegistrationNumber)
{
    public static RideDriverResponse? From(RideDriverSummary? driver) =>
        driver is null
            ? null
            : new RideDriverResponse(
                driver.DriverId, driver.Name, driver.PhotoUrl, driver.VehicleType, driver.RegistrationNumber);
}

/// <summary>
/// D3' <c>RideDetail</c>.
/// </summary>
/// <remarks>
/// Three optional members of the contract's schema are deliberately never populated here and are
/// listed as contract gaps in the C022 handoff: <c>counterpartyPhone</c> (AL-48) needs an
/// <c>iam.users</c> read this service does not make, and the two package members belong to C037.
/// <c>rating</c> and <c>etaSeconds</c> on the driver are reputation-svc's and dispatch-svc's.
/// </remarks>
public sealed record RideDetailResponse(
    Guid RideId,
    string Kind,
    string State,
    long Version,
    Guid BookerId,
    Guid? RiderId,
    string? RiderName,
    PlaceBody Pickup,
    PlaceBody Dropoff,
    string VehicleType,
    string PaymentMethod,
    DateTimeOffset? OfferExpiresAt,
    RideDriverResponse? Driver,
    FareEstimateResponse? Fare,
    DateTimeOffset CreatedAt)
{
    public static RideDetailResponse From(RideView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var ride = view.Ride;

        return new RideDetailResponse(
            RideId: ride.Id,
            Kind: ride.KindName,
            State: ride.State,
            Version: ride.Version,
            BookerId: ride.BookerId,
            RiderId: ride.RiderId,
            RiderName: ride.RiderName,

            // `address` is null because nothing stores it; the contract types it optional.
            Pickup: new PlaceBody(ride.PickupGeo.Latitude, ride.PickupGeo.Longitude, null),
            Dropoff: new PlaceBody(ride.DropoffGeo.Latitude, ride.DropoffGeo.Longitude, null),
            VehicleType: ride.VehicleType,
            PaymentMethod: ride.PaymentMethod,
            OfferExpiresAt: ride.OfferExpiresAt,
            Driver: RideDriverResponse.From(view.Driver),
            Fare: FareEstimateResponse.From(ride),
            CreatedAt: ride.CreatedAt);
    }
}

/// <summary>The 200 of the accept — a state change carrying the whole aggregate.</summary>
public sealed record AcceptOfferResponse(Guid RideId, string State, long Version, RideDetailResponse Ride)
{
    public static AcceptOfferResponse From(RideView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new AcceptOfferResponse(view.Ride.Id, view.Ride.State, view.Ride.Version, RideDetailResponse.From(view));
    }
}

/// <summary>The body of <c>POST /v1/internal/rides/{rideId}/matching</c>.</summary>
public sealed record MarkMatchingBody(long? Version);

/// <summary>The body of <c>POST /v1/internal/rides/scheduled</c> (Δ C035).</summary>
/// <param name="ScheduledRideId">
/// The <c>dispatch.scheduled_rides</c> id. Becomes the ride's <c>clientRequestId</c>, which is
/// what makes the call idempotent under R-18.
/// </param>
public sealed record MaterialiseScheduledBody(
    string? ScheduledRideId,
    string? PassengerId,
    RidePlace? Pickup,
    RidePlace? Dropoff,
    string? VehicleType,
    string? PaymentMethod);

/// <summary>The body of <c>POST /v1/internal/rides/{rideId}/offer</c>.</summary>
/// <param name="TtlSeconds">
/// Offer window; omitted means <c>Ride:OfferTtl</c> (15 s, D5' §3.5). The deadline itself is
/// stamped from ride-svc's clock — see <see cref="Rides.RideService.PlaceOfferAsync"/>.
/// </param>
public sealed record PlaceOfferBody(string? OfferId, string? DriverId, string? VehicleId, int? TtlSeconds, long? Version);

/// <summary>The 200 of the internal offer route: the state change plus the deadline dispatch must honour.</summary>
public sealed record OfferPlacedResponse(Guid RideId, string State, long Version, Guid? OfferId, DateTimeOffset? OfferExpiresAt)
{
    public static OfferPlacedResponse From(RideRow ride) =>
        new(ride.Id, ride.State, ride.Version, ride.CurrentOfferId, ride.OfferExpiresAt);
}

/// <summary>The body of <c>POST /v1/rides/{rideId}/cancel</c>.</summary>
/// <param name="Reason">
/// <c>RIDER_CHANGED_MIND | DRIVER_TOO_FAR | EMERGENCY | OTHER</c>. Recorded and published; it does
/// not choose the outcome — the §11.12 matrix does, from the ride's state and who is calling.
/// </param>
public sealed record CancelRideBody(long? Version, string? Reason);

/// <summary>The accrued debt a cancellation left behind (D3' <c>cancel</c> 200 <c>penalty</c>).</summary>
public sealed record RidePenaltyResponse(long AmountMinor, string Currency, string SettledOn);

/// <summary>The 200 of <c>POST /v1/rides/{rideId}/cancel</c>.</summary>
/// <remarks>
/// <c>penalty</c> is absent when the matrix's Penalty column says None — a pre-acceptance cancel
/// (US-6A.9) and every driver-side one. Present with the amount otherwise, so the app can show the
/// passenger what they now owe without a second call.
/// </remarks>
public sealed record RideCancelledResponse(Guid RideId, string State, long Version, RidePenaltyResponse? Penalty)
{
    public static RideCancelledResponse From(RideCancellation cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        var ride = cancellation.Ride;

        return new RideCancelledResponse(
            ride.Id,
            ride.State,
            ride.Version,
            cancellation.Outcome.Penalty is RidePenaltyBasis.None
                ? null
                : new RidePenaltyResponse(cancellation.PenaltyMinor, ride.Currency, RideSettlement.NextTrip));
    }
}

/// <summary>The body of <c>POST /v1/internal/rides/{rideId}/system-cancel</c>.</summary>
public sealed record SystemCancelBody(string? Reason);

/// <summary>The body of <c>POST /v1/internal/rides/{rideId}/payment-settled</c>.</summary>
public sealed record PaymentSettledBody(string? PaymentId, string? PaymentState, long? SettledMinor);

/// <summary>One row of the saga diagnostics' transition log.</summary>
public sealed record SagaTransitionResponse(string? From, string To, DateTimeOffset At, string Actor, string? Reason);

/// <summary>The 200 of <c>GET /v1/internal/rides/{rideId}/saga-state</c>.</summary>
public sealed record SagaStateResponse(
    Guid RideId,
    string State,
    long Version,
    IReadOnlyList<SagaTransitionResponse> Transitions,
    int PendingOutbox)
{
    public static SagaStateResponse From(RideSagaState saga)
    {
        ArgumentNullException.ThrowIfNull(saga);

        return new SagaStateResponse(
            saga.Ride.Id,
            saga.Ride.State,
            saga.Ride.Version,
            [.. saga.Transitions.Select(row =>
                new SagaTransitionResponse(row.FromState, row.ToState, row.Ts, row.ActorType, row.ReasonCode))],
            saga.PendingOutbox);
    }
}

/// <summary>The body of <c>POST /v1/internal/rides/{rideId}/offer/expire</c>.</summary>
/// <param name="OfferId">
/// Which offer expired. Required: the backstop fires against the offer it armed, and by the time
/// it runs the ride may be holding a later one.
/// </param>
/// <param name="Reason">
/// <c>deadline</c> (the default, R-04's durable backstop) or <c>driver_unreachable</c> (R-15's
/// last-will grace). <b>Only the second one may revoke an offer before its deadline</b>, and only
/// because the driver's broker session is confirmed gone — see
/// <c>IRideService.ExpireOfferAsync</c>.
/// </param>
public sealed record ExpireOfferBody(string? OfferId, string? Reason = null);
