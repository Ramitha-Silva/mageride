using System.ComponentModel.DataAnnotations;

namespace MageRide.FleetBilling.Configuration;

/// <summary>
/// fleet-billing-svc's knobs. Every default is argued at its declaration; the ones with no spec
/// behind them say so.
/// </summary>
/// <remarks>
/// <b>D7' §4.2 gives this service no variables of its own</b> — it predates fleet-billing-svc being
/// split out of fleet-svc. The two wallet keys and the two gateway secrets are therefore also read
/// under the spellings `.env.app.example` already ships for the services on the other end of each
/// seam (`Wallet:*`, `Onepay:*`, `ComBankIpg:*`), because a co-located deployment should not have to
/// set the same secret twice under two names. A `FleetBilling:*` value wins where both are set.
/// </remarks>
public sealed class FleetBillingOptions
{
    public const string SectionName = "FleetBilling";

    // -------------------------------------------------------------------------------------------
    // The ledger seam (C046). Without it this service cannot move a cent.
    // -------------------------------------------------------------------------------------------

    /// <summary>Base address of wallet-svc, whose internal plane owns every posting (D-09).</summary>
    /// <remarks>
    /// <b>Unset ⇒ no invoice can ever be settled and no top-up can ever be credited.</b> Both
    /// answer <c>503</c> rather than recording a payment that did not happen. Also read as
    /// <c>Wallet:BaseUrl</c>.
    /// </remarks>
    public string? WalletBaseUrl { get; set; }

    /// <summary>The shared secret wallet-svc's <c>/v1/internal/wallet/**</c> demands (C046).</summary>
    /// <remarks>Also read as <c>Wallet:InternalApiKey</c>. Replaced by the mesh peer identity in C042.</remarks>
    public string? WalletInternalApiKey { get; set; }

    /// <summary>Per-attempt budget for the ledger hop (D6' §8.3's internal call).</summary>
    /// <remarks>
    /// Longer than subscription-svc's 2 s: that one runs inside a 15 s offer window and this one
    /// runs inside a person pressing Pay on a web portal, or inside a background sweep with no
    /// deadline at all.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan WalletTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------------------------
    // Invoicing
    // -------------------------------------------------------------------------------------------

    /// <summary>Generate, settle and dun in this process.</summary>
    /// <remarks>
    /// <b>Off ⇒ no fleet is ever invoiced.</b> The per-vehicle charges keep piling up in
    /// <c>billing.monthly_subscriptions</c> and nothing consolidates them, which from the Fleet
    /// Portal is indistinguishable from a platform that does not charge fleets. Announced as an
    /// error at start-up. The internal run route still works, so an operator can drive it by hand.
    /// </remarks>
    public bool InvoicingEnabled { get; set; } = true;

