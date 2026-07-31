using System.Collections.Frozen;
using MageRide.Fare.Domain;
using MageRide.Fare.Pricing;
using MageRide.Shared.Errors;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;

namespace MageRide.Fare.Estimates;

/// <param name="AmountMinor">Total payable — the only number US-8.4 lets the UI show.</param>
public sealed record FareQuote(string FareEstimateToken, FareBreakdown Breakdown)
{
    public long AmountMinor => Breakdown.TotalMinor;
}

/// <summary>
/// <c>GET /v1/fare/estimate</c> — prices a trip and mints the token that binds the price (US-8.9).
/// </summary>
/// <remarks>
/// <para>
/// <b>AL-19's fence lives here.</b> A Mode C tier shows a <em>price</em> before a driver is matched
/// and nothing else — no ETA, no distance. The quote returns the total plus a
/// <c>FareBreakdown</c> the contract marks for support and receipts, and this service computes no
/// arrival time at all: there is no field for one and no code that could fill it.
/// </para>
/// <para>
/// <b>The token binds the trip, not just the price.</b> Its claims carry the tier, the kind, the
/// amount and both endpoints, and ride-svc checks all of them — so a Rs 300 quote for a short hop
/// cannot be presented for a cross-city booking. That check is the reason the coordinates are in the
/// claims at all.
/// </para>
/// </remarks>
internal sealed class FareEstimator(FarePricingService pricing, FareEstimateTokenCodec tokens)
{
    /// <summary>
    /// Sri Lanka's bounding box, with a coastal margin.
    /// </summary>
    /// <remarks>
    /// <b>Still a box, and still an interim.</b> The real answer is <c>config.operating_cities</c>
    /// (migration 0201) — per-city service polygons an admin edits, which is also where a launch city
    /// is added without an app release. A box cannot tell Colombo from a jungle; it catches a caller
    /// who is on the wrong continent, which is what <c>unserviceable-area</c> is for and no more.
    /// Carried forward from the C022 stub unchanged and re-raised in the C049 handoff.
    /// </remarks>
    private const double MinLatitude = 5.5;
    private const double MaxLatitude = 10.2;
    private const double MinLongitude = 79.2;
    private const double MaxLongitude = 82.2;

    /// <summary>
    /// The Mode C bookable set (<c>_shared.yaml#RideVehicleType</c>, AL-09).
    /// </summary>
    /// <remarks>
    /// Checked before the tariff is looked up so the two failures stay distinct: <c>bus</c> is not a
    /// tier this endpoint prices <em>at all</em> (Mode A carries no fare, ever), whereas
    /// <c>truck</c> is a real tier whose rate Finance has not published yet — a
    /// <c>422 route-unavailable</c> an admin can fix, not a client bug.
    /// </remarks>
    private static readonly FrozenSet<string> BookableTypes = new[]
    {
        "motorbike", "three_wheeler", "flex", "sedan", "mini_van", "van", "truck", "mini_truck",
    }.ToFrozenSet(StringComparer.Ordinal);

    public async Task<FareQuote> QuoteAsync(
        GeoPoint pickup,
        GeoPoint dropoff,
        string? vehicleType,
        string? kind,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (vehicleType is null || !BookableTypes.Contains(vehicleType))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["vehicleType"] =
                [
                    "vehicleType must be one of " + string.Join(", ", BookableTypes.Order(StringComparer.Ordinal))
                    + " (AL-09). 'bus' and 'train' are Mode A and carry no fare at all.",
                ],
            });
        }

        var quoteKind = kind ?? RideKinds.PassengerQuote;

        if (quoteKind is not (RideKinds.PassengerQuote or RideKinds.PackageQuote))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = [$"kind must be '{RideKinds.PassengerQuote}' or '{RideKinds.PackageQuote}'."],
            });
        }

        RequireServiceable(pickup, "pickup");
        RequireServiceable(dropoff, "dropoff");

        var distanceKm = pricing.RouteDistanceKm(pickup, dropoff);
        var breakdown = await pricing.PriceAsync(vehicleType, distanceKm, at, cancellationToken);

        var token = tokens.Issue(
            vehicleType: vehicleType,
            kind: quoteKind,
            amountMinor: breakdown.TotalMinor,
            surchargeMinor: breakdown.SurchargeMinor,
            distanceKm: breakdown.DistanceKm,
            pickup: pickup,
            dropoff: dropoff);

        return new FareQuote(token, breakdown);
    }

    private static void RequireServiceable(GeoPoint point, string field)
    {
        if (point.Latitude is < MinLatitude or > MaxLatitude || point.Longitude is < MinLongitude or > MaxLongitude)
        {
            throw new MageRideException(
                MageRideErrors.UnserviceableArea,
                $"The {field} coordinate is outside Sri Lanka; MageRide operates nowhere else.");
        }
    }
}
