using MageRide.Query.Geo;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Query.Tests.Unit;

/// <summary>
/// The wire form of a trip's track (MAP-08).
/// </summary>
/// <remarks>
/// Asserted against the published algorithm's own worked example as well as a round trip. A round trip
/// alone would pass for an encoder that is self-consistent and wrong, which is exactly the failure mode
/// that matters: MapLibre and every mobile map library decode this, so the client is what would show a
/// line running off the road.
/// </remarks>
public sealed class EncodedPolylineTests
{
    /// <summary>
    /// The example from Google's Encoded Polyline Algorithm documentation:
    /// <c>(38.5, -120.2), (40.7, -120.95), (43.252, -126.453)</c> encodes to
    /// <c>_p~iF~ps|U_ulLnnqC_mqNvxq`@</c>.
    /// </summary>
    [Fact]
    public void The_published_worked_example_encodes_byte_for_byte()
    {
        var path = new[]
        {
            new GeoPoint(38.5, -120.2),
            new GeoPoint(40.7, -120.95),
            new GeoPoint(43.252, -126.453),
        };

        Assert.Equal("_p~iF~ps|U_ulLnnqC_mqNvxq`@", EncodedPolyline.Encode(path));
    }

    [Fact]
    public void A_Colombo_route_round_trips_to_five_decimals()
    {
        var path = new[]
        {
            new GeoPoint(6.93440, 79.84280),
            new GeoPoint(6.93101, 79.84402),
            new GeoPoint(6.92715, 79.84490),
            new GeoPoint(6.92133, 79.84771),
        };

        var decoded = EncodedPolyline.Decode(EncodedPolyline.Encode(path));

        Assert.Equal(path.Length, decoded.Count);

        for (var i = 0; i < path.Length; i++)
        {
            Assert.Equal(path[i].Latitude, decoded[i].Latitude, 5);
            Assert.Equal(path[i].Longitude, decoded[i].Longitude, 5);
        }
    }

    /// <summary>
    /// Quantising before differencing is what stops error accumulating along a long line. A route of two
    /// hundred closely spaced points is where the difference shows.
    /// </summary>
    [Fact]
    public void Error_does_not_accumulate_along_a_long_line()
    {
        var path = Enumerable.Range(0, 200)
            .Select(i => new GeoPoint(6.9344 + (i * 0.000_37), 79.8428 + (i * 0.000_41)))
            .ToArray();

        var decoded = EncodedPolyline.Decode(EncodedPolyline.Encode(path));

        Assert.Equal(path.Length, decoded.Count);

        // Every point, including the last, within half a unit of the 1e-5 grid.
        for (var i = 0; i < path.Length; i++)
        {
            Assert.True(Math.Abs(path[i].Latitude - decoded[i].Latitude) <= 0.000_005);
            Assert.True(Math.Abs(path[i].Longitude - decoded[i].Longitude) <= 0.000_005);
        }
    }

    /// <summary>
    /// A line needs two positions. One point is <see langword="null"/> rather than a one-point string,
    /// which is the same decision <c>trips.session_summaries.polyline</c> stores as NULL.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Fewer_than_two_points_is_no_line_at_all(int count)
    {
        var path = Enumerable.Range(0, count).Select(i => new GeoPoint(6.9 + i, 79.8)).ToArray();

        Assert.Null(EncodedPolyline.Encode(path));
    }

    [Fact]
    public void Nothing_decodes_to_nothing()
    {
        Assert.Empty(EncodedPolyline.Decode(null));
        Assert.Empty(EncodedPolyline.Decode(string.Empty));
    }

    /// <summary>A truncated string is a format error, not a silently short line.</summary>
    [Fact]
    public void A_value_cut_mid_chunk_is_rejected()
    {
        var encoded = EncodedPolyline.Encode(
            [new GeoPoint(6.9344, 79.8428), new GeoPoint(6.9271, 79.8449)])!;

        Assert.Throws<FormatException>(() => EncodedPolyline.Decode(encoded[..^1]));
    }
}
