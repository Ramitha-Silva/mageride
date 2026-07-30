using MageRide.Shared.Errors;
using MageRide.Wallet.Domain;
using MageRide.Wallet.Ledger;
using MageRide.Wallet.Persistence;

namespace MageRide.Wallet.Money;

/// <summary>A purchase and the balance it left behind.</summary>
internal sealed record VoucherOutcome(VoucherPurchaseRow Purchase, long BalanceAfterMinor, bool Replayed);

/// <summary>
/// Bulk credit vouchers (US-9.19): pay the discounted price, receive the face value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The discount is applied at purchase and nowhere else</b> (D5' §9.3). Credit that has been bought
/// is ordinary wallet credit: when the buyer passes it on, a driver-to-driver transfer moves the exact
/// value with no commission (AL-01), so the discount they got at purchase is their entire margin. That
/// is why "reseller" is not a role — it is a driver who bought at 900 and hands on 1,000.
/// </para>
/// <para>
/// <b>The buyer's own wallet is credited, immediately.</b> There is no redeem code, no second party and
/// no voucher to hold: <c>billing.voucher_purchases</c> is a purchase record, not an instrument
/// (C005's own comment says so).
/// </para>
/// </remarks>
internal sealed class VoucherService(
    IVoucherRepository vouchers,
    IAccountRepository accounts,
    ILedgerService ledger,
    ILogger<VoucherService> logger)
{
    /// <summary>Buys one voucher for the buyer's own wallet.</summary>
    /// <remarks>
    /// <b>Idempotent on the gateway reference</b>, which is what the C046 definition of done asks for:
    /// a purchase whose <c>gatewayRef</c> has been seen returns the first purchase and credits nothing
    /// twice (<c>ux_voucher_purchases_gateway_ref</c>, C005). Without a reference — a purchase paid from
    /// an existing balance rather than at a gateway — the `Idempotency-Key` replay in the kernel is the
    /// guard, backed by the ledger's own key.
    /// </remarks>
    public async Task<VoucherOutcome> PurchaseAsync(
        Guid buyerId, long denominationMinor, string? gatewayRef, CancellationToken cancellationToken)
    {
        var tiers = await vouchers.ReadTiersAsync(cancellationToken);
        var price = VoucherPricing.Price(denominationMinor, tiers);

        if (!string.IsNullOrWhiteSpace(gatewayRef))
        {
            var existing = await vouchers.ReadByGatewayRefAsync(gatewayRef, cancellationToken);

            if (existing is not null)
            {
                if (existing.BuyerId != buyerId)
                {
                    // The same gateway payment cannot buy credit for two drivers. Refused rather than
                    // silently attributed to the first, which is the shape of a stolen reference.
                    throw new MageRideException(
                        MageRideErrors.Conflict,
                        "That gateway reference has already bought a voucher for a different driver.");
                }

                logger.LogInformation(
                    "Voucher purchase for gateway reference {GatewayRef} is a replay of {PurchaseId}; nothing "
                    + "was credited.",
                    gatewayRef,
                    existing.Id);

                var summary = await accounts.ReadSummaryAsync(buyerId, cancellationToken);

                return new VoucherOutcome(existing, summary?.BalanceMinor ?? 0, Replayed: true);
            }
        }

        var buyer = await accounts.EnsureDriverAccountAsync(buyerId, cancellationToken);
        var platform = await accounts.PlatformAccountAsync(cancellationToken);

        // Minted here, so the ledger key (`voucher_purchase:{id}`) and the purchase row agree even
        // though the row is written inside the ledger's transaction.
        var purchaseId = Guid.NewGuid();

        var result = await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.VoucherPurchase,
                LedgerKeys.VoucherPurchase(purchaseId),
                $"Bulk credit voucher {denominationMinor} minor units at {price.DiscountBps} bps",
                [
                    // The **face value** moves, both ways: what the buyer paid is a fact about a gateway
                    // settlement, and the discount is the platform's cost of the sale, recorded on the
                    // purchase row. `ck_voucher_purchases_credited` (C005) requires
                    // credited = denomination, so these two legs are the only shape this entry can take.
                    new LedgerLeg(platform.Id, -price.CreditedMinor),
                    new LedgerLeg(buyer.Id, price.CreditedMinor, gatewayRef),
                ]),
            (connection, transaction, entryId, token) =>
                vouchers.InsertPurchaseAsync(
                    connection, transaction, purchaseId, buyerId, price, gatewayRef, entryId, token),
            cancellationToken);

        var purchase = await vouchers.ReadAsync(purchaseId, cancellationToken)
                       ?? throw new InvalidOperationException(
                           $"Voucher purchase {purchaseId} was posted but cannot be read back.");

        logger.LogInformation(
            "Driver {BuyerId} bought {DenominationMinor} of credit for {PaidMinor} at {DiscountBps} bps "
            + "(purchase {PurchaseId}).",
            buyerId,
            price.CreditedMinor,
            price.PaidMinor,
            price.DiscountBps,
            purchaseId);

        return new VoucherOutcome(
            purchase, result.For(buyer.Id)?.BalanceAfterMinor ?? 0, result.Replayed);
    }
}
