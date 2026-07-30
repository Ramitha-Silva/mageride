using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Rollups;

/// <summary>Evaluates one closed window for every fleet and raises US-3.16's device-down alert.</summary>
public interface IFleetHealthAlertService
{
    /// <summary>
    /// Evaluates the window starting at <paramref name="bucketStart"/> and returns the alerts this
    /// replica actually raised — which may be fewer than the fleets that breached, because another
    /// replica may have claimed some of them.
    /// </summary>
    Task<IReadOnlyList<FleetHealthAlert>> EvaluateWindowAsync(
        DateTimeOffset bucketStart, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetHealthAlertService"/>
/// <remarks>
/// <para>
/// <b>The arithmetic, in one place.</b> <c>expected</c> is the fleet's <c>ACTIVE</c> tracker bindings —
/// the roster, which only <c>prov.tracker_bindings</c> knows. <c>reporting</c> is the closed
/// <c>telemetry.fleet_health_5m</c> bucket's distinct-vehicle count — the liveness, which only the
/// continuous aggregate knows. US-3.16's percentage is the ratio, and neither source can supply the
/// other's half: a vehicle that publishes nothing writes no row for the aggregate to count, so a
/// missing tracker is invisible to it by construction.
/// </para>
/// <para>
/// <b>The alert is edge-triggered by default.</b> US-3.16 is "N % of my fleet <b>goes</b> offline
/// within a 5-minute window" — a transition. Level-triggered, a fleet with a fifth of its vehicles
/// parked for the season would alert every window for ever and be muted inside a day, which is the same
/// outcome as not alerting at all but harder to notice. <c>Health:AlertOnCrossingOnly</c> makes the
/// choice visible and reversible.
/// </para>
/// <para>
/// <b>"Exactly one alert per window" is an index, not a lock.</b> Every replica evaluates every window;
/// <c>ux_fleet_health_alert_window</c> lets exactly one of them insert, and the replica whose insert
/// returned no row writes no outbox event. So the guarantee holds for any number of replicas, and it
/// also holds across a restart — a worker that comes back and re-evaluates a window it already alerted
/// on raises nothing.
/// </para>
/// <para>
/// <b>The alert row and the outbox row commit together</b> (D6' §2.4, R-13). An alert that committed and
/// then failed to publish would be an outage nobody was told about, sitting in a table with a unique
/// index that stops it ever being retried.
/// </para>
/// </remarks>
public sealed class FleetHealthAlertService(
    IFleetRollupRepository repository,
    IAggregateMaintainer maintainer,
    IUnitOfWorkFactory unitOfWorkFactory,
    IOutboxWriter outbox,
    IOptions<FleetHealthOptions> options,
    ILogger<FleetHealthAlertService> logger) : IFleetHealthAlertService
{
    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<FleetHealthAlert>> EvaluateWindowAsync(
        DateTimeOffset bucketStart, CancellationToken cancellationToken)
    {
        var window = _options.Window;
        var bucketEnd = bucketStart + window;
        var previousStart = bucketStart - window;

        // Materialise both buckets before reading them. The previous one is almost certainly already
        // materialised by 1802's policy; asking for the pair in one call costs nothing and makes the
        // crossing test read the same rows the current window's does.
        await maintainer.RefreshWindowAsync(previousStart, bucketEnd, cancellationToken);

        var candidates = await repository.ReadWindowCandidatesAsync(
            bucketStart, previousStart, _options.MinFleetSize, cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        var raised = new List<FleetHealthAlert>();

        foreach (var candidate in candidates)
        {
            var current = new FleetWindowRollup(
                candidate.FleetId,
                bucketStart,
                bucketEnd,
                candidate.Expected,
                Math.Min(candidate.Reporting, candidate.Expected));

            if (!current.Breaches(_options.OfflinePct))
            {
                continue;
            }

            if (_options.AlertOnCrossingOnly)
            {
                var previous = current with
                {
                    Start = previousStart,
                    End = bucketStart,
                    Reporting = Math.Min(candidate.PreviousReporting, candidate.Expected),
                };

                if (previous.Breaches(_options.OfflinePct))
                {
                    // Already breaching a window ago. Not a fleet that *went* offline.
                    continue;
                }
            }

            var alert = await RaiseAsync(current, cancellationToken);

            if (alert is not null)
            {
                raised.Add(alert);
            }
        }

        return raised;
    }

    private async Task<FleetHealthAlert?> RaiseAsync(
        FleetWindowRollup window, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var alert = await repository.TryClaimAlertAsync(
            unitOfWork, window, _options.WindowMin, _options.OfflinePct, cancellationToken);

        if (alert is null)
        {
            // Another replica claimed this window, or this worker has already evaluated it. Nothing to
            // roll back, and nothing to say — one alert per window is the point.
            await unitOfWork.RollbackAsync(cancellationToken);
            return null;
        }

        await outbox.WriteAsync(unitOfWork, FleetHealthEvents.HealthAlert(alert), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        MageRideDiagnostics.FleetHealthAlerts.Add(
            1, new KeyValuePair<string, object?>("fleet_id", alert.FleetId.ToString()));

        logger.LogWarning(
            "Fleet {FleetId}: {Offline} of {Expected} trackers ({OfflinePct:F1}%) did not report in the " +
            "{Minutes}-minute window from {Bucket:o} — threshold {ThresholdPct:F1}% (US-3.16)",
            alert.FleetId, alert.Offline, alert.Expected, alert.OfflinePct, alert.WindowMinutes,
            alert.Bucket, alert.ThresholdPct);

        return alert;
    }
}
