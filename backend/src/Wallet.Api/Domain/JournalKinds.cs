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

    /// <summary>
    /// A wallet-paid fare (D-10). <b>Δ AL-57: it now has two wallet legs, not one.</b>
    /// </summary>
    /// <remarks>
    /// Before AL-57 this debited a driver against the platform — fare-svc settling a D-05 penalty
    /// into a fare. The AL-57 rail is a different movement under the same kind: the passenger's
    /// balance moves to the driver's, and MageRide is the custodian rather than a party. Both are
    /// "a fare was paid from a wallet", which is what the kind means, and the legs say which.
    /// </remarks>
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
    /// A fleet's consolidated monthly per-Mode-B-vehicle platform charge (AL-03, US-13.10).
    /// <b>Δ C060</b> — added to <c>ck_journal_entries_kind</c> by migration 1108.
    /// </summary>
    /// <remarks>
    /// The eleventh kind, and the first that debits a <c>fleet</c> account rather than a driver's.
    /// Posted only through <see cref="Endpoints.InternalWalletEndpoints"/>'s fleet routes, by
    /// fleet-billing-svc, which owns <c>billing.fleet_invoices</c> and its per-vehicle breakdown.
    /// <c>adjustment</c> was the alternative and is wrong: it is the Finance queue's correction kind
    /// (US-14.11), and netting the platform's largest recurring revenue line into it would make
    /// revenue and corrections one number for ever.
    /// </remarks>
    public const string FleetInvoice = "fleet_invoice";

    /// <summary>
    /// A weekly sweep of a driver's balance to their bank (<b>Δ AL-58</b>, migration 1109).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The twelfth kind, and the one that discharges what AL-57 created: a card-paid fare leaves the
    /// driver holding a claim on MageRide, and this is the entry that extinguishes it. Driver debit,
    /// platform credit — the mirror of a top-up, and for the same reason: the platform account is
    /// the counterparty of every movement of real-world cash.
    /// </para>
    /// <para>
    /// <b>Not <c>adjustment</c>.</b> That is the Finance queue's correction kind (US-14.11), and
    /// netting the platform's entire outward payment flow into it would make corrections and payouts
    /// one number for ever — the same argument C060 made for <c>fleet_invoice</c>.
    /// </para>
    /// <para>
    /// Posted only through this service's own <c>/driver-payout</c> routes, never through the
    /// debit/credit seam: the key is composed from the payout id here so a retried run cannot pay
    /// twice, and a caller free to choose its own key could.
    /// </para>
    /// </remarks>
    public const string DriverPayout = "driver_payout";

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

    /// <summary>
    /// Kinds fleet-billing-svc may post as a <b>debit</b> of a fleet's wallet. <b>Δ C060.</b>
    /// </summary>
    /// <remarks>
    /// Exactly one. A fleet wallet exists to pay the platform's monthly per-Mode-B-vehicle charge
    /// (AL-03) and there is no other reason MageRide takes money out of it: a Mode B passenger's
    /// subscription is a pass-through to the owner that never enters this ledger at all (§18b,
    /// C048), Mode A is free, and a fleet has no Mode C vehicles to owe a daily fee for. The three
    /// driver debit kinds are deliberately absent — a `daily_fee` against an organisation would be a
    /// charge no rule in D5' §2.1 describes.
    /// </remarks>
    public static readonly string[] FleetDebitKinds = [FleetInvoice];

    /// <summary>
    /// Kinds fleet-billing-svc may post as a <b>credit</b> of a fleet's wallet. <b>Δ C060.</b>
    /// </summary>
    /// <remarks>
    /// <b><c>topup</c> is admitted here and refused on the driver route</b>, and the asymmetry is
    /// deliberate. The driver rails live in this service — <c>billing.topups</c>, the R-19 provider
    /// dedupe and the amount check are all here — so a caller posting <c>topup</c> through the seam
    /// would bypass them. The *fleet* rails are fleet-billing-svc's, which ADD §6 gives "top-up via
    /// card/OnePay/LankaQR" outright; the same two guards live there over
    /// <c>billing.fleet_topups</c> (migration 1108), and this service holds no fleet session to
    /// check against. <c>adjustment</c> is admin-bff's correction, which an organisation needs for
    /// the same reason a driver does.
    /// </remarks>
    public static readonly string[] FleetCreditKinds = [Topup, Adjustment];

    /// <summary>Whether <paramref name="kind"/> may be posted through the internal debit route.</summary>
    public static bool IsInternalDebit(string? kind) =>
        kind is not null && Array.IndexOf(InternalDebitKinds, kind) >= 0;

    /// <summary>Whether <paramref name="kind"/> may be posted through the internal credit route.</summary>
    public static bool IsInternalCredit(string? kind) =>
        kind is not null && Array.IndexOf(InternalCreditKinds, kind) >= 0;

    /// <summary>Whether <paramref name="kind"/> may be posted through the fleet debit route.</summary>
    public static bool IsFleetDebit(string? kind) =>
        kind is not null && Array.IndexOf(FleetDebitKinds, kind) >= 0;

    /// <summary>Whether <paramref name="kind"/> may be posted through the fleet credit route.</summary>
    public static bool IsFleetCredit(string? kind) =>
        kind is not null && Array.IndexOf(FleetCreditKinds, kind) >= 0;
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

    /// <summary>
    /// The AL-57 wallet fare. <b>1101's own header fixes this spelling</b> —
    /// <c>'trip_payment:' || ride_payment_id</c> — so it is composed here from the id fare-svc
    /// sends rather than accepted from the body: a caller free to choose the key is a caller free to
    /// pay one fare twice.
    /// </summary>
    public static string TripPayment(Guid ridePaymentId) => $"trip_payment:{ridePaymentId}";

    /// <summary>The AL-58 sweep. One key per payout id, so a retried run debits once.</summary>
    public static string DriverPayout(Guid payoutId) => $"driver_payout:{payoutId}";

    /// <summary>
    /// The reversal a <c>FAILED</c> instruction earns.
    /// </summary>
    /// <remarks>
    /// A second key rather than a second kind: the movement is still about that payout and belongs
    /// beside it in the driver's history, and the sign says which direction it went. Sharing the
    /// debit's key would make the reversal a replay of the debit and restore nothing.
    /// </remarks>
    public static string DriverPayoutReversal(Guid payoutId) => $"driver_payout_reversal:{payoutId}";
}
