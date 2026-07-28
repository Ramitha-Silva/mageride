using H3;
using H3.Algorithms;
using H3.Extensions;
using MageRide.Shared.Primitives;
using NetTopologySuite.Geometries;

namespace MageRide.Dispatch.Domain;

/// <summary>
/// The H3 geocell arithmetic the candidate index is keyed by (ADD §7.4/§9.4, R-06, D-06).
/// </summary>
/// <remarks>
/// <para>
/// One cell id per driver position at <c>Dispatch:H3Resolution</c> (5), and
/// <c>gridDisk(pickupCell, k)</c> — D5' §3.1's <c>ring(1..2)</c> — as the coarse pre-filter around
/// a pickup. Nineteen cells at k=2.
/// </para>
/// <para>
/// <b>A cell is never a distance bound.</b> R-06 says so and D5' §3.1 marks the exact post-filter
/// MANDATORY; a res-5 hexagon has a ~9.9 km average edge, so <c>gridDisk(2)</c> reaches roughly 40
/// km across and a driver inside it may be many times the search radius away. This type is
/// deliberately the whole of what H3 decides: it produces the set of keys to read and stops there.
/// <see cref="Persistence.CandidateRepository"/> is what answers "how far away".
/// </para>
/// <para>
/// Cell ids are formatted lower-case hex, which is the canonical H3 v4 string form and the same
/// one <c>com.uber:h3</c> gives the KMP module (C017) — the two have to agree, because the apps
/// subscribe to cell-keyed SignalR groups the server publishes into.
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

    /// <summary>The cell containing <paramref name="point"/>, as a canonical lower-case hex id.</summary>
    public string CellAt(GeoPoint point) => Format(IndexAt(point));

    /// <summary>
    /// <paramref name="point"/>'s cell plus every cell within <c>ringK</c> steps of it — the keys
    /// a candidate build reads. Ordered nearest ring first, so a caller that wants to stop early
    /// reads the closest cells first.
    /// </summary>
    public IReadOnlyList<string> DiskAt(GeoPoint point)
    {
        var origin = IndexAt(point);

        return [.. origin
            .GridDiskDistances(_ringK)
            .OrderBy(static cell => cell.Distance)
            .Select(static cell => Format(cell.Index))];
    }

    /// <summary>Average edge length of a cell at this resolution, in metres. Diagnostics only.</summary>
    public double AverageEdgeLengthM => H3Index.GetHexagonEdgeLengthAverageInM(_resolution);

    private H3Index IndexAt(GeoPoint point) =>
        // NetTopologySuite's Coordinate is (x, y) = (longitude, latitude). Getting this backwards
        // produces a perfectly valid cell somewhere else on the planet, which is why the
        // conversion happens here once and nowhere else.
        new Coordinate(point.Longitude, point.Latitude).ToH3Index(_resolution);

    private static string Format(H3Index index) => index.ToString();
}
