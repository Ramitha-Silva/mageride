using System.Globalization;

namespace MageRide.Shared.Primitives;

/// <summary>
/// A WGS-84 position — the shape every MageRide payload uses for a coordinate
/// (<c>{"lat":6.9271,"lng":79.8612}</c>, D6' §2.2) and the CLR side of a
/// <c>geography(Point,4326)</c> column (ADD §9.1).
/// </summary>
/// <remarks>
/// Latitude/longitude order matters: PostGIS <c>geography</c> stores (longitude, latitude), the
/// JSON payloads carry lat first. The Dapper type handler is the only place that conversion
/// happens, so nothing else has to remember.
/// </remarks>
public readonly record struct GeoPoint
{
    /// <summary>SRID for WGS-84, the only spatial reference the platform stores.</summary>
    public const int Wgs84Srid = 4326;

    public GeoPoint(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90.");
        }

        if (double.IsNaN(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({Latitude:0.######}, {Longitude:0.######})");
}
