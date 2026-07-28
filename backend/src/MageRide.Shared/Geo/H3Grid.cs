using H3;
using H3.Algorithms;
using H3.Extensions;
using MageRide.Shared.Primitives;
using NetTopologySuite.Geometries;

namespace MageRide.Shared.Geo;

/// <summary>
/// The H3 geocell arithmetic the platform's two cell-keyed indexes are built on (ADD §7.4/§9.4,
/// R-06, D-06).
/// </summary>
/// <remarks>
/// <para>
/// One cell id per point at a fixed resolution, and <c>gridDisk(origin, k)</c> — H3's ring — as the
/// set of cells around it. Two callers, two resolutions: <c>dispatch-svc</c> keys its candidate
/// index at res 5 and pre-filters with <c>ring(1..2)</c> (D5' §3.1), and the fan-out plane keys
/// <c>cell:{h3index}</c> at res 7 where a passenger's 3 km view is <c>ring(2)</c> = 19 cells
/// (R-06). <see cref="GeoCells"/> holds those numbers; this type holds only the arithmetic.
/// </para>
/// <para>
/// <b>A cell is never a distance bound.</b> R-06 says so and D5' §3.1 marks the exact post-filter
/// MANDATORY; a res-5 hexagon has a ~9.9 km average edge, so <c>gridDisk(2)</c> reaches roughly 40
/// km across and a driver inside it may be many times the search radius away. This type is
/// deliberately the whole of what H3 decides: it produces the set of keys to read and stops there.
/// </para>
/// <para>
/// Cell ids are formatted lower-case hex, which is the canonical H3 v4 string form and the same one
/// <c>com.uber:h3</c> gives the KMP module (C012/C017) — the two have to agree, because the apps
/// subscribe to cell-keyed SignalR groups the server publishes into.
/// </para>
/// <para>
/// Promoted here from <c>Dispatch.Api/Domain/H3Grid.cs</c> by C024, when the fan-out plane became
/// the second caller. Backend conventions put cross-cutting code in the kernel, and two copies of
/// a grid whose ids must be bit-identical is exactly the drift that shows up as an empty map.
/// </para>
/// </remarks>
public sealed class H3Grid(int resolution, int ringK)
{
    private readonly int _resolution = resolution is >= 0 and <= 15
        ? resolution
        : throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "H3 resolutions run 0..15.");

    private readonly int _ringK = ringK >= 0
        ? ringK
        : throw new ArgumentOutOfRangeException(nameof(ringK), ringK, "A grid disk radius cannot be negative.");

    /// <summary>The resolution every id this grid produces is at.</summary>
    public int Resolution => _resolution;

    /// <summary>How many rings out <see cref="DiskAt"/> reaches.</summary>
    public int RingK => _ringK;

    /// <summary>The cell containing <paramref name="point"/>, as a canonical lower-case hex id.</summary>
    public string CellAt(GeoPoint point) => Format(IndexAt(point));

    /// <summary>
    /// <paramref name="point"/>'s cell plus every cell within <c>ringK</c> steps of it — the keys a
    /// candidate build reads, or the groups a passenger joins. Ordered nearest ring first, so a
    /// caller that wants to stop early reads the closest cells first.
    /// </summary>
    public IReadOnlyList<string> DiskAt(GeoPoint point) => DiskAt(point, _ringK);

    /// <summary>
    /// <see cref="DiskAt(GeoPoint)"/> with an explicit ring size, for the callers that hold more
    /// than one view — the 3 km passenger map is <c>k = 2</c> and the intercity one <c>k = 3</c>
    /// (ADD §7.4 step 4).
    /// </summary>
    public IReadOnlyList<string> DiskAt(GeoPoint point, int k)
    {
        if (k < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "A grid disk radius cannot be negative.");
        }

        return [.. IndexAt(point)
            .GridDiskDistances(k)
            .OrderBy(static cell => cell.Distance)
            .Select(static cell => Format(cell.Index))];
    }

    /// <summary>Average edge length of a cell at this resolution, in metres. Diagnostics only.</summary>
    public double AverageEdgeLengthM => H3Index.GetHexagonEdgeLengthAverageInM(_resolution);

    /// <summary>
    /// Whether <paramref name="cell"/> is a well-formed H3 id at this grid's resolution.
    /// </summary>
    /// <remarks>
    /// The fan-out hub takes cell ids from clients, so "is this a cell at all" is an input check
    /// rather than a diagnostic: an unparseable string would otherwise become a SignalR group name
    /// nothing ever publishes to, and the passenger would see an empty map instead of an error.
    /// </remarks>
    public bool IsValidCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return false;
        }

        if (!ulong.TryParse(cell, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        var index = new H3Index(raw);
        return index.IsValidCell && index.Resolution == _resolution;
    }

    private H3Index IndexAt(GeoPoint point) =>
        // NetTopologySuite's Coordinate is (x, y) = (longitude, latitude). Getting this backwards
        // produces a perfectly valid cell somewhere else on the planet, which is why the
        // conversion happens here once and nowhere else.
        new Coordinate(point.Longitude, point.Latitude).ToH3Index(_resolution);

    private static string Format(H3Index index) => index.ToString();
}
