using MageRide.Shared.Primitives;

namespace MageRide.Shared.Geo;

/// <summary>
/// The geocell rules of ADD §7.4 and D5' §3.1, in one place — the server half of the KMP module's
/// <c>lk.mageride.shared.domain.geo.GeoCells</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>R-06 is a correction, and the wrong value is still in circulation.</b> Earlier ADD text
/// claimed "res-8 + ring(1) ≈ 3 km"; a res-8 edge is ~0.46 km, so <c>ring(1)</c> covers about 1 km
/// and a passenger would see a third of the vehicles they should. The corrected figure is
/// <b>res-7 + ring(2) = 19 cells ≈ 2.8–3.3 km</b> (ADD §7.4 step 4, D5' §3.1,
/// <c>backend/contracts/realtime/signalr-hub.md</c> §2). Nothing on the fan-out plane may name
/// resolution 8.
/// </para>
/// <para>
/// The client computes its own cells and sends them to <c>JoinGeocells</c>; the server computes the
/// cell of every position sample. Those two calculations have to land on the same ids or the
/// passenger joins groups nothing publishes to — which fails silently, as an empty map. That is why
/// both sides take their resolutions from a named constant rather than a literal.
/// </para>
/// </remarks>
public static class GeoCells
{
    /// <summary>
    /// H3 resolution 7 — ~5.16 km² per hexagon, ~1.22 km edge.
    /// </summary>
    /// <remarks>
    /// The fan-out granularity: <c>cell:{h3index}</c> Redis streams and the SignalR groups of the
    /// same name are res-7 (ADD §7.4 steps 1–3).
    /// </remarks>
    public const int ViewResolution = 7;

    /// <summary>
    /// H3 resolution 5 — ~252 km² per hexagon.
    /// </summary>
    /// <remarks>
    /// The dispatch driver index is keyed <c>geo:drivers:available:{type}:{res5cell}</c> and
    /// dispatch-svc scans <c>ring(1..2)</c> of the pickup's res-5 cell as a <b>coarse pre-filter</b>
    /// (D5' §3.1). Never a distance bound — the exact <c>ST_DWithin</c> post-filter is mandatory.
    /// </remarks>
    public const int DispatchResolution = 5;

    /// <summary>The 3 km passenger live map: res-7 self + <c>ring(2)</c> (R-06, US-7.3).</summary>
    public const int PassengerViewRing = 2;

    /// <summary>The wider intercity view: res-7 self + <c>ring(3)</c>, ~5 km (ADD §7.4 step 4).</summary>
    public const int IntercityViewRing = 3;

    /// <summary>How far dispatch's coarse pre-filter reaches out from the pickup cell (D5' §3.1).</summary>
    public const int DispatchPreFilterRing = 2;

    /// <summary>The 19 cells R-06 fixes for the 3 km passenger view — asserted, never assumed.</summary>
    public const int PassengerViewCellCount = 19;

    /// <summary>
    /// How long a group membership is held after the client leaves the cell (ADD §7.4 step 6,
    /// <c>signalr-hub.md</c> §2).
    /// </summary>
    /// <remarks>
    /// A passenger walking along a cell edge would otherwise join and leave the same six groups
    /// every few seconds, and every one of those is a <c>RemoveFromGroupAsync</c> on the backplane.
    /// </remarks>
    public static readonly TimeSpan BoundaryHysteresis = TimeSpan.FromSeconds(30);

    /// <summary>The res-7 view grid, at the 3 km passenger ring.</summary>
    public static H3Grid PassengerView { get; } = new(ViewResolution, PassengerViewRing);

    /// <summary>The res-5 dispatch grid, at D5' §3.1's <c>ring(1..2)</c> pre-filter reach.</summary>
    /// <remarks>
    /// dispatch-svc builds its own from <c>Dispatch:H3Resolution</c> / <c>:H3RingK</c> because an
    /// operator may want to retune the pre-filter's reach; position-processor-svc, which only ever
    /// needs the single cell a driver is <i>in</i>, takes it from here. The two must agree on the
    /// resolution or a driver would be indexed under a key no candidate build reads — which fails
    /// silently, as an empty candidate set. Asserted in <c>H3GridTests</c>.
    /// </remarks>
    public static H3Grid Dispatch { get; } = new(DispatchResolution, DispatchPreFilterRing);

    /// <summary>
    /// How many cells a hexagon-centred disk of radius <paramref name="k"/> holds:
    /// <c>1 + 3k(k + 1)</c>.
    /// </summary>
    /// <remarks>
    /// 19 at <c>k = 2</c> and 37 at <c>k = 3</c>. A disk centred on one of H3's twelve pentagons
    /// holds five fewer per ring; none of them is anywhere near Sri Lanka, but nothing here assumes
    /// the count — <see cref="H3Grid.DiskAt(GeoPoint, int)"/> returns what the grid actually has.
    /// </remarks>
    public static int HexagonDiskSize(int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        return 1 + (3 * k * (k + 1));
    }

    /// <summary>The res-7 cell a position falls in — the fan-out group and stream key.</summary>
    public static string ViewCell(GeoPoint point) => PassengerView.CellAt(point);

    /// <summary>The res-5 cell a position falls in — the <c>geo:drivers:available:*</c> key (R-08).</summary>
    public static string DispatchCell(GeoPoint point) => Dispatch.CellAt(point);

    /// <summary>
    /// The cells a client at <paramref name="centre"/> subscribes to. 19 for the default 3 km view.
    /// </summary>
    public static IReadOnlyList<string> ViewCells(GeoPoint centre, int ring = PassengerViewRing) =>
        PassengerView.DiskAt(centre, ring);

    /// <summary>The SignalR group and Redis stream suffix for a cell: <c>cell:{h3index}</c>.</summary>
    /// <remarks>
    /// The same string on both sides on purpose — <c>RedisKeys.Cell</c> names the stream
    /// position-processor-svc writes and this names the group fanout-svc publishes to, so a
    /// mismatch would be a silent no-op rather than an error.
    /// </remarks>
    public static string CellGroup(string h3Index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(h3Index);
        return $"cell:{h3Index}";
    }
}
