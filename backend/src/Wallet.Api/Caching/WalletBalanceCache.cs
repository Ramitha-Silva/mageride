using System.Globalization;
using MageRide.Shared.Caching;
using MageRide.Wallet.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Wallet.Caching;

/// <summary>
/// The write side of <c>wallet:bal:{driverId}</c> — D-08's pre-dispatch balance cache.
/// </summary>
/// <remarks>
/// dispatch-svc reads this key for every candidate before offering a second trip of the day and
/// populates it from <c>billing.wallets</c> on a miss (<c>WalletGate</c>, C034). This service is the
/// other half: the writer that keeps it honest when the balance changes.
/// </remarks>
internal interface IWalletBalanceCache
{
    /// <summary>Publishes a driver's new balance. Best effort; never throws.</summary>
    Task WriteAsync(Guid driverId, long balanceMinor);
}

/// <inheritdoc cref="IWalletBalanceCache"/>
/// <remarks>
/// <para>
/// <b>Write-through, not delete.</b> D5' §9.2 says the cache is "debit-invalidated
/// (<c>wallet.debited</c> event clears)", and writing the new balance satisfies that more strongly
/// than a delete does: the stale value is gone either way, and dispatch-svc's next gate read is
/// answered without a database round trip. C034's own test says so from the other side — "wallet-svc
/// writes the second on <c>wallet.credited</c>, ahead of its own projection catching up".
/// </para>
/// <para>
/// <b>After COMMIT, always.</b> Writing inside the transaction would publish a balance a rollback
/// then un-did, and dispatch would gate on money that does not exist for up to the TTL. Called from
/// <c>LedgerService</c> after the commit for that reason.
/// </para>
/// <para>
/// <b>A failure is logged and swallowed, and the fallback is a delete.</b> The money is already
/// committed; refusing the request now would tell the driver their top-up failed when it did not. If
/// the write fails, the key is deleted instead — a miss sends dispatch to the read model, which this
/// service updated in the same transaction. If that fails too, the 5 s TTL is what bounds it.
/// </para>
/// </remarks>
internal sealed class WalletBalanceCache(
    IConnectionMultiplexer redis,
    IOptions<WalletOptions> options,
    ILogger<WalletBalanceCache> logger) : IWalletBalanceCache
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task WriteAsync(Guid driverId, long balanceMinor)
    {
        if (!_options.BalanceCacheEnabled)
        {
            return;
        }

        var key = RedisKeys.WalletBalance(driverId);

        try
        {
            await redis.GetDatabase().StringSetAsync(
                key,
                balanceMinor.ToString(CultureInfo.InvariantCulture),
                _options.BalanceCacheTtl);
        }
        catch (RedisException exception)
        {
            logger.LogWarning(
                exception,
                "Could not write wallet:bal for {DriverId}; dropping the key instead so the D-08 gate "
                + "falls through to billing.wallets.",
                driverId);

            try
            {
                await redis.GetDatabase().KeyDeleteAsync(key);
            }
            catch (RedisException)
            {
                // Both failed. The 5 s TTL is the backstop: a stale-high balance costs at most one
                // offer to a driver who cannot pay the daily fee, and dispatch charges it before the
                // trip rather than after.
                logger.LogError(
                    "wallet:bal for {DriverId} could be neither written nor dropped; it is stale for up "
                    + "to {Ttl}.",
                    driverId,
                    _options.BalanceCacheTtl);
            }
        }
    }
}
