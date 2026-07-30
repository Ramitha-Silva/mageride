using System.Globalization;
using MageRide.Query.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Observability;
using MageRide.Shared.Primitives;
using MageRide.Shared.Realtime;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Query.Live;

/// <summary>
/// A vehicle's current position and denormalised identity, as <c>veh:meta:{vehicleId}</c> holds it.
/// </summary>
/// <param name="VehicleId">The vehicle.</param>
/// <param name="Point">Where its last accepted sample put it.</param>
/// <param name="HeadingDeg">Course over ground, or <see langword="null"/> if the device reported none.</param>
/// <param name="SpeedMps">Speed over ground, or <see langword="null"/>.</param>
/// <param name="Type">Canonical vehicle type (AL-09), or <see langword="null"/> if not denormalised.</param>
/// <param name="Mode">A, B or C, or <see langword="null"/> if not denormalised.</param>
/// <param name="SampleTs">The sample's GNSS instant — the input to US-7.17's freshness rule.</param>
public sealed record LiveVehicle(
    Guid VehicleId,
    GeoPoint Point,
    int? HeadingDeg,
    double? SpeedMps,
    string? Type,
    string? Mode,
    DateTimeOffset? SampleTs);

/// <summary>What one <c>GEOSEARCH</c> over the live index produced.</summary>
/// <param name="Vehicles">The candidates that had a readable <c>veh:meta</c> hash.</param>
/// <param name="Unresolved">Candidates in the GEO index with no readable position hash.</param>
/// <param name="Truncated">
/// How many candidates were dropped at <see cref="QueryOptions.MaxVehicles"/> before any of this ran.
/// </param>
/// <param name="LimitedLive">
/// <see langword="true"/> when the live index could not be read at all — ADD §12's
/// <c>limited_live</c>.
/// </param>
public sealed record LiveVehicleCandidates(
    IReadOnlyList<LiveVehicle> Vehicles,
    int Unresolved,
    int Truncated,
    bool LimitedLive)
{
    /// <summary>The degraded answer: the live index is unreachable, so nothing is known.</summary>
    public static readonly LiveVehicleCandidates Unavailable = new([], 0, 0, true);
}

/// <summary>
/// Reads the live geospatial state position-processor-svc maintains (ADD §8, §9.4).
/// </summary>
public interface ILiveVehicleIndex
{
    /// <summary>Vehicles whose last known position is within <paramref name="radiusM"/> of a point.</summary>
    Task<LiveVehicleCandidates> SearchAsync(
        GeoPoint centre, int radiusM, CancellationToken cancellationToken);

    /// <summary>Reads specific vehicles by id, whatever their position. Absent vehicles are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, LiveVehicle>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the per-vehicle facts the visibility rules need that are not on the position hash:
    /// <c>veh:engaged:{vehicleId}</c> and <c>veh:offline:{vehicleId}</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, VehicleState>> ReadStateAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);

