using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Rollups;

namespace MageRide.FleetHealth.Endpoints;

/// <summary>
/// The response bodies <c>backend/contracts/fleet-health.yaml</c> prints. That file is normative and
/// wins over these types.
/// </summary>
public static class FleetHealthResponses
{
    /// <summary>Projects a snapshot onto the wire shape.</summary>
    public static FleetHealthRollupResponse From(FleetHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var counts = snapshot.Counts;

        return new FleetHealthRollupResponse(
            snapshot.FleetId,

            // The pre-C044 contract's coarse pair, kept and still required. `vehiclesOffline` excludes
            // decommissioned devices: a retired tracker is not a tracker that went down, and counting it
            // here would leave every operator's alarm permanently raised after their first decommission.
            VehiclesOnline: counts.Online,
            VehiclesOffline: counts.Stale + counts.Offline,

            Counts: new TrackerStateCountsResponse(
                counts.Online, counts.Stale, counts.Offline, counts.Decommissioned, counts.Total),

            Percentages: new TrackerStatePercentagesResponse(
                counts.PercentOf(counts.Online),
                counts.PercentOf(counts.Stale),
                counts.PercentOf(counts.Offline),
                counts.PercentOf(counts.Decommissioned)),

            // Returned rather than assumed by the client, so a deployment that retunes US-3.13's five and
            // thirty minutes does not need a portal release to relabel its own legend.
            Thresholds: new HealthThresholdsResponse(
                (int)snapshot.Thresholds.StaleAfter.TotalSeconds,
                (int)snapshot.Thresholds.OfflineAfter.TotalSeconds),

            Window: new FleetHealthWindowResponse(
                snapshot.Window.Start,
                snapshot.Window.End,
                (int)(snapshot.Window.End - snapshot.Window.Start).TotalMinutes,
                snapshot.Window.Expected,
                snapshot.Window.Reporting,
                snapshot.Window.Offline,
                snapshot.Window.OfflinePct,
                snapshot.ThresholdPct,
                snapshot.Window.Breaches(snapshot.ThresholdPct)),

            Alert: snapshot.Alert is { } alert ? FleetHealthAlertResponse.From(alert) : null,

            Items: [.. snapshot.Items.Select(TrackerHealthResponse.From)],
            ItemsTruncated: snapshot.ItemsTruncated,
            AsOf: snapshot.AsOf);
    }
}

/// <summary><c>fleet-health.yaml</c>'s <c>FleetHealthRollup</c>.</summary>
public sealed record FleetHealthRollupResponse(
    Guid FleetId,
    int VehiclesOnline,
    int VehiclesOffline,
    TrackerStateCountsResponse Counts,
    TrackerStatePercentagesResponse Percentages,
    HealthThresholdsResponse Thresholds,
    FleetHealthWindowResponse Window,
    FleetHealthAlertResponse? Alert,
    IReadOnlyList<TrackerHealthResponse> Items,
    bool ItemsTruncated,
    DateTimeOffset AsOf);

/// <summary><c>fleet-health.yaml</c>'s <c>TrackerStateCounts</c> — US-3.13's four states.</summary>
public sealed record TrackerStateCountsResponse(
    int Online, int Stale, int Offline, int Decommissioned, int Total);

/// <summary><c>fleet-health.yaml</c>'s <c>TrackerStatePercentages</c>.</summary>
public sealed record TrackerStatePercentagesResponse(
    double Online, double Stale, double Offline, double Decommissioned);

/// <summary><c>fleet-health.yaml</c>'s <c>HealthThresholds</c>.</summary>
public sealed record HealthThresholdsResponse(int StaleAfterSeconds, int OfflineAfterSeconds);

/// <summary><c>fleet-health.yaml</c>'s <c>FleetHealthWindow</c> — the US-3.16 5-minute rollup.</summary>
public sealed record FleetHealthWindowResponse(
    DateTimeOffset Start,
    DateTimeOffset End,
    int WindowMinutes,
    int ExpectedVehicles,
    int ReportingVehicles,
    int OfflineVehicles,
    double OfflinePct,
    double ThresholdPct,
    bool Alerting);

/// <summary><c>fleet-health.yaml</c>'s <c>FleetHealthAlert</c>.</summary>
public sealed record FleetHealthAlertResponse(
    Guid AlertId,
    DateTimeOffset Bucket,
    int WindowMinutes,
    int ExpectedVehicles,
    int ReportingVehicles,
    int OfflineVehicles,
    double OfflinePct,
    double ThresholdPct,
    DateTimeOffset RaisedAt)
{
    public static FleetHealthAlertResponse From(FleetHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new FleetHealthAlertResponse(
            alert.AlertId,
            alert.Bucket,
            alert.WindowMinutes,
            alert.Expected,
            alert.Reporting,
            alert.Offline,
            alert.OfflinePct,
            alert.ThresholdPct,
            alert.RaisedAt);
    }
}

/// <summary><c>fleet-health.yaml</c>'s <c>TrackerHealth</c>.</summary>
public sealed record TrackerHealthResponse(
    Guid VehicleId,
    string? Imei,
    string State,
    bool Online,
    DateTimeOffset? LastSeen,
    DateTimeOffset? Since,
    int? Battery,
    int? BatteryMv,
    int? SignalStrength,
    int? Sats)
{
    public static TrackerHealthResponse From(DeviceHealthRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TrackerHealthResponse(
            row.VehicleId,
            row.Imei,
            TrackerHealthStates.ToWire(row.State),

            // The pre-C044 boolean, derived from the state rather than stored beside it — two fields that
            // could disagree about whether a device is up is the bug this projection exists to prevent.
            Online: row.State == TrackerHealthStates.Online,

            LastSeen: row.LastPingAt,
            Since: row.StateChangedAt,
            Battery: row.BatteryPct,
            BatteryMv: row.BatteryMv,
            SignalStrength: row.SignalStrength,
            Sats: row.SatCount);
    }
}
