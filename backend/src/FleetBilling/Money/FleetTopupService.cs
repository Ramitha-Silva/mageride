using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Gateways;
using MageRide.FleetBilling.Persistence;
using MageRide.FleetBilling.Wallet;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling.Money;

/// <summary>A provider callback, already signature-verified and parsed.</summary>
internal sealed record FleetTopupCallback(
    string ProviderTransactionId, Guid? TopupId, string? OrderId, string Status, long? AmountMinor);

/// <summary>What a callback did.</summary>
/// <param name="Credited">False for a redelivery, a failure or a still-pending notice.</param>
internal sealed record FleetTopupSettlement(FleetTopup Topup, bool Credited, bool Replayed);

/// <summary>The fleet wallet's top-up: OnePay's card/wallet rail and AL-15's LankaQR hand-off.</summary>
internal interface IFleetTopupService
{
    Task<(FleetTopup Topup, GatewaySession Session)> StartAsync(
        Guid fleetId, Guid initiatedBy, string method, long amountMinor, string? returnUrl,
        CancellationToken cancellationToken);

    Task<FleetTopupSettlement> SettleAsync(
        string method, FleetTopupCallback callback, CancellationToken cancellationToken);

    /// <summary>Whether a session is still inside D6' §7.1's 90-second window.</summary>
    bool IsWithinPendingWindow(FleetTopup topup);
}

