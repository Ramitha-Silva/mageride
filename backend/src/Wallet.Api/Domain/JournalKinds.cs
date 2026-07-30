namespace MageRide.Wallet.Domain;

/// <summary>
/// The <c>billing.journal_entries.kind</c> vocabulary (§10, migration 1101), and which of them each
/// caller may post.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>reseller_commission</c>, and its absence is the mechanism.</b> AL-01 removed the
/// per-transfer commission from the platform; because the ledger's <c>ck_journal_entries_kind</c>
/// admits ten kinds and none of them is a fee, a commission leg cannot be recorded even by a service
/// that decided to charge one. The C046 definition of done — "no fee row is ever written" — is held
/// by the database, not by this class.
/// </para>
/// <para>
/// The whitelists below are what stop <c>/v1/internal/wallet/{driverId}/debit</c> and
/// <c>/credit</c> becoming a general "write me any entry" API. Each kind in each list has a caller
/// named by a spec; the operation's contract documentation carries the table.
/// </para>
/// </remarks>
internal static class JournalKinds
{
    /// <summary>An in-app top-up, credited on the provider callback (D6' §7.1/§7.2).</summary>
    public const string Topup = "topup";

    /// <summary>The D-13 daily platform fee, charged before a driver's second trip of the day.</summary>
    public const string DailyFee = "daily_fee";

    /// <summary>A wallet-paid fare (D-10).</summary>
    public const string TripPayment = "trip_payment";

    /// <summary>The D-05 cross-trip cancellation penalty, settled on a later trip (D5' §7.1).</summary>
    public const string PenaltySettle = "penalty_settle";

    /// <summary>An admin correction — the <c>reverse-fee</c> route admin-bff exposes (C065).</summary>
    public const string Adjustment = "adjustment";

    /// <summary>A passenger tip reaching the driver (US-8.13).</summary>
    public const string TipPayout = "tip_payout";

    /// <summary>A refunded ride payment (E-05).</summary>
    public const string PaymentRefund = "payment_refund";

    /// <summary>An overpayment returned (D-10).</summary>
    public const string OverpaidReversal = "overpaid_reversal";

    /// <summary>Bulk credit bought at the per-denomination discount (US-9.19).</summary>
    public const string VoucherPurchase = "voucher_purchase";

    /// <summary>Driver-to-driver credit at exactly par (US-9.13/9.21, AL-01).</summary>
    public const string DriverTransfer = "driver_transfer";

    /// <summary>
    /// Kinds another service may post as a <b>debit</b> of a driver's wallet.
    /// </summary>
    /// <remarks>
    /// <c>daily_fee</c> is subscription-svc's (D-13); <c>penalty_settle</c> and <c>trip_payment</c>
    /// are fare-svc's (D5' §7.1, D-10). Nothing else: a service that wants to take money out of a
    /// driver's wallet for a new reason needs a kind, and a kind needs a migration and a spec line.
    /// </remarks>
    public static readonly string[] InternalDebitKinds = [DailyFee, PenaltySettle, TripPayment];

    /// <summary>
    /// Kinds another service may post as a <b>credit</b> of a driver's wallet.
    /// </summary>
    /// <remarks>
    /// <c>tip_payout</c>, <c>payment_refund</c> and <c>overpaid_reversal</c> are fare-svc's;
    /// <c>adjustment</c> is admin-bff's fee reversal. <b>Not <c>topup</c></b>, and not
    /// <c>voucher_purchase</c> or <c>driver_transfer</c> — those three are this service's own
    /// endpoints, and letting a caller post them here would put the discount arithmetic and the
    /// provider dedupe outside the service that owns them.
    /// </remarks>
    public static readonly string[] InternalCreditKinds =
        [TipPayout, PaymentRefund, OverpaidReversal, Adjustment];

    /// <summary>Whether <paramref name="kind"/> may be posted through the internal debit route.</summary>
    public static bool IsInternalDebit(string? kind) =>
        kind is not null && Array.IndexOf(InternalDebitKinds, kind) >= 0;

    /// <summary>Whether <paramref name="kind"/> may be posted through the internal credit route.</summary>
    public static bool IsInternalCredit(string? kind) =>
        kind is not null && Array.IndexOf(InternalCreditKinds, kind) >= 0;
}

/// <summary>
/// How this service composes <c>billing.journal_entries.idempotency_key</c>.
/// </summary>
/// <remarks>
/// The column is UNIQUE, which is what makes a retried money operation a no-op rather than a second
/// posting — so the key must be derived from the business fact and never from a random value. 1101
/// records the three spellings C004 fixed (daily fee, penalty settle, trip payment); these are the
/// three C046 adds, and migration 1107's comment carries them too so the two cannot drift.
/// </remarks>
internal static class LedgerKeys
{
    public static string Topup(Guid topupId) => $"topup:{topupId}";

    public static string VoucherPurchase(Guid purchaseId) => $"voucher_purchase:{purchaseId}";

    public static string DriverTransfer(Guid transferId) => $"driver_transfer:{transferId}";
}
