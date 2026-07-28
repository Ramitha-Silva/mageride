using MageRide.Fare.Estimates;
using MageRide.Shared.Errors;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Fare.Endpoints;

/// <summary>D3' <c>FareBreakdown</c> — support and receipt detail; US-8.4 shows only the total.</summary>
public sealed record FareBreakdownResponse(
    long FirstKmMinor,
    long PerKmMinor,
    double DistanceKm,
    int PeakSurchargePct,
    int NightSurchargePct);

/// <summary>The 200 of <c>GET /v1/fare/estimate</c>.</summary>
public sealed record FareEstimateResponse(
    string FareEstimateToken,
    long AmountMinor,
    string Currency,
    FareBreakdownResponse Breakdown);

/// <summary>
/// <c>/v1/fare</c> — the one operation the walking skeleton needs from fare-svc.
/// </summary>
/// <remarks>
/// The other fourteen operations in <c>backend/contracts/fare.yaml</c> — final calculation, the
/// payment state machine, the OnePay and LankaQR callbacks, driver-QR settlement (AL-47) and the
/// Finance refund routes — are C049/C050 and are left unmapped rather than stubbed. A stubbed
/// payment endpoint is worse than an absent one: it answers 200 to a client that then believes
/// money moved.
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

    private static Ok<FareEstimateResponse> EstimateAsync(
        double? fromLat,
        double? fromLng,
        double? toLat,
        double? toLng,
        string? vehicleType,
        string? kind,
        FareEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(estimator);

        var pickup = RequirePoint(fromLat, fromLng, "from");
        var dropoff = RequirePoint(toLat, toLng, "to");

        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["vehicleType"] = ["vehicleType is required."],
            });
        }

        // D3' declares `kind` optional with a `passenger` default.
        var quote = estimator.Quote(pickup, dropoff, vehicleType, kind ?? FareEstimator.PassengerKind);

        return TypedResults.Ok(new FareEstimateResponse(
            quote.FareEstimateToken,
            quote.AmountMinor,
            FareEstimateClaims.Currency,
            new FareBreakdownResponse(
                quote.Tariff.FirstKmMinor,
                quote.Tariff.PerKmMinor,
                Math.Round(quote.DistanceKm, 3),

                // STUB (C049): the windows are never evaluated, so both are always zero. They are
                // reported rather than omitted because a receipt that omits the field and one that
                // reports 0% say different things, and only the second is true here.
                PeakSurchargePct: 0,
                NightSurchargePct: 0)));
    }

    /// <summary>
    /// Parses a coordinate pair. A missing or out-of-range value is <c>400 validation-failed</c>;
    /// a well-formed coordinate outside the service area is <c>422 unserviceable-area</c>, which
    /// is the estimator's call, not this one's.
    /// </summary>
    private static GeoPoint RequirePoint(double? lat, double? lng, string prefix)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (lat is null || double.IsNaN(lat.Value) || lat.Value is < -90 or > 90)
        {
            errors[$"{prefix}Lat"] = [$"{prefix}Lat is required and must be between -90 and 90."];
        }

        if (lng is null || double.IsNaN(lng.Value) || lng.Value is < -180 or > 180)
        {
            errors[$"{prefix}Lng"] = [$"{prefix}Lng is required and must be between -180 and 180."];
        }

        return errors.Count == 0
            ? new GeoPoint(lat!.Value, lng!.Value)
            : throw new MageRideValidationException(errors);
    }
}
