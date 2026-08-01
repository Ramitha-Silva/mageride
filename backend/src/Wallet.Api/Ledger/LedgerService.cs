using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Wallet.Caching;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Events;
using MageRide.Wallet.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MageRide.Wallet.Ledger;

/// <summary>One leg of an entry: a signed amount against one account.</summary>
internal sealed record LedgerLeg(Guid AccountId, long AmountMinor, string? Reference = null);

/// <summary>A balanced entry, as a caller asks for it.</summary>
/// <param name="Kind">A <c>billing.journal_entries.kind</c> (see <see cref="Domain.JournalKinds"/>).</param>
/// <param name="IdempotencyKey">
/// Composed from the business fact, never random — the column is UNIQUE and it is what makes a retry
/// a no-op instead of a second movement of money.
/// </param>
internal sealed record LedgerEntry(
    string Kind,
    string IdempotencyKey,
    string? Description,
    IReadOnlyList<LedgerLeg> Legs);

/// <summary>What one account's leg did.</summary>
internal sealed record PostedLeg(Guid AccountId, Guid? OwnerId, long AmountMinor, long BalanceAfterMinor);

/// <summary>The result of posting — or of finding that it had already been posted.</summary>
/// <param name="Replayed">
/// <see langword="true"/> when the idempotency key was already used: nothing was written and the
/// balances are the ones the first attempt left behind.
/// </param>
internal sealed record LedgerResult(Guid EntryId, bool Replayed, IReadOnlyList<PostedLeg> Legs)
{
    /// <summary>The leg for one account, or <see langword="null"/> if the entry did not touch it.</summary>
    public PostedLeg? For(Guid accountId) => Legs.FirstOrDefault(leg => leg.AccountId == accountId);
}

