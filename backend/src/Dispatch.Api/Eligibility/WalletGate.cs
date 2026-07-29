using System.Globalization;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace MageRide.Dispatch.Eligibility;

/// <summary>What the D-08 wallet gate decided about one candidate, and why.</summary>
/// <param name="Allowed">Whether this driver may be offered the ride.</param>
/// <param name="Reason">
/// A stable token for the audit row: <c>first_trip</c> · <c>already_charged</c> ·
/// <c>sufficient</c> · <c>insufficient</c> · <c>unconfirmable</c> · <c>no_plan</c> ·
/// <c>gate_disabled</c>.
/// </param>
/// <param name="BalanceMinor">
/// What the gate read, or <see langword="null"/> when it could not establish one — D-08's
/// "until balance confirmable".
/// </param>
public sealed record WalletVerdict(bool Allowed, string Reason, long? BalanceMinor, int? DailyFeeMinor)
{
    public static readonly WalletVerdict NotChecked = new(true, "gate_disabled", null, null);
}

/// <summary>
/// D5' §2.1 / §9.2's pre-dispatch wallet gate: the first trip of the Colombo day is free, the
/// second onwards needs a wallet that can cover the daily platform fee (D-08, US-9.1).
/// </summary>
public interface IWalletGate
{
    /// <summary>Evaluates a whole round's candidates.</summary>
    Task<IReadOnlyDictionary<Guid, WalletVerdict>> EvaluateAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWalletGate"/>
/// <remarks>
/// <para>
/// <b>The cache is read first and written through</b> — <c>wallet:bal:{driverId}</c>, TTL 5 s
/// (D5' §9.2). It sits in front of <c>billing.wallets</c>, which is itself a read-model mirror of
/// the ledger (§10): three layers, one master, and the master is never consulted on this path.
/// Invalidation on <c>wallet.debited</c> is wallet-svc's (C046); the 5 s TTL is what bounds the
/// staleness until then, and it is short enough that a driver who tops up is dispatchable within
/// one offer window.
/// </para>
/// <para>
/// <b>The degraded-mode rule is D-08 verbatim.</b> "If cache miss AND Postgres unreachable → allow
/// first trip (free anyway), refuse 2nd-trip charge until balance confirmable (fail-safe, never
/// double-charge)." Which trip it is comes from <c>dispatch.offers</c> — this service's own table,
/// on the same connection the candidate query already used — so the first-trip half of the rule
/// survives a billing outage, and only the balance half is ever unconfirmable. A driver whose
/// balance cannot be established on their second trip is refused rather than waved through: the
/// alternative is a fleet running the day on credit nobody agreed to.
/// </para>
/// <para>
/// <b>A tier with no <c>billing.plans</c> row is refused from the second trip too.</b> Migration
/// 1901 leaves <c>truck</c> and <c>mini_truck</c> deliberately unseeded — "no default row, so a
/// delivery vehicle cannot go online until Finance sets one" — and inventing a rate here would
/// quietly overrule that.
/// </para>
/// </remarks>
public sealed class WalletGate(
    IDailyFeeRepository dailyFee,
    IConnectionMultiplexer redis,
    IOptions<DispatchOptions> options,
    ILogger<WalletGate> logger) : IWalletGate
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyDictionary<Guid, WalletVerdict>> EvaluateAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var verdicts = new Dictionary<Guid, WalletVerdict>(candidates.Count);

        if (!_options.WalletGateEnabled)
        {
            foreach (var (driverId, _, _) in candidates)
            {
                verdicts[driverId] = WalletVerdict.NotChecked;
            }

            return verdicts;
        }

        var cached = await ReadCacheAsync(candidates, cancellationToken);
        var facts = await ReadFactsAsync(connection, candidates, cancellationToken);

        foreach (var (driverId, _, _) in candidates)
        {
            facts.TryGetValue(driverId, out var fact);
            cached.TryGetValue(driverId, out var cachedBalance);

            verdicts[driverId] = Decide(fact, cachedBalance);
        }

        await WriteBackAsync(facts, cached, cancellationToken);