/// <inheritdoc cref="IFleetTopupService"/>
/// <remarks>
/// <para>
/// <b>Bank transfer is not here and cannot be added by configuration (AL-05).</b> There is no rail,
/// no <c>method</c> value the database would accept, no receipt column and no reconciliation queue;
/// adding one would need a migration, a contract change and a spec change before it could store its
/// first row.
/// </para>
/// <para>
/// <b>Two idempotency guards, answering different questions.</b>
/// <c>ux_fleet_topups_provider_txn</c> catches a redelivery of the same gateway transaction (R-19,
/// and the first thing checked); the ledger's <c>fleet_topup:{topupId}</c> key catches two
/// <em>different</em> callbacks arriving for one session, which a provider retrying under a new
/// transaction id produces. A redelivery answers 200 with the same body, because that is what stops
/// a provider retrying for ever.
/// </para>
/// <para>
/// <b>A callback whose amount disagrees with its session credits nothing.</b> Crediting what the
/// callback says lets a misconfigured or spoofed provider set the balance; crediting what the
/// session says credits money the organisation may not have paid. Both are wrong, so the session
/// stays Pending and the mismatch is logged as the settlement exception D6' §7.2 routes to Finance.
/// wallet-svc's rule, verbatim, because it is the same rail and the same failure.
/// </para>
/// <para>
/// <b>The account is resolved through wallet-svc, never created here.</b> A fleet's first top-up may
/// also be the moment its <c>owner_type='fleet'</c> account comes into existence, and the only thing
/// allowed to create one is the service that owns <c>billing.accounts</c> — so the session records
/// the id <c>POST /v1/internal/wallet/fleet/{fleetId}/account</c> returns. That route exists because
/// of this call: the alternative was posting a synthetic movement to force the row into existence,
/// which puts a transaction nobody made on a customer's statement.
/// </para>
/// </remarks>
internal sealed class FleetTopupService(
    IFleetTopupRepository topups,
    IFleetLedgerClient ledger,
    IUnitOfWorkFactory unitOfWorkFactory,
    IEnumerable<IFleetPaymentGateway> gateways,
    IOptions<FleetBillingOptions> options,
    TimeProvider clock,
    ILogger<FleetTopupService> logger) : IFleetTopupService
{
    private readonly FleetBillingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<(FleetTopup Topup, GatewaySession Session)> StartAsync(
        Guid fleetId,
        Guid initiatedBy,
        string method,
        long amountMinor,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        RequireAmountInRange(amountMinor);

        var gateway = gateways.FirstOrDefault(candidate => candidate.Method == method)
                      ?? throw new MageRideValidationException(
                          new Dictionary<string, string[]>(StringComparer.Ordinal)
                          {
                              ["method"] = [$"'{method}' is not a top-up method. AL-05 leaves onepay and lankaqr."],
                          });

        // Before the gateway is told anything: `billing.fleet_topups.account_id` is NOT NULL because
        // a session that does not know which wallet it credits is not a session, and the moment to
        // discover that wallet-svc is unreachable is before an operator has been sent to a payment
        // page.
        var account = await ledger.EnsureAccountAsync(fleetId, cancellationToken);

        // Our own reference, minted before the gateway is told anything, so a callback that echoes
        // only `orderId` can still find this session (D6' §7.1).
        var orderId = $"mr-fleet-topup-{Guid.NewGuid():N}";

        var topup = await topups.CreateAsync(
            fleetId,
            account.AccountId,
            initiatedBy,
            method,
            amountMinor,
            orderId,
            clock.GetUtcNow(),
            cancellationToken);

        try
        {
            var session = await gateway.StartAsync(topup.Id, orderId, amountMinor, returnUrl, cancellationToken);

            return (topup, session);
        }
        catch (MageRideException)
        {
            // Marked Failed rather than left Pending for a reconciler to puzzle over: the gateway
            // never accepted it, so there is no money in flight to reconcile.
            await topups.TryFailAsync(
                topup.Id, null, "gateway session could not be opened", clock.GetUtcNow(), cancellationToken);

            throw;
        }
    }

    public async Task<FleetTopupSettlement> SettleAsync(
        string method, FleetTopupCallback callback, CancellationToken cancellationToken)
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
                "Fleet top-up callback for {ProviderTransactionId} is a redelivery of top-up {TopupId} "
                + "({State}); nothing was credited.",
                callback.ProviderTransactionId,
                already.Id,
                already.State);

            return new FleetTopupSettlement(already, Credited: false, Replayed: true);
        }

        var topup = await topups.ResolveAsync(callback.TopupId, callback.OrderId, cancellationToken)
                    ?? throw new MageRideException(
                        MageRideErrors.NotFound,
                        "No fleet top-up session matches this callback's topupId or orderId.");

        if (!string.Equals(topup.Method, method, StringComparison.Ordinal))
        {
            // A OnePay callback confirming a LankaQR session, or the reverse. Refused: the two rails
            // have different secrets, and honouring it would let either secret settle the other's
            // money.
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Fleet top-up {topup.Id} was started on '{topup.Method}' and this callback arrived on '{method}'.");
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
            return new FleetTopupSettlement(topup, Credited: false, Replayed: false);
        }

        if (callback.AmountMinor is { } reported && reported != topup.AmountMinor)
        {
            logger.LogError(
                "Fleet top-up {TopupId} was opened for {Expected} and the gateway confirmed {Reported}. The "
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
                "A top-up is credited for the amount its session was opened with, and only when the gateway "
                + "confirms that amount.");
        }

        if (!string.Equals(topup.State, TopupStates.Pending, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.Conflict, $"Fleet top-up {topup.Id} is already {topup.State}.");
        }

        // Credit first, record second — the settlement path's half of C047's rule. A crash between
        // the two leaves money in the wallet and a Pending session, which the provider's next
        // redelivery repairs against the same `fleet_topup:{topupId}` ledger key; the other order
        // would leave a Succeeded session against money that never arrived.
        var posting = await ledger.CreditAsync(
            topup.FleetId,
            topup.AmountMinor,
            LedgerKeys.TopupKind,
            LedgerKeys.FleetTopup(topup.Id),
            $"Fleet wallet top-up via {topup.Method}",
            callback.ProviderTransactionId,
            cancellationToken);

        var settledAt = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var settled = await topups.TrySettleAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            topup.Id,
            callback.ProviderTransactionId,
            posting.EntryId,
            settledAt,
            cancellationToken);

        if (!settled)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Fleet top-up {topup.Id} changed state while it was being settled.");
        }

        await unitOfWork.CommitAsync(cancellationToken);

        var credited = !posting.Replayed;

        logger.LogInformation(
            "Fleet top-up {TopupId} settled for {AmountMinor} on {Method} ({ProviderTransactionId}); "
            + "credited={Credited}, wallet now {BalanceAfterMinor}.",
            topup.Id,
            topup.AmountMinor,
            topup.Method,
            callback.ProviderTransactionId,
            credited,
            posting.BalanceAfterMinor);

        return new FleetTopupSettlement(
            await topups.ReadAsync(topup.Id, cancellationToken) ?? topup, credited, posting.Replayed);
    }

    public bool IsWithinPendingWindow(FleetTopup topup)
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
            $"A fleet top-up is between {_options.MinTopupMinor} and {_options.MaxTopupMinor} minor units; "
            + $"{amountMinor} is outside that.");
    }
}
