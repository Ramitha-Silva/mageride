using MageRide.Shared.Errors;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Domain;
using MageRide.Wallet.Gateways;
using MageRide.Wallet.Ledger;
using MageRide.Wallet.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Wallet.Money;

/// <summary>A provider callback, already signature-verified and parsed.</summary>
internal sealed record TopupCallback(
    string ProviderTransactionId, Guid? TopupId, string? OrderId, string Status, long? AmountMinor);

/// <summary>What a callback did.</summary>
/// <param name="Credited">False for a redelivery, a failure or a still-pending notice.</param>
internal sealed record TopupSettlement(TopupRow Topup, bool Credited, bool Replayed);

/// <summary>
/// In-app top-up: OnePay's card/wallet rail and AL-15's LankaQR bank-app hand-off.
/// </summary>
/// <remarks>
/// <b>Bank transfer is not here and cannot be added by configuration.</b> AL-05 removed it as a top-up
/// method; <c>ck_topups_method</c> (1107) admits <c>onepay</c> and <c>lankaqr</c>, so a receipt flow or a
/// manual reconciliation queue would need a migration, a contract change and a spec change before it
/// could store its first row.
/// </remarks>
internal sealed class TopupService(
    ITopupRepository topups,
    IAccountRepository accounts,
    ILedgerService ledger,
    IEnumerable<IPaymentGateway> gateways,
    IOptions<WalletOptions> options,
    TimeProvider clock,
    ILogger<TopupService> logger)
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Opens a gateway session for one driver.</summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is credited here</b>, and the row is created before the gateway is called: a session
    /// the gateway accepted and this service failed to record would be a payment with nowhere to land.
    /// If the gateway refuses, the row is marked <c>Failed</c> rather than left <c>Pending</c> for the
    /// reconciler to puzzle over.
    /// </para>
    /// <para>
    /// <b>The session artefacts are returned and never stored.</b> A redirect URL, a session token and a
    /// QR payload are single-use, seconds-lived instruments of one gateway session; keeping them would
    /// put a payment instrument in a table that <c>GET /v1/wallet/topup/{topupId}</c> reads back long
    /// after it stopped working. A client that lost the response starts a new top-up and this one expires
    /// unpaid.
    /// </para>
    /// </remarks>
    public async Task<(TopupRow Topup, GatewaySession Session)> StartAsync(
        Guid driverId, string method, long amountMinor, string? returnUrl, CancellationToken cancellationToken)
    {
        RequireAmountInRange(amountMinor);

        var gateway = gateways.FirstOrDefault(candidate => candidate.Method == method)
                      ?? throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                      {
                          ["method"] = [$"'{method}' is not a top-up method. AL-05 leaves onepay and lankaqr."],
                      });

        var account = await accounts.EnsureDriverAccountAsync(driverId, cancellationToken);

        // Our own reference, minted before the gateway is told anything, so a callback that echoes only
        // `orderId` can still find this session (D6' §7.1).
        var orderId = $"mr-topup-{Guid.NewGuid():N}";

        var topup = await topups.CreateAsync(
            driverId, account.Id, method, amountMinor, orderId, cancellationToken);

        try
        {
            var session = await gateway.StartAsync(
                topup.Id, orderId, amountMinor, returnUrl, cancellationToken);

            return (topup, session);
        }
        catch (MageRideException)
        {
            await topups.TryFailAsync(
                topup.Id, null, "gateway session could not be opened", clock.GetUtcNow(), cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Applies a provider callback: credits the wallet on success, exactly once (R-19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two idempotency guards, and they answer different questions.</b>
    /// <c>ux_topups_provider_txn</c> catches a redelivery of the same gateway transaction — the R-19
    /// rule, and the first thing checked. The ledger's own <c>topup:{topupId}</c> key catches two
    /// *different* callbacks arriving for one session, which a provider retrying under a new
    /// transaction id would produce.
    /// </para>
    /// <para>
    /// <b>An amount that disagrees with the session is refused, not credited.</b> Crediting what the
    /// callback says would let a spoofed or misconfigured provider set the balance; crediting what the
    /// session says would credit money the driver may not have paid. Both are wrong, so the session
    /// stays <c>Pending</c> and the mismatch is logged as the settlement exception D6' §7.2 routes to
    /// the Finance queue.
    /// </para>
    /// </remarks>
    public async Task<TopupSettlement> SettleAsync(
        string method, TopupCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (string.IsNullOrWhiteSpace(callback.ProviderTransactionId))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["providerTransactionId"] = ["providerTransactionId is required — it is the R-19 dedupe key."],
            });
        }

        var already = await topups.ReadByProviderTransactionAsync(
            callback.ProviderTransactionId, cancellationToken);

        if (already is not null)
        {
            logger.LogInformation(
                "Top-up callback for {ProviderTransactionId} is a redelivery of top-up {TopupId} ({State}); "
                + "nothing was credited.",
                callback.ProviderTransactionId,
                already.Id,
                already.State);

            return new TopupSettlement(already, Credited: false, Replayed: true);
        }

        var topup = await topups.ResolveAsync(callback.TopupId, callback.OrderId, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.NotFound,
                        "No top-up session matches this callback's topupId or orderId.");

        if (topup.Method != method)
        {
            // A OnePay callback confirming a LankaQR session, or the reverse. Refused: the two rails
            // have different secrets, and honouring it would let either secret settle the other's money.
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Top-up {topup.Id} was started on '{topup.Method}' and this callback arrived on '{method}'.");
        }

        if (!string.Equals(callback.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(callback.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                await topups.TryFailAsync(
                    topup.Id,
                    callback.ProviderTransactionId,
                    "gateway reported FAILED",
                    clock.GetUtcNow(),
                    cancellationToken);
            }

            // PENDING is a progress notice, not an outcome: the session stays open and the next
            // callback decides it.
            return new TopupSettlement(topup, Credited: false, Replayed: false);
        }

        if (callback.AmountMinor is { } reported && reported != topup.AmountMinor)
        {
            logger.LogError(
                "Top-up {TopupId} was opened for {Expected} and the gateway confirmed {Reported}. The "
                + "session is left Pending: this is a settlement exception for Finance (D6' §7.2), not "
                + "something to credit in either direction.",
                topup.Id,
                topup.AmountMinor,
                reported);

            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["amountMinor"] =
                    [
                        $"The callback reports {reported} and the session was opened for {topup.AmountMinor}.",
                    ],
                },
                "A top-up is credited for the amount its session was opened with, and only when the "
                + "gateway confirms that amount.");
        }

        if (topup.State != TopupStates.Pending)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Top-up {topup.Id} is already {topup.State}.");
        }

        var platform = await accounts.PlatformAccountAsync(cancellationToken);
        var settledAt = clock.GetUtcNow();
        var settled = false;

        var result = await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.Topup,
                LedgerKeys.Topup(topup.Id),
                $"Top-up via {topup.Method}",
                [
                    // Double entry: the platform's own account carries the other side of every credit it
                    // hands out. The gateway settlement into the platform's bank is a fact about a bank
                    // account, not about this ledger.
                    new LedgerLeg(platform.Id, -topup.AmountMinor),
                    new LedgerLeg(topup.AccountId, topup.AmountMinor, callback.ProviderTransactionId),
                ]),
            async (connection, transaction, entryId, token) =>
            {
                settled = await topups.TrySettleAsync(
                    connection,
                    transaction,
                    topup.Id,
                    callback.ProviderTransactionId,
                    entryId,
                    settledAt,
                    token);

                if (!settled)
                {
                    // Another callback settled or failed this session between the read above and here.
                    // Throwing rolls the postings back with it, so the money follows the session state.
                    throw new MageRideException(
                        MageRideErrors.Conflict,
                        $"Top-up {topup.Id} changed state while it was being settled.");
                }
            },
            cancellationToken);

        var credited = !result.Replayed;

        logger.LogInformation(
            "Top-up {TopupId} settled for {AmountMinor} on {Method} ({ProviderTransactionId}); credited={Credited}.",
            topup.Id,
            topup.AmountMinor,
            topup.Method,
            callback.ProviderTransactionId,
            credited);

        return new TopupSettlement(
            await topups.ReadAsync(topup.Id, cancellationToken) ?? topup, credited, result.Replayed);
    }

    /// <summary>Whether a session is still inside D6' §7.1's 90-second window.</summary>
    public bool IsWithinPendingWindow(TopupRow topup)
    {
        ArgumentNullException.ThrowIfNull(topup);

        return clock.GetUtcNow() - topup.CreatedAt <= _options.TopupPendingWindow;
    }

    private void RequireAmountInRange(long amountMinor)
    {
        if (amountMinor >= _options.MinTopupMinor && amountMinor <= _options.MaxTopupMinor)
        {
            return;
        }

        throw new MageRideException(
            MageRideErrors.InvalidAmount,
            $"A top-up is between {_options.MinTopupMinor} and {_options.MaxTopupMinor} minor units; "
            + $"{amountMinor} is outside that.");
    }
}
