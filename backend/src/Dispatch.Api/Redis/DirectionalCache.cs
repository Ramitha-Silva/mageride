using System.Globalization;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Caching;
using MageRide.Shared.Time;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MageRide.Dispatch.Redis;

/// <summary>
/// Directional Travel's two Redis keys (ADD §9.4, DT-01/DT-03):
/// <c>driver:directional:{driverId}</c> — a HASH of <c>{filterId, destLat, destLng, label,
/// expiresAt, usedDate}</c> whose TTL is the filter's remaining duration — and
/// <c>driver:directional:uses:{driverId}:{yyyy-mm-dd}</c>, the per-Colombo-day activation counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both are written here and read by nothing in this service</b>, and that is the deliberate
/// half of C036's design rather than an oversight. ADD §9.4 marks the hash "fast hint; authoritative
/// expiry in <c>dispatch.timers</c>" and the counter as the DT-03 enforcement aid — but a Redis miss
/// and "this driver has no filter" are indistinguishable to a reader, so a flushed keyspace would
/// switch the feature off in silence: the predicate would stop excluding anything and the driver's
/// app would report no filter while the durable row still held one. Both readers therefore take the
/// durable row (see <see cref="Eligibility.DirectionalGate"/> and
/// <see cref="Directional.DirectionalService"/>), and these keys exist because ADD §9.4 specifies
/// the shape of the keyspace and something else may yet consume it. Exactly the position C034
/// recorded for <c>driver:availability</c>'s <c>level</c> and <c>walletOk</c> fields.
/// </para>
/// <para>
/// <b>Every method swallows a <see cref="RedisException"/>.</b> A cache nothing depends on for
/// correctness must never be able to fail a driver's request; the alternative is an unreachable
/// Redis turning "set my destination filter" into a 500 for a feature whose durable state was
/// already committed.
/// </para>
/// </remarks>
public interface IDirectionalCache
{
    /// <summary>Writes the hint and PEXPIREs it to the filter's remaining life (DT-01).</summary>
    Task SetAsync(DirectionalFilterRow filter, TimeSpan remaining, CancellationToken cancellationToken);

    /// <summary>Drops the hint — the filter has cleared, whichever of DT-04's four ways it was.</summary>
    Task ClearAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>INCR</c>s the day's activation counter and gives it a 36 h TTL (ADD §9.4). Mirrors an
    /// activation row that has already been committed; the count it produces is never the one DT-03
    /// is enforced against.
    /// </summary>
    Task IncrementUsesAsync(Guid driverId, DateOnly businessDate, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDirectionalCache"/>
public sealed class DirectionalCache(IConnectionMultiplexer redis, ILogger<DirectionalCache> logger)
    : IDirectionalCache
{
    /// <summary>
    /// How long the per-day counter outlives its day. ADD §9.4 pins 36 h — a Colombo day and a half,
    /// so a key written at 23:59 local is still there for anyone reading the day out, and a counter
    /// for a day that has ended cannot be mistaken for the current one.
    /// </summary>
    private static readonly TimeSpan UsesTtl = TimeSpan.FromHours(36);

    private IDatabase Db => redis.GetDatabase();

    public async Task SetAsync(DirectionalFilterRow filter, TimeSpan remaining, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            var key = RedisKeys.DriverDirectional(filter.DriverId);
            var db = Db;

            await db.HashSetAsync(key,
            [
                new HashEntry("filterId", filter.Id.ToString()),
                new HashEntry("destLat", filter.Destination.Latitude.ToString("R", CultureInfo.InvariantCulture)),
                new HashEntry("destLng", filter.Destination.Longitude.ToString("R", CultureInfo.InvariantCulture)),
                new HashEntry("label", filter.Label ?? string.Empty),
                new HashEntry("expiresAt", filter.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
                new HashEntry("usedDate", BusinessCalendar.DateKey(filter.UsedDate)),
            ]);

            // PEXPIRE to what is *left* of the duration rather than to the whole of it: the row was
            // committed a moment ago, but a retried request or a slow commit means "now + 2 h" and
            // "expires_at" are not the same instant, and the key must never outlive the row.
            await db.KeyExpireAsync(key, remaining);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(
                exception,
                "Could not cache the Directional Travel filter for driver {DriverId}; the durable row and its " +
                "expiry timer are unaffected",
                filter.DriverId);
        }
    }

    public async Task ClearAsync(Guid driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await Db.KeyDeleteAsync(RedisKeys.DriverDirectional(driverId));
        }
        catch (RedisException exception)
        {
            logger.LogWarning(
                exception, "Could not drop the Directional Travel hint for driver {DriverId}", driverId);
        }
    }

    public async Task IncrementUsesAsync(Guid driverId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var key = RedisKeys.DriverDirectionalUses(driverId, businessDate);
            var db = Db;

            await db.StringIncrementAsync(key);

            // Unconditional rather than only-when-new: EXPIRE on a key that already has one simply
            // re-arms it, and a counter that lost its TTL would live until the next flush.
            await db.KeyExpireAsync(key, UsesTtl);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(
                exception,
                "Could not increment the Directional Travel day counter for driver {DriverId}; DT-03 is enforced " +
                "from dispatch.directional_filters and is unaffected",
                driverId);
        }
    }
}
