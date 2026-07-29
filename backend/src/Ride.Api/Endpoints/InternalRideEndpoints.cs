using System.Security.Cryptography;
using System.Text;
using MageRide.Ride.Domain;
using MageRide.Ride.Rides;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Ride.Endpoints;

/// <summary>
/// <c>/v1/internal/rides</c> — the three moves dispatch-svc needs and cannot make itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> ADD §11.11's sequence diagram draws dispatch-svc running
/// <c>UPDATE rides SET state='Offered' …</c> directly, but §11.12 states in the same document that
/// "<c>ride-svc</c> is the **sole writer** of <c>rides.state</c>", and the C023 prompt's fence
/// repeats it ("Ride state stays owned by ride-svc; dispatch only offers"). Sole-writer wins:
/// two services issuing conditional updates against one aggregate is precisely the race R-02
/// exists to remove. So the moves dispatch drives are exposed as commands here, and dispatch keeps
/// its own tables (<c>dispatch.offers</c>, <c>dispatch.candidate_scores</c>) to itself.
/// <b>None of these routes is in D3' — C022 added <c>matching</c> and <c>offer</c>, C023 added
/// <c>offer/expire</c>, and all three are recorded as micro-change-sets in
/// <c>build/progress.md</c>.</b> The same argument makes <c>offer/expire</c> a command rather than
/// something the R-04 backstop writes for itself: ADD §11.11 says the durable job "transitions the
/// ride back to <c>Matching</c>", and only ride-svc may do that.
/// </para>
/// <para>
/// <b>How they are protected.</b> D3' §0 puts the whole <c>/v1/internal/**</c> family on
/// service-to-service mTLS, and the API gateway already refuses the prefix at the edge (C008).
/// Until a mesh exists (C042) the in-cluster hop is guarded by a shared secret; without
/// <c>Ride:InternalApiKey</c> configured, these routes are not mapped at all.
/// </para>
/// </remarks>
public static class InternalRideEndpoints
{
    /// <summary>Carries <c>Ride:InternalApiKey</c>. Replaced by the mTLS peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalRideEndpoints(this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // AllowAnonymous because the caller is a service, not a user: there is no bearer to
        // present and the kernel's fallback policy would otherwise 401 every call. The filter
        // below is what actually authenticates it.
        var internalRides = endpoints.MapGroup("/v1/internal/rides")
            .WithTags("rides")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalApiKeyFilter(apiKey));

        // Δ C035: mapped before the templated routes so `/scheduled` is never mistaken for a ride
        // id. It is the fourth command of the same family — dispatch-svc owns the schedule and
        // ride-svc owns the aggregate, so the T-30 materialisation is a command, not a foreign
        // INSERT into rides.rides.
        internalRides.MapPost("/scheduled", MaterialiseScheduledAsync).WithName("materialiseScheduledRide");

        internalRides.MapPost("/{rideId}/matching", MarkMatchingAsync).WithName("markRideMatching");
        internalRides.MapPost("/{rideId}/offer", PlaceOfferAsync).WithName("placeRideOffer");
        internalRides.MapPost("/{rideId}/offer/expire", ExpireOfferAsync).WithName("expireRideOffer");

        // These three are D3' §0's mTLS family proper — unlike the three above, they are in the
        // contract already. They share the interim shared-secret filter because no mesh exists yet
        // (C042); the gateway refuses the whole `/v1/internal/**` prefix at the edge either way.
        internalRides.MapPost("/{rideId}/system-cancel", SystemCancelAsync).WithName("systemCancelRide");
        internalRides.MapPost("/{rideId}/payment-settled", PaymentSettledAsync).WithName("notifyPaymentSettled");
        internalRides.MapGet("/{rideId}/saga-state", SagaStateAsync).WithName("getRideSagaState");

        // Δ C037 — AL-45's web path into the P-02 state machine. A rider with no MageRide account
        // answers at `passenger.mageride.lk/track?token=…` (SCR-WT-003); public-bff validates and
        // burns the `pickup_confirm` token safety-svc minted, and then has to move a row in
        // `rides.location_requests`, which is this service's. So it is a command on the internal
        // plane, exactly like the four dispatch-svc drives — and for the same reason: ride-svc is
        // the sole writer of its own aggregates, and AL-45 explicitly routes the web flow through
        // "the same `rides.location_requests` state machine".
        //
        // No rider identity is asserted, because there is none — an unregistered rider has no
        // `rider_id` to match. The burned token is the credential, and public-bff is trusted to
        // have checked it, which is what the internal plane's mTLS (C042) is for.
        var internalRequests = endpoints.MapGroup("/v1/internal/location-requests")
            .WithTags("location-requests")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalApiKeyFilter(apiKey));

        internalRequests.MapPost("/{requestId}/confirm", ConfirmLocationAsync)
            .WithName("confirmLocationRequestForToken");

        internalRequests.MapPost("/{requestId}/decline", DeclineLocationAsync)
            .WithName("declineLocationRequestForToken");

        return endpoints;
    }

    private static async Task<Ok<LocationRequestResponse>> ConfirmLocationAsync(
        string requestId,
        ConfirmLocationBody? body,
        ILocationRequestService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var request = await service.ConfirmAsync(
            riderId: null,
            LocationRequestEndpoints.RequireRequestId(requestId),
            LocationRequestEndpoints.RequireGeo(body),
            body?.Accuracy,
            cancellationToken);

        return TypedResults.Ok(LocationRequestResponse.From(request));
    }

    private static async Task<Ok<LocationRequestResponse>> DeclineLocationAsync(
        string requestId, ILocationRequestService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        // No body here either. The web page and the app decline through statements that have no
        // column for a coordinate, which is what makes P-02's fence a property of the code.
        var request = await service.DeclineAsync(
            riderId: null, LocationRequestEndpoints.RequireRequestId(requestId), cancellationToken);

        return TypedResults.Ok(LocationRequestResponse.From(request));
    }

    private static async Task<Ok<RideStateChangeResponse>> MarkMatchingAsync(
        string rideId, MarkMatchingBody? body, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.MarkMatchingAsync(RequireRideId(rideId), body?.Version, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> MaterialiseScheduledAsync(
        MaterialiseScheduledBody? body, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var booking = await service.MaterialiseScheduledAsync(
            new MaterialiseScheduledRideCommand(
                ScheduledRideId: RequireId(body?.ScheduledRideId, "scheduledRideId"),
                PassengerId: RequireId(body?.PassengerId, "passengerId"),
                Pickup: body?.Pickup,
                Dropoff: body?.Dropoff,
                VehicleType: body?.VehicleType,
                PaymentMethod: body?.PaymentMethod),
            cancellationToken);

        // 200 on a replay as well as on a first call: the contract's only success is 200, and a
        // scheduler that retried after a timeout needs the ride id, not a different status code.
        return TypedResults.Ok(RideStateChangeResponse.From(booking.Ride));
    }

    private static async Task<Ok<OfferPlacedResponse>> PlaceOfferAsync(
        string rideId, PlaceOfferBody? body, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.PlaceOfferAsync(
            new PlaceOfferCommand(
                RideId: RequireRideId(rideId),
                OfferId: RequireId(body?.OfferId, "offerId"),
                DriverId: RequireId(body?.DriverId, "driverId"),
                VehicleId: RequireId(body?.VehicleId, "vehicleId"),
                TtlSeconds: body?.TtlSeconds,
                ExpectedVersion: body?.Version),
            cancellationToken);

        return TypedResults.Ok(OfferPlacedResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> ExpireOfferAsync(
        string rideId, ExpireOfferBody? body, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.ExpireOfferAsync(
            RequireRideId(rideId), RequireId(body?.OfferId, "offerId"), body?.Reason, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> SystemCancelAsync(
        string rideId, SystemCancelBody? body, IRideCancellationService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var cancellation = await service.SystemCancelAsync(
            RequireRideId(rideId), body?.Reason, cancellationToken);

        // No `penalty` member: §11.12 accrues nothing on a system cancel, and the contract's 200 is
        // a bare RideStateChange for exactly that reason.
        return TypedResults.Ok(RideStateChangeResponse.From(cancellation.Ride));
    }

    private static async Task<Ok<RideStateChangeResponse>> PaymentSettledAsync(
        string rideId, PaymentSettledBody? body, IRideSettlementService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.SettleAsync(
            new PaymentSettledCommand(
                RideId: RequireRideId(rideId),
                PaymentId: RequireId(body?.PaymentId, "paymentId"),
                PaymentState: body?.PaymentState,
                SettledMinor: body?.SettledMinor),
            cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
    }

    private static async Task<Ok<SagaStateResponse>> SagaStateAsync(
        string rideId, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        return TypedResults.Ok(
            SagaStateResponse.From(await service.GetSagaStateAsync(RequireRideId(rideId), cancellationToken)));
    }

    private static Guid RequireRideId(string? rideId) =>
        Ulids.TryParse(rideId, out var parsed)
            ? parsed
            : throw new MageRideException(MageRideErrors.NotFound, $"No ride '{rideId}'.");

    private static Guid RequireId(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });
}

/// <summary>
/// Rejects a call that does not carry the configured internal key.
/// </summary>
/// <remarks>
/// The answer is <c>404 not-found</c>, matching what the gateway returns for the same prefix
/// (C008): a caller who is not entitled to the internal plane should not be able to map it.
/// The comparison is fixed-time — the key is a secret, and a length-varying compare leaks it a
/// character at a time.
/// </remarks>
internal sealed class InternalApiKeyFilter(string apiKey) : IEndpointFilter
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[InternalRideEndpoints.ApiKeyHeader].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