    /// <summary>
    /// How often the runner generates the current Colombo month's invoices, attempts settlement
    /// and sweeps for overdue ones.
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> An interval rather than a monthly alarm, for subscription-svc's reason
    /// (C047): the run is idempotent, so re-running costs one statement and catches a vehicle
    /// approved on the 9th, a deployment that was rolling at midnight on the 1st and a replica
    /// whose clock moved. A monthly alarm gets exactly one attempt per month to be running, and its
    /// failure mode is a month nobody is billed for.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan RunInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long after issue an invoice is due (US-13.10's "monthly", with no term stated anywhere).
    /// </summary>
    /// <remarks>
    /// <b>No spec.</b> Seven days: long enough for an operator who tops up monthly to notice, short
    /// enough that dunning still happens inside the month being billed. The value is copied onto
    /// the invoice at generation (<c>billing.fleet_invoices.due_at</c>), so changing it here moves
    /// the term for invoices not yet issued and never retro-dates one that is.
    /// </remarks>
    [Range(typeof(TimeSpan), "1.00:00:00", "90.00:00:00")]
    public TimeSpan PaymentTerm { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Attempt settlement against the fleet wallet as part of each run, without waiting to be asked.
    /// </summary>
    /// <remarks>
    /// <b>No spec, and on is the reading US-13.10 supports</b> — "I pay a monthly fee per Mode B
    /// vehicle *from my fleet wallet*" describes a standing arrangement rather than a checkout.
    /// A wallet with too little in it is left DUE and nothing else happens, which is what makes
    /// this safe to run every hour. Off leaves <c>POST …/billing/{invoiceId}/pay</c> as the only
    /// way an invoice is ever settled.
    /// </remarks>
    public bool AutoSettle { get; set; } = true;

    /// <summary>Invoices touched per sweep, per phase.</summary>
    /// <remarks><b>No spec</b> — a bound on a statement that would otherwise walk every open invoice
    /// on the platform, not a working limit. The next tick takes the next batch.</remarks>
    [Range(1, 5_000)]
    public int RunBatchSize { get; set; } = 200;

    /// <summary>The monthly platform charge per Mode B vehicle, in minor units.</summary>
    /// <remarks>
    /// URD §Daily Platform Fee Structure / ADD §19 (Rs 300). <b>Read, never written:</b> the amount
    /// on a line is the one subscription-svc already raised in
    /// <c>billing.monthly_subscriptions.amount_minor</c>, and this value exists only so a start-up
    /// warning can say when the two services disagree — a divergence would bill a fleet an amount
    /// no vehicle's charge adds up to. Must equal <c>Subscription:ModeBMonthlyFeeMinor</c>.
    /// </remarks>
    [Range(0, 10_000_000)]
    public long ModeBMonthlyFeeMinor { get; set; } = 30_000;

    // -------------------------------------------------------------------------------------------
    // The fleet wallet's top-up rails (US-13.10b, AL-05). OnePay and LankaQR, and nothing else.
    // -------------------------------------------------------------------------------------------

    /// <summary>OnePay's API key (D6' §7.1). Also read as <c>Onepay:ApiKey</c>.</summary>
    /// <remarks><b>Unset ⇒ the card rail answers 503</b> and LankaQR is the only way to top up.</remarks>
    public string? OnepayApiKey { get; set; }

    /// <summary>OnePay's base address. Also read as <c>Onepay:BaseUrl</c>.</summary>
    public string? OnepayBaseUrl { get; set; }

    /// <summary>The HMAC secret OnePay's callback is signed with. Also read as <c>Onepay:WebhookSecret</c>.</summary>
    /// <remarks>
    /// <b>Unset ⇒ every OnePay callback is refused and no fleet is ever credited.</b> There is no
    /// unsigned mode: a wallet-credit endpoint that trusts an unsigned body is a free-money
    /// endpoint. wallet-svc's rule, verbatim, because it is the same rail.
    /// </remarks>
    public string? OnepayWebhookSecret { get; set; }

    /// <summary>AL-15's "Pay" deep link into the bank app. Also read as <c>LankaQr:DeepLinkTemplate</c>.</summary>
    /// <remarks>
    /// <c>{orderId}</c>, <c>{amountMinor}</c> and <c>{merchantId}</c> are substituted.
    /// <b>Unset ⇒ that rail answers 503</b> — AL-15 makes the deep link the primary path, so there
    /// is nothing to fall back to.
    /// </remarks>
    public string? LankaQrDeepLinkTemplate { get; set; }

    /// <summary>The EMVCo payload template, when the acquirer has given the deployment one.</summary>
    /// <remarks>
    /// <b>No spec, and unset omits the QR fallback.</b> A LankaQR payload's merchant fields and CRC
    /// belong to the acquiring bank; composing one here would put a plausible, unscannable code in
    /// front of an operator, which is worse than not offering the fallback. Also read as
    /// <c>LankaQr:PayloadTemplate</c>.
    /// </remarks>
    public string? LankaQrPayloadTemplate { get; set; }

    /// <summary>The merchant id substituted into the two templates. Also read as <c>LankaQr:MerchantId</c>.</summary>
    public string? LankaQrMerchantId { get; set; }

    /// <summary>
    /// The HMAC secret the LankaQR confirm callback is signed with (D-12). Also read as
    /// <c>LankaQr:WebhookSecret</c> and <c>ComBankIpg:WebhookSecret</c>, which is D7' §4.2's spelling.
    /// </summary>
    public string? LankaQrWebhookSecret { get; set; }

    /// <summary>Smallest top-up this service will open a session for.</summary>
    /// <remarks><b>No spec</b> — a tenth of one vehicle's monthly charge, so a top-up is always
    /// worth a gateway round trip.</remarks>
    [Range(1, 100_000_000)]
    public long MinTopupMinor { get; set; } = 3_000;

    /// <summary>Largest top-up this service will open a session for.</summary>
    /// <remarks><b>No spec</b> — ten times wallet-svc's driver ceiling, because a fleet settles
    /// hundreds of vehicles at once.</remarks>
    [Range(1, 100_000_000_000)]
    public long MaxTopupMinor { get; set; } = 100_000_000;

    /// <summary>D6' §7.1's window a client polls a Pending session over.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "00:30:00")]
    public TimeSpan TopupPendingWindow { get; set; } = TimeSpan.FromSeconds(90);

    // -------------------------------------------------------------------------------------------
    // Dunning (notification-svc)
    // -------------------------------------------------------------------------------------------

    /// <summary>Base address of notification-svc's internal plane (C051).</summary>
    /// <remarks>
    /// <b>Unset ⇒ an overdue invoice is recorded and nobody is told.</b> The OVERDUE status and the
    /// <c>fleet.invoice_overdue</c> event still happen, so the Fleet Portal can draw it; the push
    /// US-13.10's operator would have received does not.
    /// </remarks>
    public string? NotificationBaseUrl { get; set; }

    /// <summary>The shared secret notification-svc's internal plane demands.</summary>
    public string? NotificationInternalApiKey { get; set; }

    /// <summary>Per-attempt budget for the notification hop.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan NotificationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often one organisation may be dunned about one invoice.</summary>
    /// <remarks>
    /// <b>No spec.</b> The sweep runs hourly and an invoice stays overdue until it is paid, so
    /// without this every operator with an unpaid bill would be pushed twenty-four times a day.
    /// The invoice's own <c>overdue_at</c> is the clock — the state is claimed once and the reminder
    /// is repeated on this cadence.
    /// </remarks>
    [Range(typeof(TimeSpan), "01:00:00", "30.00:00:00")]
    public TimeSpan DunningInterval { get; set; } = TimeSpan.FromDays(3);

    // -------------------------------------------------------------------------------------------
    // The internal plane and the read surface
    // -------------------------------------------------------------------------------------------

    /// <summary>The interim shared secret <c>/v1/internal/fleet-billing/**</c> demands, until mTLS (C042).</summary>
    /// <remarks>
    /// <b>Unset leaves the internal family unmapped</b>, the posture every other internal plane on
    /// the platform takes. What is behind it is a route that raises invoices and moves money.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    /// <summary>Largest page the invoice list and the wallet statement will return.</summary>
    /// <remarks><b>No spec</b> — D3' §0 caps a page at 100; this is the service's own default.</remarks>
    [Range(1, 100)]
    public int MaxPageSize { get; set; } = 50;
}
