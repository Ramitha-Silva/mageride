using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Observability;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.HotPath.PositionProcessor.Redis;

/// <summary>What one sample did to the R-08 candidate pool.</summary>
public enum PoolChange
{
    /// <summary>No driver is on standby with this vehicle. The ordinary case.</summary>
    NoDriver,

    /// <summary>The driver's availability lapsed or they are not AVAILABLE; they are out of the pool.</summary>
    Removed,

    /// <summary>The driver was already indexed in this cell; only the 60 s TTL moved.</summary>
    Refreshed,

    /// <summary>The driver was put into a cell they were not in — a new entry, or a move.</summary>
    Indexed,
}

/// <summary>
/// Where this service last put a vehicle's driver in the candidate pool.
/// </summary>
/// <remarks>
/// Both halves are needed to undo it, and neither can be recovered later: the member is the driver
/// id, which only <see cref="RedisKeys.VehicleDriver"/> gives and which disappears the moment the
/// driver goes offline; the key is the res-5 cell, which only the position at the time gives and
/// which has changed by the time the driver leaves. So it is remembered on <c>veh:meta</c>, whose
/// TTL comfortably outlives the availability hash's 60 s.
/// </remarks>
/// <param name="DriverId">The driver indexed.</param>
/// <param name="CellKey">The full <c>geo:drivers:available:{type}:{res5cell}</c> key.</param>
public sealed record PoolMembership(Guid DriverId, string CellKey)
{
    private const char Separator = '|';

    /// <summary>Renders as the single <c>veh:meta</c> field value <see cref="TryParse"/> reads.</summary>
    /// <remarks>
    /// One field rather than two because it is one fact: a driver id with no cell, or a cell with no
    /// driver, cannot remove anything, and two fields make a half-written pair representable.
    /// </remarks>
    public override string ToString() => $"{DriverId}{Separator}{CellKey}";

    /// <summary>Reads back what <see cref="ToString"/> wrote, or <see langword="null"/>.</summary>
    public static PoolMembership? TryParse(string? value)
    {
        if (value is null or { Length: 0 })
        {
            return null;
        }

        var split = value.IndexOf(Separator, StringComparison.Ordinal);

        return split > 0
               && Guid.TryParse(value.AsSpan(0, split), out var driverId)
               && split + 1 < value.Length
            ? new PoolMembership(driverId, value[(split + 1)..])
            : null;
    }
}

/// <summary>The result of reconciling one sample against the pool, and where it left the driver.</summary>
/// <param name="Change">What happened.</param>
/// <param name="Membership">
/// Where the driver now is, or <see langword="null"/> when they are not in the pool. Remembered on
/// <c>veh:meta</c> so the next sample can take them out of exactly the right key.
/// </param>
public readonly record struct PoolReconciliation(PoolChange Change, PoolMembership? Membership = null)
{
    /// <summary>Nothing to do — no driver on standby with this vehicle.</summary>
    public static readonly PoolReconciliation None = new(PoolChange.NoDriver);
}

