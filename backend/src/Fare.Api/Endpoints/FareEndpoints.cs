using MageRide.Fare.Domain;
using MageRide.Fare.Estimates;
using MageRide.Fare.Settlement;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Fare.Endpoints;

/// <summary>D3' <c>FareBreakdown</c> — support and receipt detail; US-8.4 shows only the total.</summary>
public sealed record FareBreakdownResponse(
    long FirstKmMinor,
    long PerKmMinor,
    double DistanceKm,
    int PeakSurchargePct,
    int NightSurchargePct)
{
    public static FareBreakdownResponse From(FareBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);

        return new FareBreakdownResponse(
            breakdown.FirstKmMinor,
            breakdown.PerKmMinor,
            breakdown.DistanceKm,
            breakdown.PeakSurchargePct,
            breakdown.NightSurchargePct);
    }
}

/// <summary>The 200 of <c>GET /v1/fare/estimate</c>.</summary>
public sealed record FareEstimateResponse(
    string FareEstimateToken, long AmountMinor, string Currency, FareBreakdownResponse Breakdown);

/// <summary>The body of <c>POST /v1/fare/calculate</c>.</summary>
public sealed record CalculateFareBody(string? RideId, double? DistanceKm, int? DurationSec);

/// <summary>The 200 of <c>POST /v1/fare/calculate</c>.</summary>
public sealed record FinalFareResponse(
    Guid PaymentId, long AmountMinor, string Currency, FareBreakdownResponse Breakdown);

/// <summary>
/// <c>/v1/fare</c> — the estimate a passenger is quoted and the final fare a completion produces.
/// </summary>
/// <remarks>
/// <b>AL-19's fence is structural here.</b> A Mode C tier exposes a price and nothing else before a
/// driver is matched: neither response carries an ETA or a duration, and this service computes
/// neither. The <c>durationSec</c> the contract lets a caller send is accepted and unused — the D5'
/// §1.1 tariff has no time component at all, and a fare that quietly grew one would be a different
/// pricing model.
/// </remarks>
public static class FareEndpoints
{
    public static IEndpointRouteBuilder MapFareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/v1/fare/estimate", EstimateAsync)
            .WithTags("fare")
            .WithName("estimateFare")
            .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// <c>POST /v1/fare/calculate</c> — internal, and mapped only when a key is configured.
    /// </summary>
    /// <remarks>
    /// D3' puts this route on mTLS internal. Until C042 lands a mesh identity the guard is the same
    /// interim shared secret every other internal plane on the platform carries, and an unset key
    /// leaves the route <b>unmapped</b> rather than open: every completed ride goes through here, so
    /// a caller who could reach it unauthenticated could price somebody else's journey.
    /// </remarks>
    public static IEndpointRouteBuilder MapInternalFareEndpoints(
        this IEndpointRouteBuilder endpoints, string internalApiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/v1/fare/calculate", CalculateAsync)
            .AddEndpointFilter(new InternalKeyFilter(internalApiKey))
            .AllowAnonymous()
            .WithTags("fare")
            .WithName("calculateFinalFare");

        return endpoints;
    }

    private static async Task<Ok<FareEstimateResponse>> EstimateAsync(
        double? fromLat,
        double? fromLng,
        double? toLat,
        double? toLng,
        string? vehicleType,
        string? kind,
        FareEstimator estimator,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(clock);

        var pickup = RequirePoint(fromLat, fromLng, "fromLat", "fromLng");
        var dropoff = RequirePoint(toLat, toLng, "toLat", "toLng");

        var quote = await estimator.QuoteAsync(
            pickup, dropoff, vehicleType, kind, clock.GetUtcNow(), cancellationToken);

        return TypedResults.Ok(new FareEstimateResponse(
            quote.FareEstimateToken,
            quote.AmountMinor,
            quote.Breakdown.Currency,
            FareBreakdownResponse.From(quote.Breakdown)));
    }

    private static async Task<Ok<FinalFareResponse>> CalculateAsync(
        CalculateFareBody? body,
        FareSettlementService settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var rideId = RequestIds.Require(body?.RideId, "rideId");

        var fare = await settlement.CalculateAsync(rideId, body?.DistanceKm, cancellationToken);

        return TypedResults.Ok(new FinalFareResponse(
            fare.Payment.Id,
            fare.AmountMinor,
            fare.Payment.Currency,
            FareBreakdownResponse.From(fare.Breakdown)));
    }

    /// <remarks>
    /// The four coordinates are <c>required</c> in the contract, so a missing one is the caller's
    /// bug and named as such rather than defaulted to the equator.
    /// </remarks>
    private static GeoPoint RequirePoint(double? lat, double? lng, string latField, string lngField)
    {
        if (lat is not { } latitude || lng is not { } longitude)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [latField] = [$"{latField} and {lngField} are both required."],
            });
        }

        try
        {
            return new GeoPoint(latitude, longitude);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [latField] = [exception.Message],
            });
        }
    }
}

/// <summary>Parses the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").</summary>
/// <remarks>
/// The same twelve lines wallet-svc, subscription-svc and reputation-svc carry. Per service rather
/// than in the kernel because each one names its own fields in the error, which is what makes a 400
/// actionable.
/// </remarks>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}

/// <summary>
/// Rejects a call that does not carry <c>Fare:InternalApiKey</c>.
/// </summary>
/// <remarks>
/// Answers <c>404 not-found</c>, matching what the gateway returns for the <c>/v1/internal</c>
/// prefix (C008): a caller who is not entitled to the internal plane should not be able to map it.
/// Fixed-time comparison — a length-varying compare leaks the key a character at a time.
/// </remarks>
internal sealed class InternalKeyFilter(string apiKey) : IEndpointFilter
{
    /// <summary>Carries the interim shared secret. Replaced by the mesh peer identity in C042.</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly byte[] _expected = System.Text.Encoding.UTF8.GetBytes(apiKey);

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var presented = context.HttpContext.Request.Headers[ApiKeyHeader].ToString();

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented), _expected)
            ? next(context)
            : throw new MageRideException(MageRideErrors.NotFound, "No such resource.");
    }
}