/// <summary>
/// The only thing on this platform that writes <c>billing.journal_postings</c>.
/// </summary>
internal interface ILedgerService
{
    /// <summary>
    /// Posts a balanced entry, or returns the entry an earlier attempt with the same key wrote.
    /// </summary>
    /// <remarks>
    /// Runs inside its own transaction. <paramref name="beforeCommit"/> lets a caller write its own row
    /// — a top-up settlement, a voucher purchase, a transfer approval — inside the same transaction as
    /// the postings, which is what keeps the domain row and the money it describes atomic.
    /// </remarks>
    Task<LedgerResult> PostAsync(
        LedgerEntry entry,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILedgerService"/>
/// <remarks>
/// <para>
/// <b>Every money movement in wallet-svc is this method.</b> Top-up settlement, voucher purchase,
/// credit transfer, and both internal routes: they differ in which legs they build and in what row
/// they write beside them, and in nothing else. That is deliberate — D-09's balanced-entry invariant,
/// the read-model projections, the outbox row and the D-08 cache write-through are five things that
/// have to happen together, and five call sites would eventually be four.
/// </para>
/// <para>
/// <b>Σ legs = 0 is checked here as well as by <c>trg_balanced</c>.</b> The trigger is the guarantee —
/// it binds a psql session and every future service — but it fires at COMMIT and surfaces as a 500.
/// Checking first turns a caller's arithmetic bug into a diagnosable failure instead of an
/// "internal-error" on somebody's wallet.
/// </para>
/// <para>
/// <b>Accounts are locked in id order.</b> A driver-to-driver transfer touches two wallets, and two
/// simultaneous transfers between the same pair in opposite directions would deadlock if each locked
/// its own sender first. Ordering the <c>SELECT … FOR UPDATE</c> by account id gives every transaction
/// the same lock order, which is what makes a deadlock impossible rather than rare.
/// </para>
/// <para>
/// <b>A driver's wallet may not go negative.</b> §10 leaves that to the application — "driver
/// non-negativity in app" — and nothing else enforces it, so it is enforced here, as
/// <c>402 insufficient-wallet</c>. The platform and suspense accounts are exempt: the platform side of
/// every credit is negative by construction, which is what double entry means.
/// </para>
/// <para>
/// <b>The cache write-through happens after COMMIT, and never before.</b> Writing
/// <c>wallet:bal:{driverId}</c> inside the transaction would publish a balance that a rollback then
/// un-did, and dispatch-svc would gate a second trip on money that does not exist for up to the 5 s
/// TTL. After COMMIT the worst case is a write that fails, which the TTL cleans up.
/// </para>
/// </remarks>
internal sealed class LedgerService(
    INpgsqlConnectionFactory connections,
    ILedgerRepository ledger,
    IAccountRepository accounts,
    IWalletBalanceCache cache,
    IOptions<WalletOptions> options,
    TimeProvider clock,
    ILogger<LedgerService> logger) : ILedgerService
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<LedgerResult> PostAsync(
        LedgerEntry entry,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task>? beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The claim. `ON CONFLICT (idempotency_key) DO NOTHING RETURNING id` is the whole idempotency
        // mechanism for money on this platform: the loser of a race gets no row back and reads what
        // the winner wrote, so a retried top-up callback or a re-posted daily fee moves nothing twice.
        var entryId = await ledger.TryCreateEntryAsync(
            connection, transaction, entry.Kind, entry.IdempotencyKey, entry.Description, cancellationToken);

        if (entryId is null)
        {
            await transaction.RollbackAsync(cancellationToken);

            return await ReadReplayAsync(entry, cancellationToken);
        }

        var accountIds = entry.Legs.Select(leg => leg.AccountId).Distinct().Order().ToArray();
        var locked = await accounts.LockAsync(connection, transaction, accountIds, cancellationToken);

        var posted = new List<PostedLeg>(entry.Legs.Count);

        foreach (var group in entry.Legs.GroupBy(leg => leg.AccountId))
        {
            var accountId = group.Key;
            var delta = group.Sum(leg => leg.AmountMinor);

            if (!locked.TryGetValue(accountId, out var account))
            {
                throw new MageRideException(
                    MageRideErrors.NotFound, $"Ledger account {accountId} does not exist.");
            }

            var balanceAfter = account.BalanceMinor + delta;

            if (balanceAfter < 0 && account.IsWalletOwner)
            {
                throw new MageRideException(
                    MageRideErrors.InsufficientWallet,
                    $"This would take the wallet to {balanceAfter} minor units. The balance is "
                    + $"{account.BalanceMinor} and the movement is {delta}.");
            }

            foreach (var leg in group)
            {
                await ledger.AddPostingAsync(
                    connection, transaction, entryId.Value, accountId, leg.AmountMinor, cancellationToken);
            }

            await accounts.SetBalanceAsync(
                connection, transaction, accountId, balanceAfter, cancellationToken);

            // The projections. Only a wallet owner gets a history line: the platform and suspense
            // accounts have no wallet screen, and `billing.wallets` is keyed by an account that has one.
            if (account.IsWalletOwner)
            {
                await accounts.MirrorWalletAsync(
                    connection, transaction, accountId, balanceAfter, cancellationToken);

                await ledger.AddTransactionAsync(
                    connection,
                    transaction,
                    accountId,
                    entryId.Value,
                    entry.Kind,
                    delta,
                    balanceAfter,
                    group.Select(leg => leg.Reference).FirstOrDefault(reference => reference is not null)
                        ?? entry.Description,
                    cancellationToken);
            }

            posted.Add(new PostedLeg(accountId, account.OwnerId, delta, balanceAfter));
        }

        if (beforeCommit is not null)
        {
            await beforeCommit(connection, transaction, entryId.Value, cancellationToken);
        }

        // R-13: the events commit with the postings they describe. A `wallet.credited` that was
        // published without its money would make dispatch-svc cache a balance nobody has; one that
        // committed without being published would leave the cache stale until its 5 s TTL, which is
        // the failure D-08's invalidation exists to avoid.
        await WriteEventsAsync(connection, transaction, entry, posted, locked, cancellationToken);

        // The deferred trg_balanced fires here. Σ legs = 0 was already checked, so a violation at this
        // point is a bug in this method rather than in the request.
        await transaction.CommitAsync(cancellationToken);

        await WriteThroughCacheAsync(posted, locked);

        return new LedgerResult(entryId.Value, Replayed: false, posted);
    }

    /// <summary>
    /// Reads back the entry an earlier attempt with this key wrote.
    /// </summary>
    /// <remarks>
    /// The postings are the source: the read model could in principle lag, and what a replayed caller
    /// needs is the balance their money actually left behind. Not finding the entry at all would mean
    /// the row vanished between the conflict and this read, which nothing in this schema can do —
    /// entries are never deleted.
    /// </remarks>
    private async Task<LedgerResult> ReadReplayAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var existing = await ledger.ReadEntryByKeyAsync(connection, entry.IdempotencyKey, cancellationToken)
                       ?? throw new MageRideException(
                           MageRideErrors.Conflict,
                           $"Idempotency key '{entry.IdempotencyKey}' is in use but its entry could not be read.");

        logger.LogInformation(
            "Ledger entry {EntryId} ({Kind}) replayed for idempotency key {Key}; nothing was posted.",
            existing.EntryId,
            existing.Kind,
            entry.IdempotencyKey);

        return new LedgerResult(existing.EntryId, Replayed: true, existing.Legs);
    }

