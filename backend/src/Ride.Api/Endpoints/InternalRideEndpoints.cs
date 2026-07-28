using System.Security.Cryptography;
using System.Text;
using MageRide.Ride.Domain;
using MageRide.Ride.Rides;
using MageRide.Shared.Errors;
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

        internalRides.MapPost("/{rideId}/matching", MarkMatchingAsync).WithName("markRideMatching");
        internalRides.MapPost("/{rideId}/offer", PlaceOfferAsync).WithName("placeRideOffer");
        internalRides.MapPost("/{rideId}/offer/expire", ExpireOfferAsync).WithName("expireRideOffer");

        return endpoints;
    }

    private static async Task<Ok<RideStateChangeResponse>> MarkMatchingAsync(
        string rideId, MarkMatchingBody? body, IRideService service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var ride = await service.MarkMatchingAsync(RequireRideId(rideId), body?.Version, cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
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
            RequireRideId(rideId), RequireId(body?.OfferId, "offerId"), cancellationToken);

        return TypedResults.Ok(RideStateChangeResponse.From(ride));
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