        return verdicts;
    }

    /// <summary>D5' §2.2's charge logic, evaluated as a gate rather than as a charge.</summary>
    private static WalletVerdict Decide(DailyFeeFacts? fact, long? cachedBalance)
    {
        // No row at all means the billing read failed — the candidate query that produced this
        // driver came off the same connection, so "the driver does not exist" is not a possibility
        // here. D-08: allow the first trip, refuse the rest until the balance is confirmable.
        if (fact is null)
        {
            return new WalletVerdict(false, "unconfirmable", cachedBalance, null);
        }

        if (fact.TripsToday == 0)
        {
            // US-9.1 / D-13: no wallet check at all on the first trip. Not "checked and passed" —
            // the balance is genuinely not consulted, which is why the reason says so.
            return new WalletVerdict(true, "first_trip", cachedBalance ?? fact.BalanceMinor, fact.DailyFeeMinor);
        }

        if (fact.ChargedToday)
        {
            // "Single flat charge regardless of trip count" (US-9.4). Trips 3..N of a day the
            // driver has already paid for need no balance either.
            return new WalletVerdict(true, "already_charged", cachedBalance ?? fact.BalanceMinor, fact.DailyFeeMinor);
        }

        if (fact.DailyFeeMinor is not { } fee)
        {
            return new WalletVerdict(false, "no_plan", cachedBalance ?? fact.BalanceMinor, null);
        }

        // The cache is authoritative for the *reading* when it has one — that is what the 5 s TTL
        // and the debit invalidation are for; the read model is the fallback.
        var balance = cachedBalance ?? fact.BalanceMinor;

        return new WalletVerdict(
            balance >= fee, balance >= fee ? "sufficient" : "insufficient", balance, fee);
    }

    private async Task<Dictionary<Guid, long>> ReadCacheAsync(
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken)
    {
        var balances = new Dictionary<Guid, long>(candidates.Count);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var db = redis.GetDatabase();
            var keys = candidates.Select(c => (RedisKey)RedisKeys.WalletBalance(c.DriverId)).ToArray();
            var values = await db.StringGetAsync(keys);

            for (var i = 0; i < candidates.Count; i++)
            {
                if (values[i].HasValue &&
                    long.TryParse(values[i].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var balance))
                {
                    balances[candidates[i].DriverId] = balance;
                }
            }
        }
        catch (RedisException exception)
        {
            // A cache miss by another name. The database read below is what answers; D-08's
            // degraded branch is only reached when *both* are unavailable.
            logger.LogWarning(exception, "wallet:bal cache unreadable; falling through to billing.wallets");
        }

        return balances;
    }

    private async Task<IReadOnlyDictionary<Guid, DailyFeeFacts>> ReadFactsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            return await dailyFee.ReadAsync(connection, candidates, cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            // D-08's "Postgres unreachable". Not rethrown: the whole round must still produce an
            // answer, and Decide() turns the missing facts into the fail-safe verdict.
            logger.LogError(
                exception,
                "billing read failed for {Count} candidates; the D-08 degraded rule applies — " +
                "second and later trips are refused until a balance is confirmable",
                candidates.Count);

            return new Dictionary<Guid, DailyFeeFacts>();
        }
    }

    /// <summary>Populates <c>wallet:bal:{driverId}</c> for the drivers the cache did not answer for.</summary>
    private async Task WriteBackAsync(
        IReadOnlyDictionary<Guid, DailyFeeFacts> facts,
        Dictionary<Guid, long> cached,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var misses = facts.Values.Where(fact => !cached.ContainsKey(fact.DriverId)).ToArray();

        if (misses.Length == 0)
        {
            return;
        }

        try
        {
            var db = redis.GetDatabase();

            // Fire-and-forget: this is a cache warm-up on the hot path, and the round's decision
            // has already been taken from the value being written. A failed SET costs the next
            // round one database read.
            foreach (var fact in misses)
            {
                await db.StringSetAsync(
                    RedisKeys.WalletBalance(fact.DriverId),
                    fact.BalanceMinor.ToString(CultureInfo.InvariantCulture),
                    _options.WalletCacheTtl,
                    flags: CommandFlags.FireAndForget);
            }
        }
        catch (RedisException exception)
        {
            logger.LogDebug(exception, "Could not populate the wallet:bal cache; the next round re-reads it");
        }
    }
}
