namespace MageRide.FleetHealth.Domain;

/// <summary>
/// One device's liveness, as <c>telemetry.normalized</c> reported it.
/// </summary>
/// <param name="VehicleId">The publishing vehicle — the telemetry plane's key everywhere.</param>
/// <param name="FleetId">Denormalised on the sample by persistence-writer-svc's own rule
/// (<c>mqtt-topics.md</c> §6). <see langword="null"/> leaves the stored value alone rather than
/// clearing it: a sample that arrived before C040 resolved the fleet is not evidence the vehicle left
/// one.</param>
/// <param name="PingAt">Platform receive clock — the sample's <c>receivedTs</c>, or the flush instant
/// when the producer sent none. This is what the silence thresholds are measured from.</param>
/// <param name="SampleTs">GNSS capture instant, kept for the record and never used as a clock.</param>
/// <param name="Source"><c>telemetry.positions.source</c> domain, 0…4.</param>
/// <param name="SatCount">Satellites in the fix. The one US-3.12 diagnostic a position sample
/// carries, so it is taken from here as well as from <c>sys/diag</c>.</param>
public sealed record DeviceHealthPing(
    Guid VehicleId,
    Guid? FleetId,
    DateTimeOffset PingAt,
    DateTimeOffset SampleTs,
    short? Source,
    short? SatCount)
{
    /// <summary>
    /// Collapses two pings for one vehicle to the newer, field by field.
    /// </summary>
    /// <remarks>
    /// Field by field rather than "the newer record wins": a sample carrying no satellite count must
    /// not erase the count the previous one reported, and per-vehicle ordering can lapse for seconds
    /// during a consumer-group rebalance (C039's note on the same topic), so "newer" is a comparison
    /// and not an assumption about arrival order.
    /// </remarks>
    public DeviceHealthPing Merge(DeviceHealthPing other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var newer = other.PingAt > PingAt ? other : this;

        return newer with
        {
            PingAt = other.PingAt > PingAt ? other.PingAt : PingAt,
            SampleTs = other.SampleTs > SampleTs ? other.SampleTs : SampleTs,
            FleetId = newer.FleetId ?? other.FleetId ?? FleetId,
            Source = newer.Source ?? other.Source ?? Source,
            SatCount = newer.SatCount ?? other.SatCount ?? SatCount,
        };
    }
}

/// <summary>
/// The retained <c>veh/{vehicleId}/status</c> payload (D6' §3.1/§3.4, R-15, T-04).
/// </summary>
/// <param name="Status"><c>online</c> or <c>offline</c>, lower-case as
/// <see cref="Shared.Mqtt.VehicleStatus"/> spells it.</param>
/// <param name="At">When this replica received it. The broker stamps no timestamp on a retained
/// message and a last will has no publisher clock to read, so the receive instant is the only one
/// there is — which is also why the comparison against <c>last_ping_at</c> in
/// <c>device_health_state()</c> is between two platform clocks and not between a device's and ours.</param>
public sealed record DeviceStatusReport(Guid VehicleId, string Status, DateTimeOffset At);

/// <summary>
/// A <c>sys/diag/{vehicleId}</c> report (D6' §3.1, QoS 0) — US-3.12's per-tracker fields.
/// </summary>
/// <remarks>
/// Every member but the vehicle and the instant is optional, because the device population reports
/// different subsets: a GT06 status byte carries a coarse voltage level and a GSM signal strength,
/// JT/T 808 additional items carry millivolts and a satellite count, generic NMEA carries neither.
/// A field nobody reported stays as it was rather than being written null — "this device stopped
/// reporting its battery" and "this device's battery is unknown" are different facts and only the
/// second is true.
/// </remarks>
public sealed record DeviceDiagnosticsReport(
    Guid VehicleId,
    DateTimeOffset At,
    short? SignalStrength,
    int? BatteryMv,
    short? BatteryPct,
    short? SatCount)
{
    /// <summary>Whether the report carries anything worth storing.</summary>
    public bool HasAnyValue =>
        SignalStrength is not null || BatteryMv is not null || BatteryPct is not null || SatCount is not null;
}

