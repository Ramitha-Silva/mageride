using H3;
using H3.Extensions;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using NetTopologySuite.Geometries;

namespace MageRide.Dispatch.Tests.Domain;

/// <summary>
/// The H3 arithmetic the candidate index is keyed by (ADD §9.4, R-06, D-06).
/// </summary>
/// <remarks>
/// These assert against <b>known-good H3 v4 values</b> rather than against whatever the library
/// returns. The KMP module computes the same cells through <c>com.uber:h3</c> (C017) and the apps
/// subscribe to cell-keyed groups the server publishes into; a port that disagreed by one bit
/// would not fail loudly, it would produce an empty candidate set and a map with no vehicles on it.
/// </remarks>
public sealed class H3GridTests
{
    /// <summary>Colombo Fort. Its res-5 parent, from the reference implementation.</summary>
    private const string ColomboFortRes5 = "85611cb3fffffff";

    private static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    [Fact]
    public void Cell_at_res_5_matches_the_reference_implementation()
    {
        var grid = new H3Grid(resolution: 5, ringK: 2);

        Assert.Equal(ColomboFortRes5, grid.CellAt(ColomboFort));
    }

    [Fact]
    public void Cell_id_is_lower_case_hex_like_the_KMP_module_produces()
    {
        var cell = new H3Grid(5, 2).CellAt(ColomboFort);

        Assert.Equal(cell.ToLowerInvariant(), cell);
        Assert.Equal(15, cell.Length);
        Assert.All(cell, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Latitude_and_longitude_are_not_transposed()
    {
        // NetTopologySuite's Coordinate is (x, y) = (lng, lat). Getting it backwards produces a
        // perfectly valid cell in the Indian Ocean and no error anywhere, so the check is that the
        // cell's own centre comes back near Colombo rather than near (79.84 N, 6.93 E).
        var index = new Coordinate(ColomboFort.Longitude, ColomboFort.Latitude).ToH3Index(5);
        var centre = index.ToLatLng();

        Assert.InRange(centre.LatitudeDegrees, 6.0, 8.0);
        Assert.InRange(centre.LongitudeDegrees, 79.0, 81.0);
    }

    [Fact]
    public void Disk_of_k_2_is_the_19_cells_D5_3_1_asks_for()
    {
        var cells = new H3Grid(5, 2).DiskAt(ColomboFort);

        // ring(0) + ring(1) + ring(2) = 1 + 6 + 12.
        Assert.Equal(19, cells.Count);
        Assert.Equal(19, cells.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Disk_starts_at_the_pickup_cell_so_the_nearest_keys_are_read_first()
    {
        var cells = new H3Grid(5, 2).DiskAt(ColomboFort);

        Assert.Equal(ColomboFortRes5, cells[0]);
    }

    [Fact]
    public void Ring_k_0_is_the_pickup_cell_alone()
    {
        Assert.Equal([ColomboFortRes5], new H3Grid(5, 0).DiskAt(ColomboFort));
    }

    /// <summary>
    /// The reason R-06 marks the exact post-filter mandatory, stated as a number: a res-5 hexagon's
    /// average edge is nearly 10 km, so two drivers in the same cell can be 20 km apart before the
    /// ring is even considered.
    /// </summary>
    [Fact]
    public void A_res_5_cell_is_far_too_coarse_to_be_a_distance_bound()
    {
        var grid = new H3Grid(5, 2);

        Assert.InRange(grid.AverageEdgeLengthM, 8_000, 12_000);
        Assert.True(
            grid.AverageEdgeLengthM > 5_000,
            "A res-5 edge must exceed the default 5 km search radius, or the pre-filter would " +
            "accidentally be a distance bound and the post-filter would look optional.");
    }

    [Fact]
    public void Two_points_10_metres_apart_share_a_cell()
    {
        var grid = new H3Grid(5, 2);
        var nudged = new GeoPoint(ColomboFort.Latitude + 0.0001, ColomboFort.Longitude);

        Assert.Equal(grid.CellAt(ColomboFort), grid.CellAt(nudged));
    }

    [Fact]
    public void A_point_100_km_away_is_outside_the_disk()
    {
        var grid = new H3Grid(5, 2);
        var kandy = new GeoPoint(7.2906, 80.6337);

        Assert.DoesNotContain(grid.CellAt(kandy), grid.DiskAt(ColomboFort));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void An_impossible_resolution_is_refused(int resolution) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new H3Grid(resolution, 2));

    [Fact]
    public void A_negative_ring_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new H3Grid(5, -1));

    /// <summary>
    /// The index key is <c>geo:drivers:available:{vehicleType}:{h3Res5Cell}</c> (ADD §9.4).
    /// position-processor-svc (C039) writes it and dispatch-svc reads it, so it is asserted here
    /// against the ADD's literal pattern rather than against the helper that builds it.
    /// </summary>
    [Fact]
    public void The_candidate_index_key_is_the_one_ADD_9_4_prints()
    {
        var cell = new H3Grid(5, 2).CellAt(ColomboFort);

        Assert.Equal(
            $"geo:drivers:available:three_wheeler:{ColomboFortRes5}",
            RedisKeys.AvailableDrivers("three_wheeler", cell));
    }

    [Fact]
    public void Res_5_is_a_parent_of_the_res_7_passenger_view_cell()
    {
        // R-06 pairs a res-7 + ring(2) passenger view with a res-5 dispatch pre-filter. They are
        // the same grid, so the dispatch cell must be the passenger cell's ancestor — the property
        // that lets one position feed both indexes.
        var res7 = new Coordinate(ColomboFort.Longitude, ColomboFort.Latitude).ToH3Index(7);
        var parent = res7.GetParentForResolution(5);

        Assert.Equal(ColomboFortRes5, parent.ToString());
    }
}
