using System.ComponentModel.DataAnnotations;

namespace MageRide.Fanout.Configuration;

/// <summary>
/// How the hub batches, what it lets through and how long it holds a group after a client leaves
/// (<c>Fanout</c> section).
/// </summary>
public sealed class FanoutOptions
{
    public const string SectionName = "Fanout";

    /// <summary>
    /// Runs the position pumps in this process. Off in tests that assert on the registry or the
    /// hub alone, so a background push cannot arrive under an assertion.
    /// </summary>
    public bool PumpEnabled { get; set; } = true;

    /// <summary>
    /// How often the pumps drain — the cell streams this replica has subscribers for, and the
    /// vehicles behind its <c>vehicle:</c> and <c>ride:</c> groups.
    /// </summary>
    /// <remarks>
    /// <c>signalr-hub.md</c> §3 puts a <c>VehiclePositions</c> batch at "every 2–8 s (US-7.3)", and
    /// this is the bottom of that band — the component's SLO is "a position reaches the passenger in
    /// under 5 s (p95)", so the batch window has to be a small fraction of it. The band exists to
    /// stop a <b>per-fix</b> fan-out (5 msg/s per vehicle times every subscriber of its cell,
    /// ADD §7.4); batching at two seconds is still batching.
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
    /// The seed goes through the same visibility filter as a live batch — an engaged Mode C vehicle
    /// or a Mode B one is no more visible in a replay than it is live. Set to 0 to turn it off.
    /// <b>C042 should remove it</b> once <c>/v1/nearby</c> lands — two snapshot paths is one more
    /// than the contract has.
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

    /// <summary>
    /// How old a sample may be and still be drawn as a vehicle's current position (US-7.17).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No spec pins the number.</b> D5' §5.4, ADD §6 and US-7.17 all say "older than the
    /// freshness window" and none of them says how long that is; the only related figure is ADD
    /// §7.5.1's dispatch rule, "older than <c>2 × expectedInterval</c>", whose expected interval is
    /// per phase and ranges from 1 s to 60 s. 60 s is chosen to match
    /// <c>PositionProcessor:DriverAvailabilityTtl</c> and <c>Dispatch:PresenceTtl</c>, so a vehicle
    /// disappears from the passenger's map at the same moment its driver leaves the dispatch pool —
    /// two different answers to "is this vehicle live" would be visible to a passenger as a marker
    /// they can see and cannot book.
    /// </para>
    /// <para>
    /// It cuts twice. A vehicle with no sample inside the window is removed from the public groups
    /// with <c>VehicleRemoved{reason:"stale"}</c>; and a frame that <em>arrives</em> already older
    /// than the window is dropped rather than drawn, which is what keeps an offline device's replay
    /// backlog (<c>veh/{id}/pos/replay</c>, R-17) off the live map — those samples travel the same
    /// cell stream as live ones.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many <c>vehicle:{vehicleId}</c> groups one connection may hold — Mode B grants plus, for
    /// a driver, their own active vehicle.
    /// </summary>
    /// <remarks>
    /// The bound is on the <em>server's</em> read of <c>share:{userId}</c> rather than on anything
    /// the client asks for, so it is a guard against a runaway grant list rather than against a
    /// hostile client. A passenger with more grants than this sees the first N; the log says so.
    /// </remarks>
    [Range(1, 1_000)]
    public int MaxVehicleSubscriptions { get; set; } = 64;

    /// <summary>
    /// Consume <c>ride.events</c> and <c>registry.events</c> — the whole visibility model's input.
    /// </summary>
    /// <remarks>
    /// Off leaves the hub fanning out unfiltered positions, which is the C024 behaviour this
    /// component exists to replace. It is a test switch and a break-glass, and the service logs a
    /// warning at start-up when it is off, for the same reason position-processor-svc warns about a
    /// disabled gate: an open filter looks exactly like a working one from the outside.
    /// </remarks>
    public bool EventsEnabled { get; set; } = true;

    /// <summary>Consumer group for both topics (D6' §2: "consumer group per service").</summary>
    public string ConsumerGroup { get; set; } = "fanout-svc";

    /// <summary>
    /// Hold the EMQX last-will subscription, so a vehicle going offline leaves the map at once
    /// rather than after <see cref="FreshnessWindow"/> (R-15, T-04, US-7.17).
    /// </summary>
    /// <remarks>
    /// The freshness window is the backstop and covers the same ground a few seconds later, so a
    /// deployment with no broker reachable degrades rather than breaks. ADD §6's fanout-svc row
    /// names the last will explicitly, which is why it is on by default here and off by default in
    /// ride-svc, where the same subscription serves a grace timer rather than a visibility rule.
    /// </remarks>
    public bool PresenceEnabled { get; set; } = true;

    /// <summary>
    /// Publish and apply directed sends over <see cref="Shared.Caching.RedisKeys.FanoutControlChannel"/>.
    /// </summary>
    /// <remarks>
    /// D6' §5's "Redis backplane (MVP)". Off, a directed send only reaches connections on the
    /// replica that consumed the event — correct in a single-replica deployment and a silent
    /// half-delivery in any other, which is why it is on by default and warned about when off.
    /// </remarks>
    public bool ControlPlaneEnabled { get; set; } = true;

    /// <summary>
    /// How long fanout-svc remembers a ride's participants after the last event about it.
    /// </summary>
    /// <remarks>
    /// The projection exists to answer <c>SubscribeRide</c>, so it has to outlive the ride by at
    /// least a reconnect. A day is long enough that no live ride can age out of it — R-20's
    /// stuck-state SLOs are minutes — and short enough that the keyspace is bounded by a day of
    /// bookings rather than by all of them.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan RideProjectionTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Backstop expiry on <c>veh:engaged:{vehicleId}</c>.
    /// </summary>
    /// <remarks>
    /// The key is cleared by the ride's terminal event and this is what happens if that event is
    /// never seen at all — a topic retention gap, a keyspace restored from elsewhere. It errs long
    /// on purpose: the failure it guards against is a vehicle stuck <em>invisible</em>, which costs
    /// one driver their bookings, and the opposite default would put an engaged taxi back on the
    /// public map mid-ride, which is the thing US-7.16 forbids.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:30:00", "7.00:00:00")]
    public TimeSpan EngagementTtl { get; set; } = TimeSpan.FromHours(12);
}
