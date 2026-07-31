using System.Globalization;
using System.Text;
using MageRide.Shared.Primitives;

namespace MageRide.Shared.Geo;

/// <summary>
/// Google's Encoded Polyline Algorithm, precision 5 — the wire form of a trip's track (MAP-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an encoded polyline and not a coordinate array.</b> D3' <c>TripDetail.polyline</c> is a
/// <c>string</c>, and the reason is size: a Mode A bus route simplified to 25 m holds hundreds of
/// points, and as JSON numbers that is four to five times the bytes. MapLibre's <c>LineLayer</c> and
/// every mobile map library read this format directly, so the client does no work either. The
/// algorithm is a published, stable, patent-free format despite the name — there is no Google service
/// involved and nothing here calls one (D3' map hard rule).
/// </para>
/// <para>
/// <b>Not <c>ST_AsEncodedPolyline</c>.</b> PostGIS has the function, and using it would put the wire
/// encoding inside a query — so a second endpoint reading the same column would either repeat the SQL
/// or disagree about precision. The repository returns points; this turns points into the transport's
/// representation, on one side of the wire, once.
/// </para>
/// <para>
/// <b>Deltas are quantised before differencing, not after.</b> Rounding each coordinate to its integer
/// grid and then subtracting is what makes the encoding round-trip: differencing first and rounding the
/// difference lets error accumulate along the line, and a long route drifts visibly off the road.
/// </para>
/// </remarks>
public static class EncodedPolyline
{
    /// <summary>1e5 — five decimal places, ~1.1 m at the equator.</summary>
    private const double Precision = 1e5;

    /// <summary>
    /// Encodes an ordered path, or <see langword="null"/> when there is no line to draw.
    /// </summary>
    /// <remarks>
    /// A single point is <see langword="null"/> rather than a one-point string: a line needs two
    /// distinct positions, and <c>trips.session_summaries.polyline</c> stores NULL for the same reason.
    /// A caller that wants "where did this trip start" has the summary's pickup.
    /// </remarks>
    public static string? Encode(IReadOnlyList<GeoPoint>? path)
    {
        if (path is null || path.Count < 2)
        {
            return null;
        }

        var encoded = new StringBuilder(path.Count * 6);
        long previousLat = 0;
        long previousLng = 0;

        foreach (var point in path)
        {
            var lat = (long)Math.Round(point.Latitude * Precision, MidpointRounding.AwayFromZero);
            var lng = (long)Math.Round(point.Longitude * Precision, MidpointRounding.AwayFromZero);

            AppendValue(encoded, lat - previousLat);
            AppendValue(encoded, lng - previousLng);

            previousLat = lat;
            previousLng = lng;
        }

        return encoded.ToString();
    }

    /// <summary>
    /// Decodes an encoded polyline. Used by the test suite to assert a round trip.
    /// </summary>
    /// <remarks>
    /// Kept beside the encoder rather than in the tests: an encoder verified only against a
    /// hand-written expected string is verified against whatever the author believed the algorithm was.
    /// </remarks>
    public static IReadOnlyList<GeoPoint> Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return [];
        }

        var points = new List<GeoPoint>();
        var index = 0;
        long lat = 0;
        long lng = 0;

        while (index < encoded.Length)
        {
            lat += ReadValue(encoded, ref index);
            lng += ReadValue(encoded, ref index);

            points.Add(new GeoPoint(
                Math.Round(lat / Precision, 5, MidpointRounding.AwayFromZero),
                Math.Round(lng / Precision, 5, MidpointRounding.AwayFromZero)));
        }

        return points;
    }

    private static void AppendValue(StringBuilder builder, long value)
    {
        // Zig-zag: the sign becomes the low bit, so a small negative delta stays a short encoding.
        var shifted = value < 0 ? ~(value << 1) : value << 1;

        while (shifted >= 0x20)
        {
            builder.Append((char)((0x20 | (int)(shifted & 0x1f)) + 63));
            shifted >>= 5;
        }

        builder.Append((char)((int)shifted + 63));
    }

    private static long ReadValue(string encoded, ref int index)
    {
        long result = 0;
        var shift = 0;
        int chunk;

        do
        {
            if (index >= encoded.Length)
            {
                throw new FormatException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Encoded polyline ended mid-value at index {index}."));
            }

            chunk = encoded[index++] - 63;
            result |= (long)(chunk & 0x1f) << shift;
            shift += 5;
        }
        while (chunk >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }
}
