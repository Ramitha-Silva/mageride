using MageRide.Shared.Geo;
using System.Globalization;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Dispatch.Redis;

/// <summary>
/// The R-08 Redis candidate index and the R-10 driver reservation (ADD §9.4).
/// </summary>
/// <remarks>
/// <para>
/// Three key families, all spelled by <see cref="RedisKeys"/> so
/// <c>position-processor-svc</c> (C039) and this service cannot disagree about where a value
/// lives: <c>geo:drivers:available:{vehicleType}:{h3Res5Cell}</c> (GEO, membership only),
/// <c>driver:availability:{driverId}</c> (HASH, TTL 60 s) and
/// <c>lock:driver-offer:{driverId}</c> + <c>offer:{rideId}</c> (the reservation pair).
/// </para>
/// <para>
/// <b>None of this is authoritative.</b> ADD §9.4 marks <c>offer:{rideId}</c> "fast hint, NOT
/// authoritative", ADD §11.11 makes Postgres the authoritative writer, and every method here is
/// written so that losing the whole keyspace costs latency and nothing else: presence lives in
/// <c>dispatch.driver_presence</c>, the single-live-offer rule lives in
/// <c>ux_offers_driver_live</c>, and expiry lives in <c>rides.timers</c>.
/// </para>
/// </remarks>
public interface IDriverIndex
{
    /// <summary>Adds a driver to their cell's GEO set and refreshes the availability hash.</summary>
    Task IndexAvailableAsync(
        Guid driverId, Guid vehicleId, string vehicleType, GeoPoint position, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a driver out of the candidate pool and records why in the availability hash. The cell
    /// is read back from the hash rather than recomputed, because a driver who moved between going
    /// online and leaving would otherwise be deleted from the wrong key and left in the pool.
    /// </summary>
    /// <param name="newState">One of <see cref="PresenceStates"/> — OFFERED or ON_RIDE.</param>
    Task RemoveFromPoolAsync(Guid driverId, string newState, CancellationToken cancellationToken);

    /// <summary>Drops the availability hash as well — the driver is off duty.</summary>
    Task ForgetAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// The H3 pre-filter's raw result: every driver id in any of <paramref name="cells"/> for this
    /// tier, deduplicated. <b>Membership only — no distance is applied here</b> (R-06).
    /// </summary>
    Task<IReadOnlyList<Guid>> PreFilterAsync(
        string vehicleType, IReadOnlyList<string> cells, CancellationToken cancellationToken);

    /// <summary>
    /// D5' §3.6's Lua reservation: <c>SET lock:driver-offer:{driverId} NX PX ttl</c> combined with
    /// writing <c>offer:{rideId}</c>, in one round trip so no other worker can interleave.
    /// </summary>
    Task<bool> TryReserveAsync(
        Guid driverId, Guid rideId, Guid offerId, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the reservation, but only if this offer still owns it — a blind <c>DEL</c> would
    /// let a slow expiry sweep unlock a driver who has since been offered a different ride.
    /// </summary>
    Task ReleaseReservationAsync(Guid driverId, Guid rideId, Guid offerId, CancellationToken cancellationToken);

    /// <summary>Realigns the <c>offer:{rideId}</c> key to ride-svc's authoritative deadline (D-07).</summary>
    Task RefreshOfferDeadlineAsync(Guid rideId, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Reads back an offer hint, for the keyspace-expiry path and for tests.</summary>
    Task<OfferHint?> ReadOfferAsync(Guid rideId, CancellationToken cancellationToken);
}

/// <summary>The <c>offer:{rideId}</c> HASH of ADD §9.4 — <c>{driverId, expiresAt, status}</c>.</summary>
public sealed record OfferHint(Guid OfferId, Guid DriverId, DateTimeOffset ExpiresAt, string Status);

/// <inheritdoc cref="IDriverIndex"/>
public sealed class DriverIndex(
    IConnectionMultiplexer redis,
    IOptions<DispatchOptions> options,
    ILogger<DriverIndex> logger) : IDriverIndex
{
    /// <summary>Field of <c>driver:availability:{driverId}</c> holding the cell the driver is indexed in.</summary>
    internal const string CellField = "cell";

    /// <summary>
    /// The reservation, as one script. <c>SET NX</c> alone would leave a window in which the lock
    /// is held but <c>offer:{rideId}</c> does not exist yet, which is precisely the phantom the
    /// fast path is meant to prevent; Lua makes the pair atomic (ADD §9.4's
    /// <c>lock:driver-offer</c> row says so in as many words).
    ///
    /// KEYS[1] = lock:driver-offer:{driverId}   KEYS[2] = offer:{rideId}
    /// ARGV[1] = offerId  ARGV[2] = driverId  ARGV[3] = expiresAt (ISO-8601)  ARGV[4] = ttl ms
    /// </summary>
    private const string ReserveScript =
        """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[4]) then
          redis.call('HSET', KEYS[2], 'offerId', ARGV[1], 'driverId', ARGV[2],
                                      'expiresAt', ARGV[3], 'status', 'OFFERED')
          redis.call('PEXPIRE', KEYS[2], ARGV[4])
          return 1
        end
        return 0
        """;

    /// <summary>
    /// Compare-and-delete. KEYS as above; ARGV[1] = offerId. Releasing only what this offer holds
    /// is what keeps a late sweep from unlocking a driver the next round already reserved.
    /// </summary>
    private const string ReleaseScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          redis.call('DEL', KEYS[1])
        end
        if redis.call('HGET', KEYS[2], 'offerId') == ARGV[1] then
          redis.call('DEL', KEYS[2])
        end
        return 1
        """;

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private IDatabase Db => redis.GetDatabase();

    public async Task IndexAvailableAsync(
        Guid driverId, Guid vehicleId, string vehicleType, GeoPoint position, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleType);
        cancellationToken.ThrowIfCancellationRequested();

        var grid = new H3Grid(_options.H3Resolution, _options.H3RingK);
        var cell = grid.CellAt(position);
        var db = Db;

        // Move, not add: the driver may have been indexed in a different cell a moment ago, and a
        // GEOADD to the new key without a GEOREM from the old one leaves them discoverable from
        // two places at once — one of which is now a lie about where they are.
        await RemoveFromGeoSetAsync(db, driverId);

        await db.GeoAddAsync(
            RedisKeys.AvailableDrivers(vehicleType, cell),
            new GeoEntry(position.Longitude, position.Latitude, driverId.ToString()));

        var availability = RedisKeys.DriverAvailability(driverId);

        await db.HashSetAsync(availability,
        [
            new HashEntry("state", PresenceStates.Available),
            new HashEntry("lastSeen", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new HashEntry("vehicleType", vehicleType),
            new HashEntry("vehicleId", vehicleId.ToString()),
            new HashEntry(CellField, cell),

            // `level` and `walletOk` are part of ADD §9.4's shape and are written so the hash is
            // the documented one from the start. Both are placeholders: the Driver Level engine is
            // C034 and the D-08 wallet cache is C034/C046, and NOTHING in this slice reads either
            // — the candidate build applies no level and no wallet gate (see CandidateRepository).
            new HashEntry("level", 3),
            new HashEntry("walletOk", true),
        ]);

        // TTL 60 s, refreshed on every live GPS sample (R-08). Nothing here refreshes it yet:
        // position-processor-svc (C039) owns the heartbeat, so in this slice a driver drops out of
        // the hash a minute after going online while the durable presence row stays. That is why
        // the exact post-filter reads dispatch.driver_presence and not this hash.
        await db.KeyExpireAsync(availability, _options.PresenceTtl);
    }

    public async Task RemoveFromPoolAsync(Guid driverId, string newState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newState);
        cancellationToken.ThrowIfCancellationRequested();

        var db = Db;
        var removed = await RemoveFromGeoSetAsync(db, driverId);

        if (removed)
        {
            // Only touch the hash when it actually described an indexed driver — an HSET on a
            // missing key would resurrect an expired availability entry with one field set and no
            // TTL, which reads to every later caller as "this driver is online, position unknown".
            await db.HashSetAsync(RedisKeys.DriverAvailability(driverId), "state", newState);
        }
    }

    public async Task ForgetAsync(Guid driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = Db;
        await RemoveFromGeoSetAsync(db, driverId);
        await db.KeyDeleteAsync(RedisKeys.DriverAvailability(driverId));
    }

    public async Task<IReadOnlyList<Guid>> PreFilterAsync(
        string vehicleType, IReadOnlyList<string> cells, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleType);
        ArgumentNullException.ThrowIfNull(cells);
        cancellationToken.ThrowIfCancellationRequested();

        var db = Db;

        // A GEO set is a sorted set, so reading every member is the whole of the pre-filter: no
        // GEOSEARCH, no radius, no ordering. Deliberate — R-06 and this component's DoD both say
        // the cell is never a distance bound, and a Redis-side radius here would make it very easy
        // to believe the exact post-filter downstream was optional.
        var reads = cells
            .Select(cell => db.SortedSetRangeByRankAsync(RedisKeys.AvailableDrivers(vehicleType, cell)))
            .ToArray();

        var results = await Task.WhenAll(reads);

        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>();

        foreach (var member in results.SelectMany(static values => values))
        {
            if (Guid.TryParse(member.ToString(), out var driverId) && seen.Add(driverId))
            {
                ordered.Add(driverId);
            }
        }

        return ordered;
    }

    public async Task<bool> TryReserveAsync(
        Guid driverId, Guid rideId, Guid offerId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

        var result = await Db.ScriptEvaluateAsync(
            ReserveScript,
            [RedisKeys.DriverOfferLock(driverId), RedisKeys.Offer(rideId)],
            [
                offerId.ToString(),
                driverId.ToString(),
                expiresAt.ToString("O", CultureInfo.InvariantCulture),
                (long)ttl.TotalMilliseconds,
            ]);

        return (long)result == 1;
    }

    public async Task ReleaseReservationAsync(
        Guid driverId, Guid rideId, Guid offerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Db.ScriptEvaluateAsync(
            ReleaseScript,
            [RedisKeys.DriverOfferLock(driverId), RedisKeys.Offer(rideId)],
            [offerId.ToString()]);
    }

    public async Task RefreshOfferDeadlineAsync(
        Guid rideId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = RedisKeys.Offer(rideId);
        var remaining = expiresAt - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            // ride-svc's deadline has already passed by the time we heard about it — clock skew or
            // a slow hop. Leave the key to its own TTL; the durable timer is what will fire.
            logger.LogWarning(
                "Offer deadline {ExpiresAt:O} for ride {RideId} is already in the past; leaving the Redis hint alone",
                expiresAt, rideId);
            return;
        }

        var db = Db;
        await db.HashSetAsync(key, "expiresAt", expiresAt.ToString("O", CultureInfo.InvariantCulture));
        await db.KeyExpireAsync(key, remaining);
    }

    public async Task<OfferHint?> ReadOfferAsync(Guid rideId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await Db.HashGetAsync(
            RedisKeys.Offer(rideId), ["offerId", "driverId", "expiresAt", "status"]);

        if (!Guid.TryParse(values[0].ToString(), out var offerId) ||
            !Guid.TryParse(values[1].ToString(), out var driverId) ||
            !DateTimeOffset.TryParse(
                values[2].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return null;
        }

        return new OfferHint(offerId, driverId, expiresAt, values[3].ToString() ?? "OFFERED");
    }

    /// <summary>
    /// GEOREMs a driver from whichever cell key the availability hash says they are in. Returns
    /// whether the hash knew, so callers can tell "removed" from "there was nothing to remove".
    /// </summary>
    private static async Task<bool> RemoveFromGeoSetAsync(IDatabase db, Guid driverId)
    {
        var values = await db.HashGetAsync(
            RedisKeys.DriverAvailability(driverId), [CellField, "vehicleType"]);

        if (values[0].IsNullOrEmpty || values[1].IsNullOrEmpty)
        {
            return false;
        }

        await db.SortedSetRemoveAsync(
            RedisKeys.AvailableDrivers(values[1]!, values[0]!), driverId.ToString());

        return true;
    }
}
