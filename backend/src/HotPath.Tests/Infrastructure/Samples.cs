using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>Positions and samples the suite works from.</summary>
/// <remarks>
/// The coordinates are far enough apart to fall in different H3 res-7 cells, which several
/// assertions depend on: a res-7 hexagon is about 1.2 km on a side, so "a different cell" means
/// kilometres, not metres.
/// </remarks>
internal static class Samples
{
    /// <summary>Colombo Fort.</summary>
    public static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    /// <summary>
    /// Dehiwala, ~9.6 km south — a different res-7 cell, and the <b>same</b> res-5 one.
    /// </summary>
    /// <remarks>
    /// Δ C039: this was documented as a different res-5 cell and is not — both fall in
    /// <c>85611cb3fffffff</c>. A res-5 hexagon averages 252 km², so ten kilometres is comfortably
    /// inside one. Use <see cref="Moratuwa"/> where a res-5 boundary has to be crossed.
    /// </remarks>
    public static readonly GeoPoint Dehiwala = new(6.8514, 79.8653);

    /// <summary>
    /// Moratuwa, ~18.5 km south — a different res-5 cell, and reachable at a lawful speed.
    /// </summary>
    /// <remarks>
    /// The R-08 candidate index is keyed at res 5, so a test that moves a driver between cells needs
    /// a step this long. It is also short enough that the D-18 teleport gate accepts it over any
    /// realistic interval: 18.5 km is 14 minutes at a three-wheeler's 80 km/h ceiling.
    /// </remarks>
    public static readonly GeoPoint Moratuwa = new(6.7730, 79.8816);

    /// <summary>Kandy, ~95 km inland. Nothing in Colombo's view reaches it.</summary>
    public static readonly GeoPoint Kandy = new(7.2906, 80.6337);

    /// <summary>A well-formed live sample for <paramref name="vehicleId"/>.</summary>
    public static PositionSample At(
        Guid vehicleId,
        GeoPoint point,
        long seq = 1,
        string vehicleType = "three_wheeler",
        string mode = "C",
        DateTimeOffset? sampleTs = null) =>
        new(
            vehicleId,
            sampleTs ?? DateTimeOffset.UtcNow,
            seq,
            point.Latitude,
            point.Longitude,
            PositionSource.Mobile,
            SpeedMps: 8.5,
            HeadingDeg: 90,
            AccuracyM: 6.0,
            Mode: mode,
            VehicleType: vehicleType);
}