    /// <summary>Whether <c>share:{userId}</c> entitles a passenger to watch a vehicle (D-23).</summary>
    Task<ISet<Guid>> ReadEntitlementsAsync(
        Guid userId, IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILiveVehicleIndex"/>
/// <remarks>
/// <para>
/// <b>Every key here has exactly one writer and this service is not it.</b> <c>geo:live</c>,
/// <c>veh:meta:{vehicleId}</c> — position-processor-svc (C039). <c>veh:engaged:{vehicleId}</c>,
/// <c>veh:offline:{vehicleId}</c>, <c>share:{userId}</c> — fanout-svc (C041). query-svc reads all
/// five and writes none, which is what makes the snapshot and the socket two views of one state
/// rather than two states.
/// </para>
/// <para>
/// <b><c>geo:live</c> is a superset of the live fleet, and that is why the post-filter is not
/// optional.</b> <c>GEOADD</c> replaces a member's position but nothing ever removes one: a GEO set
/// has no per-member TTL, C039 does not <c>GEOREM</c>, and C041's stale sweep works on the cell
/// streams instead. So the index accumulates every vehicle that has ever published — a
/// <c>GEOSEARCH</c> returns vehicles that stopped reporting last year, at the place they stopped.
/// Every candidate is therefore re-read from <c>veh:meta</c>, which <em>does</em> expire
/// (<c>PositionProcessor:VehicleMetaTtl</c>, 10 min), and re-measured against its own current
/// position. A candidate with no hash is not "somewhere approximate", it is unknown, and it is
/// dropped.
/// </para>
/// <para>
/// <b>The radius is applied twice, deliberately.</b> Redis GEO is geohash-based and its own
/// documentation admits errors up to 0.5 % at the search boundary; the second measurement is a
/// haversine against the position actually read back. Both directions matter: the exact pass drops a
/// vehicle Redis included from just outside, and the <em>inflated</em> search radius is what stops it
/// missing one Redis excluded from just inside.
/// </para>
/// <para>
/// <b>A Redis failure degrades rather than 500s.</b> ADD §12's resilience table is explicit —
/// "Redis failure … query-svc returns <c>limited_live</c> flag". A passenger opening the map during a
/// cache outage gets an empty map that says it is incomplete, which is recoverable; a 500 is a screen
/// they cannot use at all. The counter is what tells an operator it is happening.
/// </para>
/// </remarks>
public sealed class LiveVehicleIndex(
    IConnectionMultiplexer redis,
    IOptions<QueryOptions> options,
    ILogger<LiveVehicleIndex> logger) : ILiveVehicleIndex
{
    /// <summary>
    /// How far past the requested radius the GEO search reaches before the exact pass narrows it.
    /// </summary>
    /// <remarks>
    /// 1 % — twice Redis's own documented worst-case geohash error at the boundary, so a vehicle the
    /// index would have excluded from just inside the line is still a candidate for the haversine to
    /// judge. Cheap: the extra area is ~2 % and every candidate is post-filtered anyway.
    /// </remarks>
    private const double SearchInflation = 1.01;

    /// <summary>
    /// The <c>veh:meta</c> fields this service reads, in the order the parser expects.
    /// </summary>
    /// <remarks>
    /// These names are position-processor-svc's <c>MetaFields</c>. The two services cannot reference
    /// each other, so the names <em>are</em> the contract and <c>NearbyVisibilityTests</c> asserts
    /// them against a hash a real processor wrote — the same arrangement fanout-svc's
    /// <c>VehicleSnapshotReader</c> is under.
    /// </remarks>
    private static readonly RedisValue[] MetaFields =
    [
        "lat", "lng", "heading", "speed", "type", "mode", "sampleTs",
    ];

    private readonly QueryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<LiveVehicleCandidates> SearchAsync(
        GeoPoint centre, int radiusM, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radiusM);

        GeoRadiusResult[] hits;

        try
        {
            hits = await redis.GetDatabase().GeoSearchAsync(
                RedisKeys.GeoLive,
                centre.Longitude,
                centre.Latitude,
                new GeoSearchCircle(radiusM * SearchInflation, GeoUnit.Meters),
                // +1 so truncation is detectable: a page exactly at the ceiling is indistinguishable
                // from a page that was cut, and a silently cut map is the failure this bound risks.
                count: _options.MaxVehicles + 1,
                demandClosest: true,
                options: GeoRadiusOptions.None);
        }
        catch (RedisException failure)
        {
            MageRideDiagnostics.NearbyLimitedLive.Add(1);

            logger.LogError(
                failure,
                "geo:live is unreachable; serving a limited-live snapshot (ADD §12). "
                + "The live map is degraded until Redis recovers.");

            return LiveVehicleCandidates.Unavailable;
        }
        catch (TimeoutException failure)
        {
            MageRideDiagnostics.NearbyLimitedLive.Add(1);

            logger.LogError(failure, "geo:live timed out; serving a limited-live snapshot (ADD §12).");

            return LiveVehicleCandidates.Unavailable;
        }

        var truncated = 0;
        var candidates = new List<Guid>(Math.Min(hits.Length, _options.MaxVehicles));

        foreach (var hit in hits)
        {
            if (candidates.Count == _options.MaxVehicles)
            {
                truncated = hits.Length - candidates.Count;
                break;
            }

            if (Guid.TryParse(hit.Member.ToString(), out var vehicleId))
            {
                candidates.Add(vehicleId);
            }
        }

        if (truncated > 0)
        {
            // Said out loud rather than absorbed: from the outside a truncated map and a quiet city
            // look the same. `demandClosest` means what was dropped is the farthest, which is the
            // least-bad thing to drop and still a thing that was dropped.
            logger.LogWarning(
                "Nearby snapshot at {Radius} m hit the {Ceiling}-vehicle ceiling; {Dropped} farther "
                + "vehicles were not returned (Query:MaxVehicles).",
                radiusM, _options.MaxVehicles, truncated);
        }

        var resolved = await ReadAsync(candidates, cancellationToken);

        // The exact pass. Measured against the position `veh:meta` reports, not the one the GEO index
        // held — a vehicle that moved between the two writes is where its hash says it is.
        var inRadius = new List<LiveVehicle>(resolved.Count);

        foreach (var vehicle in resolved.Values)
        {
            if (GeoMath.DistanceM(centre, vehicle.Point) <= radiusM)
            {
                inRadius.Add(vehicle);
            }
            else
            {
                MageRideDiagnostics.NearbyVehiclesFiltered.Add(
                    1, new KeyValuePair<string, object?>("reason", "out_of_radius"));
            }
        }

        var unresolved = candidates.Count - resolved.Count;

        if (unresolved > 0)
        {
            MageRideDiagnostics.NearbyVehiclesFiltered.Add(
                unresolved, new KeyValuePair<string, object?>("reason", "unknown"));
        }

        return new LiveVehicleCandidates(inRadius, unresolved, truncated, LimitedLive: false);
    }

    public async Task<IReadOnlyDictionary<Guid, LiveVehicle>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        var vehicles = new Dictionary<Guid, LiveVehicle>(vehicleIds.Count);

        if (vehicleIds.Count == 0)
        {
            return vehicles;
        }

        var batch = redis.GetDatabase().CreateBatch();
        var pending = new List<(Guid VehicleId, Task<RedisValue[]> Values)>(vehicleIds.Count);

        foreach (var vehicleId in vehicleIds)
        {
            pending.Add((vehicleId, batch.HashGetAsync(RedisKeys.VehicleMeta(vehicleId), MetaFields)));
        }

        batch.Execute();

        foreach (var (vehicleId, task) in pending)
        {
            var values = await task;

            // No readable position: the hash aged out, or the vehicle has never published. Omitted,
            // because the alternative is drawing a marker at a coordinate the GEO index remembers
            // from an unknown time — which is precisely the vehicle US-7.17 exists to remove.
            if (!TryReadDouble(values[0], out var lat) || !TryReadDouble(values[1], out var lng))
            {
                continue;
            }

            vehicles[vehicleId] = new LiveVehicle(
                vehicleId,
                new GeoPoint(lat, lng),
                TryReadDouble(values[2], out var heading) ? (int)Math.Round(heading) : null,
                TryReadDouble(values[3], out var speed) ? speed : null,
                values[4].IsNullOrEmpty ? null : values[4].ToString(),
                values[5].IsNullOrEmpty ? null : values[5].ToString(),
                ReadInstant(values[6]));
        }

        return vehicles;
    }

