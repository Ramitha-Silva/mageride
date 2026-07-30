using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Rollups;

/// <summary>
/// Keeps <c>telemetry.fleet_health_5m</c> answerable: verifies the aggregate and its refresh policy,
/// and materialises a closed window before anything reads it.
/// </summary>
public interface IAggregateMaintainer
{
    /// <summary>
    /// Checks the aggregate is present, has a refresh policy and reads the live tail, saying loudly
    /// what is wrong when it is not.
    /// </summary>
    Task<AggregateStatus> VerifyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Materialises <c>[start, end)</c>, or reports why it could not. Never throws: a failed refresh
    /// leaves the read correct and slower, not wrong.
    /// </summary>
    Task<bool> RefreshWindowAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAggregateMaintainer"/>
/// <remarks>
/// <para>
/// <b>This is the "continuous-aggregate maintenance" half of C044, and it is deliberately not a second
/// opinion about the rollup.</b> Migration 1802 owns the aggregate's definition and its refresh policy;
/// TimescaleDB's own scheduler runs the policy. What a service can usefully add is two things the
/// scheduler cannot: it can say so when the aggregate is missing or misconfigured, and it can
/// materialise a specific closed window on demand by calling the aggregate's own
/// <c>refresh_continuous_aggregate</c> procedure. Recomputing the numbers here instead would be
/// exactly the duplication C040's handoff warns against.
/// </para>
/// <para>
/// <b>Why the on-demand refresh matters.</b> 1802 gives the policy an <c>end_offset</c> of five
/// minutes, so the bucket that has just closed is materialised eventually and not necessarily yet.
/// <c>materialized_only = false</c> means a read still answers correctly by rescanning raw chunks for
/// the un-materialised tail — correct, and precisely the scan the rollup exists to avoid, on the
/// largest table on the platform. Refreshing first turns the alert evaluation and the fleet dashboard
/// into reads of materialised rows.
/// </para>
/// <para>
/// <b>The window width is the aggregate's, not this service's to choose.</b> The relation is a
/// <b>5-minute</b> aggregate by name and by 1802's <c>time_bucket('5 minutes', …)</c>, so a
/// <c>Health:WindowMin</c> of anything else would measure a 3-minute expectation against a 5-minute
/// count. <see cref="VerifyAsync"/> says so rather than quietly producing wrong percentages.
/// </para>
/// </remarks>
public sealed class AggregateMaintainer(
    IFleetRollupRepository repository,
    IOptions<FleetHealthOptions> options,
    ILogger<AggregateMaintainer> logger) : IAggregateMaintainer
{
    /// <summary>
    /// The <c>time_bucket</c> width migration 1802 gives <c>telemetry.fleet_health_5m</c>, in minutes.
    /// </summary>
    /// <remarks>
    /// A constant rather than something read back from TimescaleDB: the bucket width of a continuous
    /// aggregate is not exposed by any <c>timescaledb_information</c> view, and parsing it out of
    /// <c>view_definition</c> would be a fragile way to learn something the migration and the relation's
    /// own name both state.
    /// </remarks>
    public const int AggregateBucketMinutes = 5;

    private readonly FleetHealthOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<AggregateStatus> VerifyAsync(CancellationToken cancellationToken)
    {
        var status = await repository.ReadAggregateStatusAsync(cancellationToken);

        if (!status.Exists)
        {
            logger.LogError(
                "telemetry.fleet_health_5m is not a continuous aggregate on this database. US-3.13's " +
                "window rollup and the US-3.16 device-down alert both read it, so both are dead. " +
                "Migration 1802 creates it — has the migrate step run?");
        }
        else
        {
            if (status.MaterializedOnly)
            {
                logger.LogWarning(
                    "telemetry.fleet_health_5m has materialized_only = true, so a read sees only " +
                    "materialised buckets and never the live tail. The window that has just closed will " +
                    "read as zero vehicles reporting, which is indistinguishable from a total outage. " +
                    "Migration 1802 sets it false.");
            }

            if (!status.HasRefreshPolicy)
            {
                logger.LogWarning(
                    "telemetry.fleet_health_5m has no refresh policy, so nothing but this service " +
                    "materialises it and every read outside the refreshed window rescans raw chunks. " +
                    "Migration 1802 adds the policy.");
            }
        }

        if (_options.WindowMin != AggregateBucketMinutes)
        {
            logger.LogError(
                "Health:WindowMin is {Configured} minutes but telemetry.fleet_health_5m buckets are " +
                "{Bucket} minutes wide. The alert's numerator comes from that aggregate and its " +
                "denominator from the tracker roster, so the percentage is being computed over two " +
                "different windows and US-3.16's threshold means nothing.",
                _options.WindowMin, AggregateBucketMinutes);
        }

        return status;
    }

    public async Task<bool> RefreshWindowAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (!_options.RefreshAggregateEnabled)
        {
            return false;
        }

        try
        {
            await repository.RefreshAggregateAsync(start, end, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately swallowed. `materialized_only = false` means the read that follows is still
            // correct — it just pays for a raw-chunk scan of the tail — so a refresh that could not run
            // must not stop an alert from being raised. The one thing it must not do is pass silently.
            logger.LogWarning(
                exception,
                "Could not refresh telemetry.fleet_health_5m over [{Start:o}, {End:o}); the window will be " +
                "read from raw chunks instead",
                start, end);

            return false;
        }
    }
}
