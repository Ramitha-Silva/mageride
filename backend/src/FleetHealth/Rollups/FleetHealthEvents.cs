using System.Text.Json;
using MageRide.FleetHealth.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;

namespace MageRide.FleetHealth.Rollups;

/// <summary>The event names fleet-health-svc publishes on <c>fleet.events</c>.</summary>
public static class FleetHealthEventTypes
{
    /// <summary>
    /// US-3.16. More than <c>Health:OfflinePct</c> of a fleet's active trackers stopped reporting
    /// inside one <c>Health:WindowMin</c> window.
    /// </summary>
    public const string HealthAlert = "fleet.health_alert";
}

/// <summary>
/// The envelope fleet-health-svc writes into <c>telemetry.outbox</c> (D6' §2.4, migration 1805).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither the topic nor the envelope is in D6'.</b> §2.1's registry lists six topics, none of them
/// this service's, and §2.2 prints no schema for a fleet health alert — so both are C044's and both are
/// raised as micro-change-sets in the handoff, the same shape C028, C030 and C033 raised for their own
/// outbox topics.
/// </para>
/// <para>
/// <b>The aggregate id is the fleet</b>, matching the topic's partition key. An alert is a fact about an
/// organisation, and two windows' verdicts for one fleet have to arrive in the order they were reached.
/// </para>
/// <para>
/// <b>No rendered text, and that is a rule rather than an omission.</b> The payload carries
/// <c>notificationType</c> and the numbers behind the decision; the trilingual template, the channel and
/// the recipient's preferences are notification-svc's (C051, D-26), and US-3.16 delivers this by
/// email/SMS to whoever subscribed. This service owns the threshold and the clock and nothing about the
/// message. The same split C036 makes for <c>directional.expiring</c>.
/// </para>
/// </remarks>
public static class FleetHealthEvents
{
    /// <summary>
    /// The <c>notificationType</c> notification-svc renders a template for. Named here because the
    /// producer and the template registry are in different components and a typo produces a
    /// notification nobody receives.
    /// </summary>
    public const string NotificationType = "FLEET_DEVICES_OFFLINE";

    public static OutboxRecord HealthAlert(FleetHealthAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new OutboxRecord(
            alert.FleetId,
            FleetHealthEventTypes.HealthAlert,
            JsonSerializer.Serialize(
                new
                {
                    alertId = alert.AlertId,
                    fleetId = alert.FleetId,
                    notificationType = NotificationType,
                    window = new
                    {
                        start = alert.Bucket,
                        end = alert.Bucket.AddMinutes(alert.WindowMinutes),
                        minutes = alert.WindowMinutes,
                    },
                    expectedVehicles = alert.Expected,
                    reportingVehicles = alert.Reporting,
                    offlineVehicles = alert.Offline,
                    offlinePct = alert.OfflinePct,
                    thresholdPct = alert.ThresholdPct,
                    raisedAt = alert.RaisedAt,
                },
                MageRideJson.StorageOptions));
    }
}
