using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Dispatch.Eligibility;

/// <summary>What the DT-02 predicate decided about one candidate.</summary>
/// <param name="Breakdown">
/// The measurements, or <see langword="null"/> when this driver has no active filter — which is
/// most of them, most of the time, and is what keeps <c>candidate_scores.breakdown</c> unchanged
/// for every round nobody has set a destination in.
/// </param>
public sealed record DirectionalVerdict(bool Active, DirectionalBreakdown? Breakdown)
{
    /// <summary>The driver has no Destination Filter, so the predicate does not apply to them.</summary>
    public static readonly DirectionalVerdict NoFilter = new(false, null);

    /// <summary>
    /// Whether this candidate survives the predicate. A driver with no filter always does — DT-05
    /// only ever removes, and it removes from the set of drivers who asked for it.
    /// </summary>
    public bool Allowed => !Active || Breakdown is null || Breakdown.Matched;
}

/// <summary>
/// Reads each round's active Destination Filters and runs D5' §12.1's predicate over them
/// (DT-02, DT-05).
/// </summary>
public interface IDirectionalGate
{
    /// <summary>Evaluates a whole round's candidates.</summary>
    Task<IReadOnlyDictionary<Guid, DirectionalVerdict>> EvaluateAsync(
        NpgsqlConnection connection,
        RideDispatchRequest ride,
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectionalGate"/>
/// <remarks>
/// <para>
/// <b>The filters are read from Postgres, not from Redis, and that is a deliberate departure.</b>
/// ADD §7.4 and §11.11 both say dispatch "reads <c>driver:directional:{driverId}</c> for each
/// surviving candidate". The cache is genuinely there and genuinely written (DT-01's key, with the
/// PEXPIRE the ADD asks for) — but a Redis miss and "this driver has no filter" are the same answer
/// from a reader, so a flushed keyspace would silently switch the whole feature off, and a driver
/// heading home would be sent rides in the wrong direction with nothing anywhere saying why. The
/// durable read costs one indexed round trip on a path that already takes several, and it is the
/// same call C034 made for presence: "the exact post-filter still reads
/// <c>dispatch.driver_presence</c> rather than this hash". Raised as a micro-change-set in the C036
/// handoff.
/// </para>
/// <para>
/// <b>The configuration is only read when somebody has a filter.</b> The common round has none, and
/// then this gate is exactly one query returning nothing — which is what stops a feature a handful
/// of drivers use from costing every dispatch two extra reads.
/// </para>
/// <para>
/// <b>An unreadable filter table opens the gate.</b> This predicate exists to narrow a candidate
/// set on a driver's own preference; a database hiccup that instead narrowed it to nothing would
/// strand passengers for a convenience feature. Failing open here means at worst a driver gets one
/// ride they would rather not have and can decline, which DT-06 already accepts as the shape of the
/// whole mechanism.
/// </para>
/// </remarks>
public sealed class DirectionalGate(
    IDirectionalRepository directional,
    IOptions<DispatchOptions> options,
    ILogger<DirectionalGate> logger) : IDirectionalGate
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyDictionary<Guid, DirectionalVerdict>> EvaluateAsync(
        NpgsqlConnection connection,
        RideDispatchRequest ride,
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ride);
        ArgumentNullException.ThrowIfNull(candidates);

        var verdicts = new Dictionary<Guid, DirectionalVerdict>(candidates.Count);

        foreach (var candidate in candidates)
        {
            verdicts[candidate.DriverId] = DirectionalVerdict.NoFilter;
        }

        if (!_options.DirectionalGateEnabled || candidates.Count == 0)
        {
            return verdicts;
        }

        IReadOnlyList<DirectionalFilterRow> filters;

        try
        {
            filters = await directional.FindActiveForDriversAsync(
                connection, [.. candidates.Select(static c => c.DriverId)], cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            logger.LogError(
                exception,
                "Could not read dispatch.directional_filters for {Count} candidates; the DT-02 predicate is " +
                "skipped for this round and every candidate stays in it",
                candidates.Count);

            return verdicts;
        }

        if (filters.Count == 0)
        {
            return verdicts;
        }

        var config = await directional.GetConfigAsync(connection, cancellationToken);
        var byDriver = filters.ToDictionary(static f => f.DriverId);

        foreach (var candidate in candidates)
        {
            if (!byDriver.TryGetValue(candidate.DriverId, out var filter))
            {
                continue;
            }

            var breakdown = DirectionalPredicate.Evaluate(
                candidate.Geo, candidate.DistanceM, ride.Pickup, ride.Dropoff, filter.Destination, config);

            verdicts[candidate.DriverId] = new DirectionalVerdict(Active: true, breakdown);

            if (!breakdown.Matched)
            {
                logger.LogInformation(
                    "Driver {DriverId} is filtered out of ride {RideId} by their Destination Filter " +
                    "({FailedOn}: bearing {BearingDiff:0.0}° / detour {Detour:0} m / progress {Progress:0} m); " +
                    "no offer, no penalty (DT-02, US-6A.23)",
                    candidate.DriverId, ride.RideId, breakdown.FailedOn, breakdown.BearingDiffDeg,
                    breakdown.DetourM, breakdown.ProgressM);
            }
        }

        return verdicts;
    }
}