    /// <summary>
    /// Queues one <c>wallet.debited</c> / <c>wallet.credited</c> per wallet-owning leg, plus the
    /// US-9.9 low-balance edge.
    /// </summary>
    /// <remarks>
    /// <b>The low-balance event is edge-triggered</b>, on the crossing rather than on the state: the
    /// balance before the posting is known here, so a driver who is already below the threshold and
    /// spends again is not notified twice. Level-triggered, every debit of a low wallet would be a
    /// push, and US-9.9's warning would be the noise a driver mutes. The same choice C044 made for the
    /// fleet-outage alert.
    /// </remarks>
    private async Task WriteEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LedgerEntry entry,
        IReadOnlyList<PostedLeg> posted,
        IReadOnlyDictionary<Guid, LedgerAccount> locked,
        CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();

        foreach (var leg in posted)
        {
            if (leg.OwnerId is not { } ownerId || !locked[leg.AccountId].IsWalletOwner)
            {
                continue;
            }

            var eventType = leg.AmountMinor < 0 ? WalletEventTypes.Debited : WalletEventTypes.Credited;

            await ledger.AddOutboxAsync(
                connection,
                transaction,
                ownerId,
                eventType,
                WalletEvents.Movement(
                    ownerId, leg.AccountId, entry.Kind, leg.AmountMinor, leg.BalanceAfterMinor, at),
                cancellationToken);

            var before = locked[leg.AccountId].BalanceMinor;
            var threshold = _options.LowBalanceThresholdMinor;

            // Drivers only (Δ C060). US-9.9 is a driver's warning — "top up before your next trip" —
            // and D5' §9.4's second clause is about going online. A fleet's balance is spent once a
            // month against an invoice it can already see, its dunning is fleet-billing-svc's
            // OVERDUE signal, and a LOW_BALANCE push at an organisation would resolve to no
            // recipient and mean the wrong thing if it did.
            if (locked[leg.AccountId].OwnerType == AccountOwnerTypes.Driver
                && leg.BalanceAfterMinor < threshold
                && before >= threshold)
            {
                await ledger.AddOutboxAsync(
                    connection,
                    transaction,
                    ownerId,
                    WalletEventTypes.LowBalance,
                    WalletEvents.LowBalance(ownerId, leg.BalanceAfterMinor, threshold, at),
                    cancellationToken);
            }
        }
    }

    /// <summary>Write-through of <c>wallet:bal:{driverId}</c> (D-08), after COMMIT and best effort.</summary>
    private async Task WriteThroughCacheAsync(
        IReadOnlyList<PostedLeg> posted, IReadOnlyDictionary<Guid, LedgerAccount> locked)
    {
        foreach (var leg in posted)
        {
            if (leg.OwnerId is { } ownerId && locked[leg.AccountId].OwnerType == AccountOwnerTypes.Driver)
            {
                await cache.WriteAsync(ownerId, leg.BalanceAfterMinor);
            }
        }
    }

    private static void Validate(LedgerEntry entry)
    {
        if (entry.Legs.Count < 2)
        {
            throw new InvalidOperationException(
                $"A journal entry needs at least two legs; '{entry.Kind}' was given {entry.Legs.Count}.");
        }

        if (entry.Legs.Sum(leg => leg.AmountMinor) != 0)
        {
            throw new InvalidOperationException(
                $"Journal entry '{entry.IdempotencyKey}' ({entry.Kind}) does not balance: the legs sum to "
                + $"{entry.Legs.Sum(leg => leg.AmountMinor)}, not 0 (D-09).");
        }

        if (entry.Legs.Any(leg => leg.AmountMinor == 0))
        {
            throw new InvalidOperationException(
                $"Journal entry '{entry.IdempotencyKey}' ({entry.Kind}) has a zero leg, which records a "
                + "movement that did not happen.");
        }
    }
}
