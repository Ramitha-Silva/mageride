using System.ComponentModel.DataAnnotations;

namespace MageRide.Fanout.Configuration;

/// <summary>
/// How the hub batches and how long it holds a group after a client leaves (<c>Fanout</c> section).
/// </summary>
public sealed class FanoutOptions
{
    public const string SectionName = "Fanout";

    /// <summary>
    /// Runs the cell-stream pump in this process. Off in tests that assert on the registry or the
    /// hub alone, so a background push cannot arrive under an assertion.
    /// </summary>
    public bool PumpEnabled { get; set; } = true;

    /// <summary>
    /// How often the pump drains the streams of the cells this replica has subscribers for.
    /// </summary>
    /// <remarks>
    /// <c>signalr-hub.md</c> §3 puts a <c>VehiclePositions</c> batch at "every 2–8 s (US-7.3)", and
    /// this is the bottom of that band — the component's SLO is "a position reaches the passenger in
    /// under 5 s (p95)", so the batch window has to be a small fraction of it. The band exists to
    /// stop a <b>per-fix</b> fan-out (5 msg/s per vehicle times every subscriber of its cell,
    /// ADD §7.4); batching at two seconds is still batching. C041 makes it adaptive.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:00:30")]
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Stream entries read per cell per tick.</summary>
    /// <remarks>
    /// A ceiling, not a target: a cell holding 200 vehicles at 1 Hz produces 400 entries in a
    /// two-second window and the batch only ever carries the newest frame per vehicle, so this
    /// bounds the read rather than the send. A cell that overruns it catches up on the next tick.
    /// </remarks>
    [Range(16, 10_000)]
    public int MaxEntriesPerCellPerTick { get; set; } = 512;

    /// <summary>
    /// How many recent frames a joining client is sent immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stand-in, and a deliberate one.</b> <c>signalr-hub.md</c> §1.1 says a client resyncs
    /// from <c>GET /v1/nearby</c> (query-svc) because "the socket carries deltas, the REST read
    /// carries the snapshot" — and query-svc is C042. Until it exists, a passenger who opens the map
    /// sees nothing at all until each nearby vehicle's next sample, which looks exactly like a
    /// broken map. This replays the tail of each joined cell's buffer to <b>the joining connection
    /// only</b>, which is bounded and costs the group nothing.
    /// </para>
    /// <para>
    /// Set to 0 to turn it off. <b>C041/C042 should remove it</b> once <c>/v1/nearby</c> lands —
    /// two snapshot paths is one more than the contract has.
    /// </para>
    /// </remarks>
    [Range(0, 1_000)]
    public int JoinSeedFrames { get; set; } = 32;

    /// <summary>
    /// How long a group membership survives a <c>LeaveGeocells</c> (ADD §7.4 step 6).
    /// </summary>
    /// <remarks>
    /// Defaults to the 30 s <see cref="Shared.Geo.GeoCells.BoundaryHysteresis"/> the KMP module also
    /// holds. A passenger walking along a cell edge would otherwise join and leave the same six
    /// groups every few seconds, and each of those is a backplane round trip.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan LeaveHysteresis { get; set; } = Shared.Geo.GeoCells.BoundaryHysteresis;

    /// <summary>
    /// The most cells one connection may hold.
    /// </summary>
    /// <remarks>
    /// The 3 km view is 19 and the intercity view 37 (ADD §7.4 step 4); the ceiling is above both so
    /// a client crossing a boundary can hold two views at once during the hysteresis window. It
    /// exists because <c>JoinGeocells</c> takes an array off the wire: without a bound, one client
    /// could ask this replica to poll every cell in the country.
    /// </remarks>
    [Range(19, 10_000)]
    public int MaxCellsPerConnection { get; set; } = 128;
}