    public async Task<IReadOnlyDictionary<Guid, VehicleState>> ReadStateAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        var states = new Dictionary<Guid, VehicleState>(vehicleIds.Count);

        if (vehicleIds.Count == 0)
        {
            return states;
        }

        var batch = redis.GetDatabase().CreateBatch();
        var pending = new List<(Guid VehicleId, Task<RedisValue> Engaged, Task<RedisValue> Offline)>(
            vehicleIds.Count);

        foreach (var vehicleId in vehicleIds)
        {
            pending.Add((
                vehicleId,
                batch.StringGetAsync(RedisKeys.VehicleEngagement(vehicleId)),
                batch.StringGetAsync(RedisKeys.VehicleOfflineAt(vehicleId))));
        }

        batch.Execute();

        foreach (var (vehicleId, engagedTask, offlineTask) in pending)
        {
            var engaged = await engagedTask;
            var offline = await offlineTask;

            states[vehicleId] = new VehicleState(
                Guid.TryParse(engaged.ToString(), out var rideId) ? rideId : null,
                ReadInstant(offline));
        }

        return states;
    }

    public async Task<ISet<Guid>> ReadEntitlementsAsync(
        Guid userId, IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        var entitled = new HashSet<Guid>();

        if (vehicleIds.Count == 0)
        {
            return entitled;
        }

        // SMISMEMBER, not SMEMBERS: a passenger entitled to a school run and an office van has two
        // members, but a fleet manager's account could hold hundreds, and the question asked is only
        // ever about the handful of Mode B vehicles that are actually on this screen.
        var members = vehicleIds.Select(static id => (RedisValue)id.ToString()).ToArray();

        var results = await redis.GetDatabase().SetContainsAsync(RedisKeys.Share(userId), members);

        var index = 0;
        foreach (var vehicleId in vehicleIds)
        {
            if (results[index++])
            {
                entitled.Add(vehicleId);
            }
        }

        return entitled;
    }

    private static bool TryReadDouble(RedisValue value, out double parsed) =>
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

    private static DateTimeOffset? ReadInstant(RedisValue value) =>
        !value.IsNullOrEmpty
        && DateTimeOffset.TryParse(
            value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
