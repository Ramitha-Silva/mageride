using System.ComponentModel.DataAnnotations;

namespace MageRide.TripState.Configuration;

/// <summary>trip-state-svc's own settings.</summary>
public sealed class TripStateOptions
{
    public const string SectionName = "TripState";

    /// <summary>
    /// How long a session may go without movement before the sweep ends it (US-5.3).
    /// </summary>
    /// <remarks>
    /// D7' §4.2 spells the deployment key <c>Session__IdleTimeoutMin</c> and URD US-5.3 fixes the
    /// value at 30 minutes. Measured from <c>trips.sessions.last_movement_at</c>, which the
    /// <c>telemetry.normalized</c> consumer advances — <b>not</b> from the last position of any
    /// kind. A bus parked at a terminus still reports fixes; what US-5.3 means by idle is that it
    /// has not moved.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far a vehicle must travel between fixes to count as movement.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins this.</b> US-5.3 says "no movement detected" and stops there. GNSS on a
    /// stationary vehicle wanders by tens of metres, so a naive "position changed" test would keep
    /// a parked bus alive forever — which is the failure US-5.3 exists to prevent. 50 m is above
    /// consumer-GPS drift and below a bus length's worth of manoeuvring; the speed gate below is
    /// the second, independent signal.
    /// </remarks>
    [Range(5, 1000)]
    public double MovementThresholdM { get; set; } = 50;

    /// <summary>Ground speed above which a fix counts as movement whatever the displacement.</summary>
    /// <remarks>D5' §5.2 calls a vehicle "moving" above 5 km/h; this is that number in m/s.</remarks>
    [Range(0.1, 30)]
    public double MovementSpeedMps { get; set; } = 1.4;

    /// <summary>
    /// The destination geofence radius (US-5.4). D7' §4.2's <c>Geofence__AutoEndM</c>.
    /// </summary>
    [Range(10, 5000)]
    public double GeofenceRadiusM { get; set; } = 100;

    /// <summary>
    /// How long after an auto-end a session may be restarted (US-5.10).
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: <c>restartableUntil</c> is <c>ended_at + this</c>, so changing
    /// the window does not strand rows minted under the old one. Only an <i>auto</i>-ended session
    /// qualifies — a driver who pressed End Journey meant it, and reopening that would make the
    /// button ambiguous.
    /// </remarks>
    public TimeSpan RestartGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Grace after the last will before an offline vehicle's session is ended (R-15, T-04).
    /// </summary>
    /// <remarks>
    /// <b>No spec pins this either.</b> R-15 and T-04 give the broker a last will and D5' §5.4
    /// takes an offline vehicle off the public map, but neither says how long a tunnel is allowed
    /// to last. Ending on the first `offline` would close a journey every time a bus passes under
    /// a bridge; two minutes covers ordinary coverage gaps, and the 30-minute idle sweep is what
    /// catches a vehicle that never comes back.
    /// </remarks>
    public TimeSpan OfflineGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Whether the idle / geofence / offline sweep runs in this process.</summary>
    /// <remarks>
    /// On by default — US-5.3 is a platform guarantee and a deployment that silently skipped it
    /// would leave every forgotten session live forever. Off in tests, which drive
    /// <c>SessionSweepWorker.SweepOnceAsync</c> directly rather than waiting on a ticker.
    /// </remarks>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>How often the sweep looks.</summary>
    /// <remarks>
    /// A minute, not thirty. The sweep's precision is its interval, and US-5.9 pushes the driver a
    /// notification naming the reason — "your journey ended 29 minutes ago" is not that.
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many sessions one sweep pass claims.</summary>
    [Range(1, 10_000)]
    public int SweepBatchSize { get; set; } = 200;

    /// <summary>Whether the <c>telemetry.normalized</c> consumer runs in this process.</summary>
    /// <remarks>
    /// It is what advances <c>last_movement_at</c> and what notices a vehicle arriving inside its
    /// destination geofence, so turning it off disables US-5.3 and US-5.4 together — the sweep
    /// keeps running and simply never finds a session that has stopped moving.
    /// </remarks>
    public bool PositionConsumerEnabled { get; set; } = true;

    /// <summary>Consumer group for the position stream.</summary>
    /// <remarks>
    /// Its own group, not the position processor's: D6' §2.1 makes <c>telemetry.normalized</c> a
    /// fan-out stream and two services sharing a group would each see half the fixes.
    /// </remarks>
    public string PositionConsumerGroup { get; set; } = "trip-state-positions";

    /// <summary>Whether the EMQX presence subscriber runs in this process (R-15, T-04).</summary>
    public bool VehicleStatusEnabled { get; set; }

    /// <summary>MQTT service-account name; <c>svc-</c> is prefixed by the token issuer.</summary>
    public string MqttServiceName { get; set; } = "trip-state";

    /// <summary>
    /// Whether a cadence hint is published to <c>veh/{vehicleId}/cmd</c> on a session transition.
    /// </summary>
    /// <remarks>
    /// D5' §5.2 puts "Standby moving" (5–10 s) against an A/B session and "Standby idle"
    /// (30–60 s) against a vehicle with none, and says the <b>server</b> pushes the hint. This is
    /// that push. Best effort: a device that never receives it keeps its previous rate, which
    /// costs battery rather than correctness.
    /// </remarks>
    public bool PublishCadenceHints { get; set; }

    /// <summary>Cadence for a vehicle with a live A/B session — D5' §5.2 "Standby moving".</summary>
    public TimeSpan SessionCadence { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Cadence for a vehicle with no session — D5' §5.2 "Standby idle".</summary>
    public TimeSpan StandbyCadence { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shared secret for <c>/v1/internal/sessions/**</c>, in <c>X-MageRide-Internal-Key</c>.
    /// </summary>
    /// <remarks>
    /// D3' §0 puts the internal family on mTLS and the gateway refuses the prefix at the edge
    /// (C008); this is the interim until C042 lands a mesh. It must equal what the tcp-adapter
    /// (C043) sends for the ignition route. <b>Unset means those routes are not mapped at all</b>,
    /// so a deployment that forgets it gets 404s — and the visible symptom is that ignition
    /// auto-sessions stop happening, not that anything unauthenticated can end a journey.
    /// </remarks>
    public string? InternalApiKey { get; set; }
}
