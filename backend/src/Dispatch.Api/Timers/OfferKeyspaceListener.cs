using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Caching;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Dispatch.Timers;

/// <summary>
/// D-07's other half: react to <c>offer:{rideId}</c> expiring in Redis instead of waiting for the
/// next durable sweep.
/// </summary>
/// <remarks>
/// <para>
/// ADD's D-07 row resolves "15 s offer TTL mechanism not stated" with "Redis key with
/// <c>PEXPIRE</c> + <b>keyspace-notification reassignment</b>", and ADD §6 repeats it for
/// dispatch-svc. This is that reassignment — and it is <b>an accelerator, nothing more</b>. The
/// guarantee is <c>rides.timers</c> (R-04): every path this listener takes is one
/// <see cref="OfferExpiryWorker"/> would take a few hundred milliseconds later, and the expiry
/// itself is idempotent, so the two racing is a no-op rather than a double fire.
/// </para>
/// <para>
/// Keyspace notifications are off by default in Redis. When
/// <c>Dispatch:ConfigureKeyspaceNotifications</c> is on, the existing flags are read and the
/// generic + expired classes are added to them rather than replaced — overwriting the setting
/// would silently switch off whatever another service on the same instance subscribed to. A
/// managed Redis that refuses <c>CONFIG SET</c> logs and carries on: the durable backstop is
/// unaffected.
/// </para>
/// </remarks>
public sealed class OfferKeyspaceListener(
    IConnectionMultiplexer redis,
    IServiceProvider services,
    IOptions<RedisOptions> redisOptions,
    IOptions<DispatchOptions> options,
    ILogger<OfferKeyspaceListener> logger) : IHostedService
{
    /// <summary>The two classes needed: <c>g</c>eneric commands and key<c>e</c>vent + <c>x</c>pired.</summary>
    private const string RequiredFlags = "Ex";

    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly RedisOptions _redis = redisOptions?.Value ?? throw new ArgumentNullException(nameof(redisOptions));

    /// <summary>The channel subscribed to, so shutdown releases exactly it and nothing else.</summary>
    private RedisChannel Channel =>
        new($"__keyevent@{_redis.Database}__:expired", RedisChannel.PatternMode.Literal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ConfigureKeyspaceNotifications)
        {
            await TryEnableNotificationsAsync();
        }

        await redis.GetSubscriber().SubscribeAsync(Channel, (_, key) => OnExpired(key.ToString()));

        logger.LogInformation("Subscribed to {Channel} for offer:{{rideId}} expiries (D-07)", Channel);
    }

    // Not UnsubscribeAllAsync: the multiplexer is shared, and C024's SignalR backplane will be on
    // it. Releasing one channel is the difference between shutting this listener down and shutting
    // every subscriber in the process down.
    public Task StopAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().UnsubscribeAsync(Channel);

    /// <summary>
    /// Parses <c>offer:{rideId}</c>. Every other expiring key on the instance flows through this
    /// callback too, so the shape check is the filter.
    /// </summary>
    internal static Guid? RideIdFromOfferKey(string? key)
    {
        const string prefix = "offer:";

        if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Guid.TryParse(key[prefix.Length..], out var rideId) ? rideId : null;
    }

    private void OnExpired(string? key)
    {
        if (RideIdFromOfferKey(key) is not { } rideId)
        {
            return;
        }

        // Fire and forget: this runs on StackExchange.Redis's subscription callback, and blocking
        // it would stall every other subscriber on the multiplexer. Nothing is lost if the task is
        // dropped — the durable sweep is still armed for the same offer.
        _ = Task.Run(async () =>
        {
            try
            {
                await ExpireAsync(rideId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Keyspace-driven expiry for ride {RideId} failed; the durable backstop still has it", rideId);
            }
        });
    }

    private async Task ExpireAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
        var offers = scope.ServiceProvider.GetRequiredService<IOfferRepository>();
        var timers = scope.ServiceProvider.GetRequiredService<IOfferTimerRepository>();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        Domain.DueOfferTimer? claimed;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            var live = await offers.FindLiveForRideAsync(connection, null, rideId, cancellationToken);

            if (live is null)
            {
                // The offer key expired after the driver already answered. Nothing to accelerate.
                return;
            }

            // Take the lease on this offer's own timer, not on "whatever is due": the Redis key
            // expires at the deadline and the durable row is armed a grace period after it, so a
            // due-only claim would miss it by exactly the interval this path exists to save.
            // Going through the lease is also what keeps this listener and a concurrent sweep from
            // both firing, and keeps the two paths on one implementation.
            claimed = await timers.TryClaimForOfferAsync(
                connection, null, live.Id, _options.TimerLease, cancellationToken);
        }

        if (claimed is not null)
        {
            // D-07 is an accelerator for R-04, so it takes R-04's path: the Redis key expiring is a
            // hint that the deadline has passed, and ride-svc's own predicate is what confirms it.
            await dispatch.ExpireAsync(claimed, driverUnreachable: false, cancellationToken);
        }
    }

    private async Task TryEnableNotificationsAsync()
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            try
            {
                var server = redis.GetServer(endpoint);

                if (server.IsReplica)
                {
                    continue;
                }

                var current = (await server.ConfigGetAsync("notify-keyspace-events"))
                    .Select(static entry => entry.Value)
                    .FirstOrDefault() ?? string.Empty;

                var merged = new string([.. current.Concat(RequiredFlags).Distinct().Order()]);

                if (merged != new string([.. current.Distinct().Order()]))
                {
                    await server.ConfigSetAsync("notify-keyspace-events", merged);
                    logger.LogInformation(
                        "Enabled Redis keyspace expiry notifications on {Endpoint}: '{Before}' → '{After}'",
                        endpoint, current, merged);
                }
            }
            catch (RedisException ex)
            {
                // Managed Redis usually refuses CONFIG SET. Not an error — D-07's key expiry is a
                // fast hint and R-04's rides.timers row is the guarantee.
                logger.LogInformation(
                    ex,
                    "Could not enable keyspace notifications on {Endpoint}; offer expiry runs on the durable " +
                    "rides.timers backstop alone (R-04)",
                    endpoint);
            }
        }
    }
}