/// <summary>
/// R-08's dispatch candidate index, kept at the driver's live position (ADD §9.4).
/// </summary>
public interface IDriverAvailabilityIndex
{
    /// <summary>
    /// Reconciles the candidate pool to one accepted live sample.
    /// </summary>
    /// <param name="sample">The accepted sample. Only ever a live one — see the remarks on the
    /// implementation.</param>
    /// <param name="previous">What <c>veh:meta</c> remembers, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PoolReconciliation> ReconcileAsync(
        PositionSample sample, PoolMembership? previous, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverAvailabilityIndex"/>
/// <remarks>
/// <para>
/// <b>ADD §9.4 gives these two keys to position-processor-svc and a position sample carries no
/// driver.</b> That is the whole difficulty of R-08, and it is why C024 left the heartbeat unwritten
/// and C034 landed a version of it in dispatch-svc. The telemetry plane is keyed by
/// <c>vehicleId</c> end to end because EMQX authenticates a <i>vehicle</i>
/// (<c>mqtt-topics.md</c> §1); the dispatch plane is keyed by <c>driverId</c> because a ride is
/// offered to a person. <see cref="RedisKeys.VehicleDriver"/> is the binding between them, written
/// by dispatch-svc at the two moments the pair is established and dissolved, and read here.
/// </para>
/// <para>
/// <b>This service tracks; it does not declare.</b> It never creates an availability hash and never
/// adds a driver the hash does not already say is <c>AVAILABLE</c> — the phase is dispatch-svc's
/// fact, established by <c>POST /v1/standby/online</c> and moved by the offer loop, and a hash
/// resurrected here with one field and no TTL would read to every later caller as "this driver is
/// online, position unknown". What this owns is everything a <i>position</i> decides: which res-5
/// cell the driver is discoverable from, and the 60 s clock that says they are still there. The two
/// writers therefore cannot disagree — one says who is in the pool, the other says where.
/// </para>
/// <para>
/// <b>Live samples only.</b> A backlog (R-17) arrives with a fresh receive time and a stale capture
/// time; refreshing presence from it would advertise a driver as available at the position they held
/// an hour ago, which is precisely the case D5' §3.2's freshness gate exists to refuse.
/// </para>
/// <para>
/// <b>Nothing here is authoritative.</b> ADD §11.11 makes Postgres the authoritative writer and
/// dispatch-svc's exact post-filter reads <c>dispatch.driver_presence</c>; losing this whole
/// keyspace costs a round of candidates and nothing else. Which is also why every failure below is
/// swallowed and counted rather than propagated: a Redis blip must not stall a partition of
/// positions that the live map still needs.
/// </para>
/// </remarks>
public sealed class DriverAvailabilityIndex(
    IConnectionMultiplexer redis,
    IOptions<PositionProcessorOptions> options,
    ILogger<DriverAvailabilityIndex> logger) : IDriverAvailabilityIndex
{
    /// <summary>The <c>state</c> value that means "in the candidate pool" (dispatch-svc's <c>PresenceStates</c>).</summary>
    /// <remarks>
    /// Spelled here rather than referenced: this project does not depend on Dispatch.Api and must
    /// not. It is asserted against dispatch-svc's own constant in the C039 test suite, which is
    /// where a divergence should fail rather than in production as an empty candidate set.
    /// </remarks>
    public const string AvailableState = "AVAILABLE";

    /// <summary>
    /// Fields of <c>driver:availability:{driverId}</c> this service touches — ADD §9.4's shape, and
    /// dispatch-svc's <c>DriverIndex</c> writes the same names.
    /// </summary>
    /// <remarks>
    /// <c>state</c> and <c>lastSeen</c> and <c>cell</c> appear as literals inside
    /// <see cref="ReconcileScript"/> because a Lua body is a compile-time constant; this is the list
    /// they have to match. <c>level</c> and <c>walletOk</c> are deliberately untouched — they are
    /// dispatch-svc's, written once and read by nothing.
    /// </remarks>
    private const string VehicleTypeField = "vehicleType";

    /// <summary>
    /// The whole reconciliation, in one round trip, so no other writer can interleave between
    /// reading the driver's phase and acting on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The existence check and the write have to be atomic for the same reason dispatch-svc's
    /// heartbeat script gives: an <c>HSET</c> on a key whose TTL lapsed a millisecond ago resurrects
    /// it with one field and no expiry, and that hash then says "online, position unknown" for ever.
    /// </para>
    /// <para>
    /// KEYS[1] = driver:availability:{driverId}
    /// KEYS[2] = the pool cell the driver was last put in ("" when none)
    /// KEYS[3] = the pool cell this sample puts them in ("" when the phase is not AVAILABLE)
    /// ARGV[1] = driverId  ARGV[2] = lastSeen (ISO-8601)  ARGV[3] = ttl ms
    /// ARGV[4] = longitude  ARGV[5] = latitude  ARGV[6] = the res-5 cell id  ARGV[7] = AVAILABLE
    ///
    /// Returns 0 = the hash is gone (removed), 1 = present but not AVAILABLE (removed),
    ///         2 = AVAILABLE and already in this cell (refreshed), 3 = AVAILABLE and (re)indexed.
    /// </para>
    /// </remarks>
    private const string ReconcileScript =
        """
        local previous = KEYS[2]
        local target = KEYS[3]

        if redis.call('EXISTS', KEYS[1]) == 0 then
          if previous ~= '' then redis.call('ZREM', previous, ARGV[1]) end
          return 0
        end

        redis.call('HSET', KEYS[1], 'lastSeen', ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])

        if target == '' or redis.call('HGET', KEYS[1], 'state') ~= ARGV[7] then
          if previous ~= '' then redis.call('ZREM', previous, ARGV[1]) end
          return 1
        end

        if previous == target then
          redis.call('GEOADD', target, ARGV[4], ARGV[5], ARGV[1])
          return 2
        end

        if previous ~= '' then redis.call('ZREM', previous, ARGV[1]) end
        redis.call('GEOADD', target, ARGV[4], ARGV[5], ARGV[1])
        redis.call('HSET', KEYS[1], 'cell', ARGV[6])
        return 3
        """;

    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PoolReconciliation> ReconcileAsync(
        PositionSample sample, PoolMembership? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        cancellationToken.ThrowIfCancellationRequested();

        var db = redis.GetDatabase();

        var bound = await db.StringGetAsync(RedisKeys.VehicleDriver(sample.VehicleId));

        if (!Guid.TryParse(bound.ToString(), out var driverId) || driverId == Guid.Empty)
        {
            // No Mode C driver is on standby with this vehicle. The ordinary case by a wide margin:
            // telemetry.raw carries every Mode A bus and every Mode B shared vehicle on the
            // platform, and dispatch has a presence row for a small fraction of them.
            //
            // A membership remembered from an earlier standby is still undone, because a driver
            // going offline is exactly how the binding disappears — and a GEO set has no TTL, so
            // nothing else would ever take them out.
            return previous is null
                ? PoolReconciliation.None
                : await ForgetAsync(db, previous, sample);
        }

        // The tier keying the pool comes from the availability hash, not from the sample: it is what
        // dispatch-svc indexed the driver under and what its candidate build reads back. A sample's
        // own `vehicleType` is denormalised by the publisher and a disagreement between the two
        // would put the driver in a key nobody looks in.
        var availability = RedisKeys.DriverAvailability(driverId);
        var vehicleType = await db.HashGetAsync(availability, VehicleTypeField);

        var cell = GeoCells.DispatchCell(sample.Point);

        // Empty when the hash is gone or carries no tier — the script reads that as "take them out
        // and put them nowhere", which is the only safe answer when the key that says where they
        // belong has expired.
        var target = vehicleType.IsNullOrEmpty
            ? string.Empty
            : RedisKeys.AvailableDrivers(vehicleType!, cell);

        // A driver who switched vehicles is still indexed under the old one's membership. The
        // previous key is only ours to undo when it names this same driver.
        var previousKey = previous is { } held && held.DriverId == driverId ? held.CellKey : string.Empty;

        var result = (long)await db.ScriptEvaluateAsync(
            ReconcileScript,
            [availability, previousKey, target],
            [
                driverId.ToString(),
                sample.SampleTs.ToString("O", CultureInfo.InvariantCulture),
                (long)_options.DriverAvailabilityTtl.TotalMilliseconds,
                sample.Lng,
                sample.Lat,
                cell,
                AvailableState,
            ]);

        if (previous is { } orphan && orphan.DriverId != driverId)
        {
            // The vehicle changed hands while the old driver was still indexed. Theirs to undo, and
            // nobody else will: their own vehicle's samples no longer carry this membership.
            await ForgetAsync(db, orphan, sample);
        }

        return Report(result, driverId, target, previousKey, sample);
    }

    /// <summary>Takes a driver out of the cell this service put them in.</summary>
    private async Task<PoolReconciliation> ForgetAsync(IDatabase db, PoolMembership held, PositionSample sample)
    {
        var removed = await db.SortedSetRemoveAsync(held.CellKey, held.DriverId.ToString());

        if (removed)
        {
            MageRideDiagnostics.DriverPoolChanges.Add(1, new KeyValuePair<string, object?>("change", "removed"));

            logger.LogInformation(
                "Driver {DriverId} is no longer bound to vehicle {VehicleId}; removed from {Cell}",
                held.DriverId, sample.VehicleId, held.CellKey);
        }

        return new PoolReconciliation(PoolChange.Removed);
    }

    private PoolReconciliation Report(
        long result, Guid driverId, string target, string previousKey, PositionSample sample)
    {
        switch (result)
        {
            case 0 or 1:
                if (previousKey.Length > 0)
                {
                    MageRideDiagnostics.DriverPoolChanges.Add(
                        1, new KeyValuePair<string, object?>("change", "removed"));

                    logger.LogDebug(
                        "Driver {DriverId} left the candidate pool ({Reason})",
                        driverId, result == 0 ? "availability lapsed" : "phase is not AVAILABLE");
                }

                return new PoolReconciliation(PoolChange.Removed);

            case 2:
                MageRideDiagnostics.DriverPoolChanges.Add(
                    1, new KeyValuePair<string, object?>("change", "refreshed"));

                return new PoolReconciliation(PoolChange.Refreshed, new PoolMembership(driverId, target));

            default:
                MageRideDiagnostics.DriverPoolChanges.Add(
                    1,
                    new KeyValuePair<string, object?>("change", previousKey.Length > 0 ? "moved" : "added"));

                logger.LogDebug(
                    "Driver {DriverId} on vehicle {VehicleId} is discoverable from {Cell}",
                    driverId, sample.VehicleId, target);

                return new PoolReconciliation(PoolChange.Indexed, new PoolMembership(driverId, target));
        }
    }
}
