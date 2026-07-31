using System.ComponentModel.DataAnnotations;

namespace MageRide.Subscriptions.Configuration;

/// <summary>
/// subscription-svc's settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three of these decide whether the platform can collect anything at all</b> and are announced at
/// start-up: <see cref="WalletBaseUrl"/>, <see cref="WalletInternalApiKey"/> and
/// <see cref="ModeBBillingEnabled"/>. Each failure is silent from the inside — trips are accepted, the
/// month rolls over, nothing errors, and the fee simply is not charged.
/// </para>
/// <para>
/// The section is <c>Subscription</c>. D7' §4.2 gives this service no variables of its own, so unlike
/// wallet-svc there is no unprefixed spelling to honour; the two wallet keys are read from
/// <c>Wallet:*</c> as well, because that is what <c>.env.app.example</c> already ships for the service
/// on the other end of the seam and an operator should not have to set the same secret twice.
/// </para>
/// </remarks>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscription";

    // -------------------------------------------------------------------------------------------
    // The wallet seam (D-09, C046)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Base URL of wallet-svc. This service writes no ledger row itself — every movement of a driver's
    /// money is a call to <c>POST /v1/internal/wallet/{driverId}/debit</c>.
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ every fee charge answers 503</b> and ride-svc's accept fails rather than silently
    /// letting a second trip run free. That is the loud failure of the two available: a service that
    /// answered 200 with "nothing charged" would lose the platform its only revenue and look healthy.
    /// </remarks>
    public string? WalletBaseUrl { get; set; }

    /// <summary>
    /// The interim shared secret wallet-svc's internal plane demands (<c>X-MageRide-Internal-Key</c>),
    /// replaced by the mesh peer identity in C042.
    /// </summary>
    /// <remarks><b>Unset ⇒ every fee charge answers 503</b>, for the same reason as above.</remarks>
    public string? WalletInternalApiKey { get; set; }

    /// <summary>Timeout for a call to wallet-svc. D6' §8.3 budgets an internal hop at 2 s.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan WalletTimeout { get; set; } = TimeSpan.FromSeconds(2);

    // -------------------------------------------------------------------------------------------
    // The daily fee (D5' §2, D-13)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How many completed-or-accepted Mode C trips a driver may take in a Colombo day before the fee
    /// falls due. 1 — "the first trip of the day is always free" (US-9.1), verbatim.
    /// </summary>
    /// <remarks>
    /// A setting rather than a constant only so the value is visible next to the rule it encodes;
    /// changing it changes a P0 requirement and the start-up log says so. Zero would charge the first
    /// trip, which the fence forbids.
    /// </remarks>
    [Range(1, 10)]
    public int FreeTripsPerDay { get; set; } = 1;

    /// <summary>
    /// Rows returned by <c>GET /v1/fees/{driverId}/history</c> before the range is truncated.
    /// </summary>
    /// <remarks><b>No spec</b> — a bound, not a working limit. Three years of a two-vehicle driver.</remarks>
    [Range(1, 10_000)]
    public int MaxHistoryRows { get; set; } = 2_000;

    // -------------------------------------------------------------------------------------------
    // The Mode B monthly platform charge (D5' §2.1, AL-03)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The platform's monthly charge per Mode B vehicle. Rs 300 — URD §Daily Platform Fee Structure
    /// and ADD §19, and the same default <c>billing.monthly_subscriptions.amount_minor</c> carries.
    /// </summary>
    [Range(0, 10_000_000)]
    public long ModeBMonthlyFeeMinor { get; set; } = 30_000;

    /// <summary>
    /// Raise the current Colombo month's per-vehicle charges in the background. On.
    /// </summary>
    /// <remarks>
    /// Off means the rows are only ever created by the internal run endpoint. Announced at start-up:
    /// with nothing raising them, a Mode B fleet is never billed and the month simply passes.
    /// </remarks>
    public bool ModeBBillingEnabled { get; set; } = true;

    /// <summary>
    /// How often the background runner re-checks that the current Colombo month has been raised.
    /// </summary>
    /// <remarks>
    /// Hourly, not monthly-on-a-timer: the run is an idempotent upsert keyed by
    /// <c>(vehicle_id, period_month)</c>, so re-running it costs one statement and catches a vehicle
    /// approved mid-month, a process that was down at midnight on the 1st, and a clock that moved.
    /// A monthly timer would have exactly one chance per month to be running.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan ModeBBillingInterval { get; set; } = TimeSpan.FromHours(1);

    // -------------------------------------------------------------------------------------------
    // The internal plane
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Guards <c>/v1/internal/fees/**</c> until C042's mesh identity lands — the same interim scheme
    /// wallet-svc, content-svc and registry-svc carry.
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ <c>/v1/internal/fees/**</c> is not mapped at all</b>, following wallet-svc rather than
    /// content-svc: this family charges money, and an unauthenticated caller who can charge a driver's
    /// wallet is worse than a caller who gets a 404.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // Mode B passenger subscriptions (Epic 23, C048)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Maps <c>/v1/mode-b/**</c>. On.
    /// </summary>
    /// <remarks>
    /// Off means a Mode B passenger cannot request access, pay a fare or unsubscribe, and an owner
    /// has no roster — the whole of Epic 23. Announced at start-up, because from the outside it looks
    /// like a platform with no private vehicles on it rather than a switch somebody turned off.
    /// </remarks>
    public bool ModeBSubscriptionsEnabled { get; set; } = true;

    /// <summary>
    /// HMAC secret for <c>POST /v1/mode-b/pay/onepay/webhook</c> (D6' §7.1).
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ every OnePay subscription callback is refused.</b> There is no "accept unsigned"
    /// mode: a callback that marks a month paid without a signature settles the fleet owner's money
    /// for anyone who finds the URL.
    /// </remarks>
    public string? OnepayWebhookSecret { get; set; }

    /// <summary>HMAC secret for <c>POST /v1/mode-b/pay/lankaqr/confirm</c>. Unset refuses every callback.</summary>
    public string? LankaQrWebhookSecret { get; set; }

    /// <summary>
    /// Signs the expiring URLs on <c>payTo.lankaqrImageUrl</c> and <c>SubscriptionPayment.slipUrl</c>.
    /// </summary>
    /// <remarks>
    /// <b>Unset means a key generated per process</b>: correct for one instance and wrong for
    /// several, because a link minted by one replica does not verify on another. Said at start-up.
    /// </remarks>
    public string? FileLinkSigningKey { get; set; }

    /// <summary>
    /// How long a signed document link stays valid.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> 15 minutes is long enough for a pay sheet to render and a passenger to
    /// scan the QR, and short enough that a link copied out of a screenshot is dead by the time it is
    /// shared.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan FileLinkTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Where transfer-slip screenshots are written.
    /// </summary>
    /// <remarks>
    /// <b>Not object storage.</b> D-36 puts uploaded images on SSE-KMS buckets with Postgres holding
    /// a pointer; no service in this build has an S3 client, so this is a directory and a pod restart
    /// can lose the image while the payment row survives. The same seam ride-svc's
    /// <c>Ride:ProofPhotoRoot</c> opens.
    /// </remarks>
    public string? SlipRoot { get; set; }

    /// <summary>
    /// Largest transfer slip accepted, in bytes. 8 MiB.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it</b> — the same bound and the same number as <c>Ride:ProofPhotoMaxBytes</c>,
    /// because both are a phone photograph. The idempotency middleware's request buffer is raised to
    /// match in <c>SubscriptionApplication</c>, so the <c>413</c> a passenger gets is this one, with
    /// the size in it, rather than the middleware's generic refusal.
    /// </remarks>
    [Range(64 * 1024, 64 * 1024 * 1024)]
    public long SlipMaxBytes { get; set; } = 8 * 1024 * 1024;
}
