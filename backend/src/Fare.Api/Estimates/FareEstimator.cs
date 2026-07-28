using MageRide.Shared.Errors;
using MageRide.Shared.Fares;
using MageRide.Shared.Primitives;

namespace MageRide.Fare.Estimates;

/// <param name="AmountMinor">Total payable — the only number US-8.4 lets the UI show.</param>
/// <param name="Breakdown">Support/receipt detail (D3' <c>FareBreakdown</c>).</param>
public sealed record FareQuote(
    string FareEstimateToken,
    long AmountMinor,
    long SurchargeMinor,
    FareTariff Tariff,
    double DistanceKm);

/// <summary>
/// Prices a trip and mints the token that binds the price.
/// </summary>
/// <remarks>
/// <b>STUB (C049/C050).</b> Implements the D5' §1.1 master formula with two deliberate holes,
/// each marked at the line that owns it: distance is straight-line rather than routed, and no
/// peak or night surcharge is ever applied. Both are what "a flat estimate so the flow can
/// complete" (the C022 scope) means; neither is safe to ship to a passenger.
/// </remarks>
public sealed class FareEstimator(FareEstimateTokenCodec tokens)
{
    /// <summary>The kinds <c>GET /v1/fare/estimate</c> accepts (D3' fare-svc).</summary>
    public const string PassengerKind = "passenger";
    public const string PackageKind = "package";

    /// <summary>
    /// Sri Lanka's bounding box, with a coastal margin.
    /// <para>
    /// <b>STUB (C049).</b> The real answer is <c>config.operating_cities</c> (C005 migration
    /// 0201) — a per-city service polygon an admin edits. A box cannot tell Colombo from a
    /// jungle, so this only catches a caller who is on the wrong continent, which is exactly
    /// what <c>422 unserviceable-area</c> is for and no more.
    /// </para>
    /// </summary>
    private const double MinLatitude = 5.5;
    private const double MaxLatitude = 10.2;
    private const double MinLongitude = 79.2;
    private const double MaxLongitude = 82.2;

    private const double EarthRadiusKm = 6371.0088;

    public FareQuote Quote(GeoPoint pickup, GeoPoint dropoff, string vehicleType, string kind)
    {
        if (!FareTariff.TryGet(vehicleType, out var tariff))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["vehicleType"] =
                [
                    "vehicleType must be one of " +
                    string.Join(", ", FareTariff.BookableVehicleTypes.Order(StringComparer.Ordinal)) +
                    " (AL-09). 'bus' and 'train' are Mode A and carry no fare.",
                ],
            });
        }

        if (kind is not (PassengerKind or PackageKind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["kind"] = [$"kind must be '{PassengerKind}' or '{PackageKind}'."],
            });
        }

        RequireServiceable(pickup, "pickup");
        RequireServiceable(dropoff, "dropoff");

        // STUB (C049): D5' §1.2 prices the estimate on the OSRM/Valhalla *route* distance. This
        // is the haversine straight line, so every quote is low by whatever the road detour is.
        var distanceKm = HaversineKm(pickup, dropoff);

        // D5' §1.1: the first kilometre is inside the first-km charge.
        var extraKm = Math.Max(0, distanceKm - 1.0);

        // D5' §1.3 computes in minor units with a single round where a product is taken. Away
        // from zero rather than banker's: the amount is always positive and a passenger reading
        // "Rs 480" should not need to know which way 0.5 fell.
        var baseMinor = tariff.FirstKmMinor + (long)Math.Round(extraKm * tariff.PerKmMinor, MidpointRounding.AwayFromZero);

        // STUB (C049): D5' §1.1 stacks peak (+20%) and night (+15%) on baseMinor, evaluated in
        // Asia/Colombo against fares.peak_windows (D-38). The stub never surcharges, so a 07:30
        // quote and a 14:00 quote are the same price.
        const long SurchargeMinor = 0;

        var amountMinor = baseMinor + SurchargeMinor;

        var token = tokens.Issue(
            vehicleType: tariff.VehicleType,
            kind: kind,
            amountMinor: amountMinor,
            surchargeMinor: SurchargeMinor,
            distanceKm: distanceKm,
            pickup: pickup,
            dropoff: dropoff);

        return new FareQuote(token, amountMinor, SurchargeMinor, tariff, distanceKm);
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

    /// <summary>Great-circle distance in kilometres (D5' §1.2, "straight-line proximity = haversine").</summary>
    internal static double HaversineKm(GeoPoint from, GeoPoint to)
    {
        var dLat = double.DegreesToRadians(to.Latitude - from.Latitude);
        var dLng = double.DegreesToRadians(to.Longitude - from.Longitude);

        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(double.DegreesToRadians(from.Latitude))
                   * Math.Cos(double.DegreesToRadians(to.Latitude))
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));

        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}
