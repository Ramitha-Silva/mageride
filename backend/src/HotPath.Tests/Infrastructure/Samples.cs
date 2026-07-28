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

    /// <summary>Dehiwala, ~9 km south — a different res-7 cell, and a different res-5 one.</summary>
    public static readonly GeoPoint Dehiwala = new(6.8514, 79.8653);

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