/// <summary>
/// A binding lifecycle change from <c>provisioning.events</c> (C030) — what this service needs from
/// the credential plane.
/// </summary>
/// <param name="Imei">The device identity, for the <c>prov.tracker_bindings</c> diagnostics sync.</param>
/// <param name="FleetId">The fleet the binding carries (ADD §7.7.7). Authoritative here, unlike the
/// sample's, because provisioning-svc is where a fleet binding is decided.</param>
/// <param name="BindingState">One of <see cref="TrackerBindingStates"/>.</param>
/// <param name="DecommissionedAt">Set when the credential was revoked (US-3.8) and cleared when a
/// fresh bind supersedes it — a re-provisioned tracker is not decommissioned.</param>
public sealed record TrackerBindingChange(
    Guid VehicleId,
    string? Imei,
    Guid? FleetId,
    string BindingState,
    DateTimeOffset? DecommissionedAt);

/// <summary>
/// One device as the fleet dashboard reads it back (<c>fleet-health.yaml</c>'s
/// <c>TrackerHealth</c>).
/// </summary>
/// <param name="State">Derived by <c>telemetry.device_health_state()</c> in the query itself, never
/// in this process.</param>
public sealed record DeviceHealthRow(
    Guid VehicleId,
    string? Imei,
    string State,
    DateTimeOffset? LastPingAt,
    DateTimeOffset? StateChangedAt,
    short? SignalStrength,
    int? BatteryMv,
    short? BatteryPct,
    short? SatCount);

/// <summary>US-3.13's four counts over a whole fleet.</summary>
public sealed record TrackerStateCounts(int Online, int Stale, int Offline, int Decommissioned)
{
    public static readonly TrackerStateCounts Empty = new(0, 0, 0, 0);

    public int Total => Online + Stale + Offline + Decommissioned;

    /// <summary>
    /// The share of the fleet in <paramref name="count"/>, to one decimal place; 0 for an empty
    /// fleet.
    /// </summary>
    /// <remarks>
    /// A percentage of nothing is reported as zero rather than as a division error or a null — an
    /// operator who has onboarded no trackers should see an empty dashboard, not a broken one.
    /// </remarks>
    public double PercentOf(int count) =>
        Total == 0 ? 0 : Math.Round(count * 100d / Total, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// One closed <c>telemetry.fleet_health_5m</c> bucket for one fleet, against the fleet's roster.
/// </summary>
/// <param name="Expected">The fleet's <c>ACTIVE</c> tracker bindings. The continuous aggregate cannot
/// know this — it only sees vehicles that reported — so the denominator comes from
/// <c>prov.tracker_bindings</c>.</param>
/// <param name="Reporting">The bucket's <c>active_vehicles</c>, capped at <paramref name="Expected"/>.
/// Capped because the aggregate counts distinct vehicles carrying the fleet's id in
/// <c>telemetry.positions</c>, which includes a fleet vehicle publishing from a phone (US-3.6's other
/// source) and a vehicle whose binding was revoked mid-window; neither should make the offline count
/// negative.</param>
public sealed record FleetWindowRollup(
    Guid FleetId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int Expected,
    int Reporting)
{
    public int Offline => Math.Max(0, Expected - Reporting);

    public double OfflinePct =>
        Expected == 0 ? 0 : Math.Round(Offline * 100d / Expected, 2, MidpointRounding.AwayFromZero);

    /// <summary>Whether this window is at or above <paramref name="thresholdPct"/>.</summary>
    /// <remarks>
    /// <c>&gt;=</c>, not <c>&gt;</c> — see <see cref="Configuration.FleetHealthOptions.OfflinePct"/>.
    /// A fleet with no trackers never breaches: zero of zero is not an outage.
    /// </remarks>
    public bool Breaches(double thresholdPct) => Expected > 0 && OfflinePct >= thresholdPct;
}

/// <summary>A raised device-down alert, as <c>telemetry.fleet_health_alerts</c> stored it.</summary>
public sealed record FleetHealthAlert(
    Guid AlertId,
    Guid FleetId,
    DateTimeOffset Bucket,
    int WindowMinutes,
    int Expected,
    int Reporting,
    int Offline,
    double OfflinePct,
    double ThresholdPct,
    DateTimeOffset RaisedAt);

/// <summary>One device's state change, as the sweep recorded it.</summary>
public sealed record HealthTransition(Guid VehicleId, Guid? FleetId, string FromState, string ToState);

/// <summary>One row of the per-state <c>GROUP BY</c> behind <see cref="TrackerStateCounts"/>.</summary>
internal sealed record StateCount(string State, int Count);
