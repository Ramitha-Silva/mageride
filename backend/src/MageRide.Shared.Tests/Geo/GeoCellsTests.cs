using H3;
using H3.Extensions;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using NetTopologySuite.Geometries;

namespace MageRide.Shared.Tests.Geo;

/// <summary>
/// The fan-out half of the geocell rules: res-7 groups and the 19-cell 3 km passenger view
/// (R-06, ADD §7.4, <c>backend/contracts/realtime/signalr-hub.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>R-06 is a correction and the wrong figure is still in circulation.</b> Earlier ADD text said
/// "res-8 + ring(1) ≈ 3 km"; res-8's edge is ~0.46 km, so that view is about 1 km across and the
/// passenger sees a third of the vehicles they should. The KMP module fails its own build if
/// resolution 8 appears anywhere in its geo package; this is the server-side equivalent, asserting
/// the resolution and the count rather than trusting either.
/// </para>
/// <para>
/// The numbers matter because nothing downstream errors when they are wrong. A client that computed
/// its cells at another resolution joins SignalR groups the server never publishes into, and the
/// symptom is an empty map, not an exception.
/// </para>
/// </remarks>
public sealed class GeoCellsTests
{
    /// <summary>
    /// Colombo Fort at res 7. Anchored to <see cref="ColomboFortRes5"/>: an H3 id is a base cell
    /// followed by one 3-bit digit per resolution, so a res-7 id whose first five digits are the
    /// known-good res-5 id's is the res-5 cell's descendant by construction —
    /// <c>The_dispatch_cell_is_the_view_cell_s_ancestor</c> below is that check. Both are base cell
    /// 48, digits 4·3·4·5·4, and res 7 adds 3·6.
    /// </summary>
    private const string ColomboFortRes7 = "87611cb1effffff";

    /// <summary>The same point at res 5 — the dispatch index key, from the reference implementation.</summary>
    private const string ColomboFortRes5 = "85611cb3fffffff";

    private static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    [Fact]
    public void The_fan_out_resolution_is_7_and_the_dispatch_resolution_is_5()
    {
        Assert.Equal(7, GeoCells.ViewResolution);
        Assert.Equal(5, GeoCells.DispatchResolution);
    }

    [Fact]
    public void The_passenger_view_cell_matches_the_reference_implementation()
    {
        Assert.Equal(ColomboFortRes7, GeoCells.ViewCell(ColomboFort));
        Assert.Equal(7, GeoCells.PassengerView.Resolution);
    }

    [Fact]
    public void The_3_km_passenger_view_is_exactly_19_cells()
    {
        var cells = GeoCells.ViewCells(ColomboFort);

        // ring(0) + ring(1) + ring(2) = 1 + 6 + 12. This is the number R-06 fixes and the number
        // this component's definition of done names.
        Assert.Equal(GeoCells.PassengerViewCellCount, cells.Count);
        Assert.Equal(19, cells.Count);
        Assert.Equal(19, cells.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ColomboFortRes7, cells[0]);
    }

    [Fact]
    public void The_5_km_intercity_view_is_37_cells()
    {
        var cells = GeoCells.ViewCells(ColomboFort, GeoCells.IntercityViewRing);

        Assert.Equal(37, cells.Count);
        Assert.Equal(GeoCells.HexagonDiskSize(GeoCells.IntercityViewRing), cells.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 7)]
    [InlineData(2, 19)]
    [InlineData(3, 37)]
    public void A_hexagon_disk_holds_1_plus_3k_k_plus_1_cells(int k, int expected) =>
        Assert.Equal(expected, GeoCells.HexagonDiskSize(k));

    /// <summary>
    /// The res-7 view covers roughly 3 km, which is the whole point of R-06's correction. Asserted
    /// as an edge length rather than a radius because that is the number H3 itself reports.
    /// </summary>
    [Fact]
    public void A_res_7_edge_is_about_1_2_km_so_ring_2_reaches_about_3_km()
    {
        Assert.InRange(GeoCells.PassengerView.AverageEdgeLengthM, 1_000, 1_500);
    }

    [Fact]
    public void The_dispatch_cell_is_the_view_cell_s_ancestor()
    {
        // One position feeds both indexes. Because res 5 is a parent of res 7 in the same grid, the
        // driver a passenger can see in `cell:{res7}` is the same driver dispatch finds in
        // `geo:drivers:available:{type}:{res5}` — they are two views of one point, not two systems.
        var res7 = new Coordinate(ColomboFort.Longitude, ColomboFort.Latitude).ToH3Index(7);

        Assert.Equal(ColomboFortRes5, res7.GetParentForResolution(5).ToString());
    }

    [Fact]
    public void The_group_name_is_the_stream_name_the_processor_writes()
    {
        // fanout-svc publishes to the SignalR group and position-processor-svc writes the Redis
        // stream; a disagreement here is a silent no-op, so the two are asserted against each other.
        var cell = GeoCells.ViewCell(ColomboFort);

        Assert.Equal($"cell:{cell}", GeoCells.CellGroup(cell));
        Assert.Equal(GeoCells.CellGroup(cell), RedisKeys.Cell(cell));
    }

    [Fact]
    public void The_boundary_hysteresis_is_the_30_seconds_ADD_7_4_step_6_asks_for() =>
        Assert.Equal(TimeSpan.FromSeconds(30), GeoCells.BoundaryHysteresis);

    [Fact]
    public void A_cell_from_another_resolution_is_not_a_valid_view_cell()
    {
        // JoinGeocells takes cell ids off the wire, so "is this a res-7 cell" is an input check.
        Assert.True(GeoCells.PassengerView.IsValidCell(ColomboFortRes7));
        Assert.False(GeoCells.PassengerView.IsValidCell(ColomboFortRes5));
        Assert.False(GeoCells.PassengerView.IsValidCell("not-a-cell"));
        Assert.False(GeoCells.PassengerView.IsValidCell(""));
        Assert.False(GeoCells.PassengerView.IsValidCell(null));
    }

    [Fact]
    public void An_id_that_parses_as_hex_but_is_not_a_cell_is_refused() =>
        // ffffffffffffffff parses, and every H3 id does too — validity is a bit-pattern question,
        // not a parse one. A group name built from this would simply never receive anything.
        Assert.False(GeoCells.PassengerView.IsValidCell("ffffffffffffffff"));
}
