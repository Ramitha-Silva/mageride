using MageRide.Query.Configuration;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Live;

/// <summary>
/// US-7.11's arrival estimate.
/// </summary>
/// <remarks>
/// <para>
/// <b>A straight line with a detour factor, and it says so.</b> ADD §7.6 puts routing
/// (OSRM/Valhalla, snap-to-road) in <b>Phase 3</b>: in Phase 1 there is no road network on the
/// platform to measure a path against, so a routed ETA is not something this service can compute. The
/// alternatives were to omit the field — C041 already deferred it here once, and deferring again
/// would mean the contract's <c>etaSeconds</c> is never populated by anything — or to publish an
/// estimate whose method is stated. This is the second, with every assumption a setting rather than a
/// constant so it can be retuned against observed arrivals and deleted when the router lands.
/// </para>
/// <para>
/// <b>Speed comes from the vehicle when the vehicle is moving, and from its type when it is not.</b>
/// A taxi stopped at a light reports ~0 m/s and dividing by that gives an ETA of hours. Below
/// <see cref="QueryOptions.EtaMinSpeedMps"/> the per-type average is used instead — an average
/// *including* stops, which is why those figures are a third of ADD §12.6's anti-spoof ceilings. Those
/// are the speeds above which a fix is a lie; these are the speeds a vehicle actually crosses a city
/// at.
/// </para>
/// <para>
/// <b>Nothing is returned above <see cref="QueryOptions.MaxEta"/>.</b> "Arriving in 94 minutes" for a
/// bus at the far edge of a 20 km search is arithmetically sound and not a thing a passenger should
/// plan around; an absent field is a truer statement than a number at the limit of the method.
/// </para>
/// </remarks>
public sealed class EtaEstimator(IOptions<QueryOptions> options)
{
    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Seconds for <paramref name="vehicle"/> to reach <paramref name="target"/>, or
    /// <see langword="null"/> when no defensible estimate exists.
    /// </summary>
    public int? Estimate(LiveVehicle vehicle, GeoPoint target)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!_options.EtaEnabled)
        {
            return null;
        }

        var straightLineM = GeoMath.DistanceM(vehicle.Point, target);
        var roadM = straightLineM * _options.EtaDetourFactor;

        var speedMps = vehicle.SpeedMps is { } reported && reported >= _options.EtaMinSpeedMps
            ? reported
            : AssumedSpeedMps(vehicle.Type);

        if (speedMps <= 0)
        {
            return null;
        }

        var seconds = roadM / speedMps;

        return seconds > _options.MaxEta.TotalSeconds ? null : (int)Math.Round(seconds);
    }

    private double AssumedSpeedMps(string? vehicleType)
    {
        var kph = vehicleType is { Length: > 0 } type && _options.EtaSpeedKph.TryGetValue(type, out var configured)
            ? configured
            : _options.DefaultEtaSpeedKph;

        return kph * 1000d / 3600d;
    }
}
