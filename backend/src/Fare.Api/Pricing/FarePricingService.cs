using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.Shared.Time;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Pricing;

/// <summary>Which surcharge windows an instant fell in, evaluated in Asia/Colombo (D-38).</summary>
public sealed record SurchargeWindows(bool IsPeak, bool IsNight)
{
    public static readonly SurchargeWindows None = new(false, false);
}

/// <summary>
/// The one place a price is computed: resolve the tariff, resolve the windows, apply D5' §1.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>One engine for the estimate and the settlement</b> (D5' §1.4, verbatim: "same engine"). The
/// only difference between a quote and a final fare is which distance and which instant are handed
/// in — the estimate uses the route distance and the moment of quoting, the settlement uses the
/// Kalman-filtered track and the moment the ride was <em>requested</em>. Two code paths would be two
/// chances for a passenger to be charged a number they were never shown.
/// </para>
/// <para>
/// <b>The window decides <em>whether</em>; the tariff decides <em>how much</em>.</b> D5' §1.1 is
/// explicit — <c>peakPct = isPeak(rideTime) ? tariff.peak_surcharge_pct : 0</c> — so
/// <c>fares.peak_windows.multiplier_pct</c> is not read. It is seeded to the same 20/15 the tariffs
/// carry, so the two agree today; an admin who set them apart would find only the tariff's mattered.
/// Raised as a spec gap in the C049 handoff.
/// </para>
/// </remarks>
internal sealed class FarePricingService(ITariffRepository tariffs, IOptions<FareOptions> options)
{
    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Prices a trip of <paramref name="distanceKm"/> taken at <paramref name="at"/>.
    /// </summary>
    /// <param name="at">
    /// The instant that decides <b>both</b> the tariff version and the surcharge windows. For a
    /// settlement that is when the ride was requested, so a rate published mid-journey cannot
    /// re-price it and a trip that began at 08:55 is a peak trip even if it ended at 09:30.
    /// </param>
    public async Task<FareBreakdown> PriceAsync(
        string vehicleType, double distanceKm, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var tariff = await tariffs.ResolveAsync(vehicleType, at, cancellationToken)
                     ?? throw NoTariff(vehicleType);

        var windows = await WindowsAtAsync(at, cancellationToken);

        return FareFormula.Price(tariff, distanceKm, windows.IsPeak, windows.IsNight);
    }

    /// <summary>Which windows an instant falls in.</summary>
    public async Task<SurchargeWindows> WindowsAtAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        var windows = await tariffs.ListWindowsAsync(cancellationToken);

        // The wall-clock in Colombo, which is the only frame these windows are meaningful in: 22:00
        // means ten at night where the vehicle is, not ten at night in UTC (D-38).
        var local = TimeOnly.FromDateTime(BusinessCalendar.ToLocal(at).DateTime);

        var isPeak = windows.Any(w => string.Equals(w.Kind, PeakWindow.Peak, StringComparison.Ordinal) && w.Covers(local));
        var isNight = windows.Any(w => string.Equals(w.Kind, PeakWindow.Night, StringComparison.Ordinal) && w.Covers(local));

        return new SurchargeWindows(isPeak, isNight);
    }

    /// <summary>
    /// The road distance an estimate is priced on.
    /// </summary>
    /// <remarks>
    /// <b>A straight line with a detour factor, and an interim</b> — see
    /// <see cref="FareOptions.RouteDetourFactor"/>. This is the seam OSRM lands behind: everything
    /// above it takes a distance and does not care where it came from, which is why the settlement
    /// path (a real, measured track) shares the whole of the rest of this class.
    /// </remarks>
    public double RouteDistanceKm(GeoPoint pickup, GeoPoint dropoff) =>
        GeoMath.DistanceM(pickup, dropoff) / 1_000.0 * _options.RouteDetourFactor;

    /// <summary>
    /// A vehicle type with no configured tariff is refused, never guessed.
    /// </summary>
    /// <remarks>
    /// §20 seeds no rate for <c>truck</c> or <c>mini_truck</c> deliberately — Epic 20 leaves delivery
    /// rates to be configured before such a vehicle can be booked — and the C022 stub's invented
    /// numbers for them are the first thing this component deletes. Inventing a rate bills somebody
    /// an amount nobody chose; the same argument subscription-svc's daily fee makes for its own
    /// unconfigured types.
    /// </remarks>
    private static MageRideException NoTariff(string vehicleType) =>
        new(
            MageRideErrors.RouteUnavailable,
            $"No Mode C tariff is configured for '{vehicleType}'. Package-delivery rates (truck, mini_truck) "
            + "are admin-configured per Epic 20 and are not seeded; a vehicle type cannot be priced until "
            + "Finance publishes a rate for it.");
}
