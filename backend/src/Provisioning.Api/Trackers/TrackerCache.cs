using System.Text.Json;
using MageRide.Provisioning.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Provisioning.Trackers;

/// <summary>
/// What a subscriber to <see cref="RedisKeys.TrackerCredentialChannel"/> receives (T-12, D6' §4.2).
/// </summary>
/// <param name="Type">The event name, matching the outbox event that carries the same fact
/// durably — <c>tracker.bound</c>, <c>tracker.unbound</c>, <c>tracker.revoked</c>,
/// <c>tracker.quarantined</c>.</param>
/// <param name="Serials">Credential serials this message invalidates. The adapter matches an open
/// socket by IMEI; the broker and anything holding a certificate match by serial.</param>
public sealed record TrackerCredentialSignal(
    string Type, string Imei, Guid VehicleId, IReadOnlyList<string> Serials, string? Reason, DateTimeOffset At);

/// <summary>
/// The Redis half of the tracker plane: the <c>imei:{imei}</c> lookup (T-03) and the pub/sub
/// invalidation that makes revocation sub-second (T-12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every operation is best effort.</b> <c>prov.tracker_bindings</c> is the source of truth, so
/// a Redis outage costs latency on the validate path and the fast half of the revocation signal —
/// it does not cost correctness, and it must never turn a bind into a 500. The durable half is
/// the <c>provisioning.events</c> outbox row, written in the same transaction as the state change.
/// </para>
/// <para>
/// <b>Present means ACTIVE.</b> There is no cached "revoked" value: a reader that missed the cache
/// and a reader that found a revoked entry have to reach the same conclusion, and the only way to
/// guarantee that is to have one representation of "not usable" — absence.
/// </para>
/// </remarks>
public interface ITrackerCache
{
    /// <summary>The cached vehicle for an IMEI, or <see langword="null"/> on a miss or an outage.</summary>
    Task<Guid?> ResolveAsync(string imei, CancellationToken cancellationToken);

    /// <summary>Primes <c>imei:{imei}</c> for an ACTIVE binding.</summary>
    Task PrimeAsync(string imei, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Drops <c>imei:{imei}</c>. Called for every non-ACTIVE outcome.</summary>
    Task InvalidateAsync(string imei, CancellationToken cancellationToken);

    /// <summary>Publishes the fast revocation/invalidation signal both transports read.</summary>
    Task PublishAsync(TrackerCredentialSignal signal, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackerCache"/>
public sealed class TrackerCache(
    IConnectionMultiplexer redis,
    IOptions<ProvisioningOptions> options,
    ILogger<TrackerCache> logger) : ITrackerCache
{
    private readonly ProvisioningOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<Guid?> ResolveAsync(string imei, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.Imei(imei));

            return value.IsNullOrEmpty || !Guid.TryParse(value.ToString(), out var vehicleId) ? null : vehicleId;
        }
        catch (RedisException exception)
        {
            // A miss and an outage are the same answer to the caller — go and ask Postgres.
            logger.LogWarning(exception, "IMEI cache read failed for {Imei}; falling back to Postgres", imei);
            return null;
        }
    }

    public async Task PrimeAsync(string imei, Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.Imei(imei), vehicleId.ToString(), _options.ImeiCacheTtl);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "IMEI cache prime failed for {Imei}; the binding is committed regardless", imei);
        }
    }

    public async Task InvalidateAsync(string imei, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKeys.Imei(imei));
        }
        catch (RedisException exception)
        {
            // Loud: this is the fast half of T-12 failing, and what is left is the cache's own
            // 24 h TTL. The durable outbox event has still been committed.
            logger.LogError(
                exception,
                "IMEI cache invalidation failed for {Imei}. The entry now expires on its {Ttl} TTL rather than " +
                "within the T-12 budget; consumers of provisioning.events are unaffected.",
                imei,
                _options.ImeiCacheTtl);
        }
    }

    public async Task PublishAsync(TrackerCredentialSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(RedisKeys.TrackerCredentialChannel),
                JsonSerializer.Serialize(signal, MageRideJson.Options));
        }
        catch (RedisException exception)
        {
            logger.LogError(
                exception,
                "Could not publish {Type} for {Imei} on {Channel}. Subscribers fall back to the 5-minute " +
                "revalidation the adapter does on long sockets (T-01).",
                signal.Type,
                signal.Imei,
                RedisKeys.TrackerCredentialChannel);
        }
    }
}
