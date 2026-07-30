using System.ComponentModel.DataAnnotations;

namespace MageRide.Wallet.Configuration;

/// <summary>
/// wallet-svc's settings. D7' §4.2 gives this service four — <c>Onepay__ApiKey</c>,
/// <c>ComBankIpg__WebhookSecret</c>, <c>LowBalance__ThresholdMinor</c>=20000 — and everything else
/// here is argued at its declaration.
/// </summary>
/// <remarks>
/// <para>
/// The section is <c>Wallet</c>, but the three variables D7' §4.2 spells without that prefix are read
/// where it spells them: <c>Onepay:*</c>, <c>LankaQr:*</c>, <c>ComBankIpg:*</c> and
/// <c>LowBalance:ThresholdMinor</c>. <c>.env.app.example</c> ships them that way and an operator who
/// set the documented variable must not be setting a key nothing reads — the same problem
/// content-svc's <c>Cache__Ttl</c> and reputation-svc's <c>Reputation__Outbox__*</c> record.
/// </para>
/// <para>
/// <b>Two of these decide whether money can move at all</b> and are announced at start-up:
/// <see cref="OnepayWebhookSecret"/> and <see cref="LankaQrWebhookSecret"/>. Without a secret the
/// matching callback is refused, so a top-up initiates, the driver pays, and the wallet is never
/// credited — the worst failure this service has, and invisible from the inside.
/// </para>
/// </remarks>
public sealed class WalletOptions
{
    public const string SectionName = "Wallet";

    // -------------------------------------------------------------------------------------------
    // D-08's balance cache (D5' §9.2)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// TTL of the <c>wallet:bal:{driverId}</c> value this service writes through. 5 s, D-08 verbatim.
    /// </summary>
    /// <remarks>
    /// <b>Must equal <c>Dispatch:WalletCacheTtl</c>.</b> Both sides of one key: dispatch-svc populates
    /// it on a miss with its own TTL and this service refreshes it on every movement. A longer TTL here
    /// would leave a debited balance readable for longer than the gate expects.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan BalanceCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Write through to the cache at all. On.
    /// </summary>
    /// <remarks>
    /// Off means a top-up is invisible to dispatch-svc for up to the TTL, and a debit stays readable
    /// for the same window — a driver could be offered one trip they can no longer pay for. Not an
    /// outage, and announced at start-up because it looks like one from nowhere.
    /// </remarks>
    public bool BalanceCacheEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // US-9.9's low-balance warning
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Balance below which a driver is warned — D7' §4.2's <c>LowBalance__ThresholdMinor</c>=20000
    /// (Rs 200, D5' §9.4).
    /// </summary>
    /// <remarks>
    /// Edge-triggered on the crossing, not on the state: a driver already below it who spends again is
    /// not warned twice. D5' §9.4's second clause — below zero, "Top Up Required" — travels as the
    /// event's <c>severity</c> rather than as a second threshold, because only a client draws a banner.
    /// </remarks>
    [Range(0, 10_000_000)]
    public long LowBalanceThresholdMinor { get; set; } = 20_000;

    // -------------------------------------------------------------------------------------------
    // OnePay (D6' §7.1)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// OnePay app/API key. Unset means the gateway cannot be called and a top-up initiate answers
    /// <c>503</c>.
    /// </summary>
    public string? OnepayApiKey { get; set; }

    /// <summary>Base URL of the OnePay create-session API (D6' §7.1).</summary>
    public string? OnepayBaseUrl { get; set; }

    /// <summary>
    /// Secret the OnePay callback's <c>X-Signature</c> is verified against.
    /// </summary>
    /// <remarks>
    /// <b>Unset means every OnePay callback is refused</b> — the top-up initiates, the driver pays at
    /// the gateway, and the wallet is never credited. Announced loudly at start-up; there is no
    /// "accept unsigned" mode, because a wallet-credit endpoint that trusts an unsigned body is a
    /// free-money endpoint.
    /// </remarks>
    public string? OnepayWebhookSecret { get; set; }

    // -------------------------------------------------------------------------------------------
    // LankaQR / Commercial Bank IPG (D6' §7.2, D-12, AL-15)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Template for AL-15's "Pay" deep link into the bank app. <c>{orderId}</c> and
    /// <c>{amountMinor}</c> are substituted.
    /// </summary>
    /// <remarks>
    /// <b>No spec gives the scheme</b>, and it belongs to the bank rather than to this platform —
    /// D6' §7.2 says "'Pay' deep link to bank app (QR fallback only)" and stops. Unset means the
    /// LankaQR route answers <c>503</c> rather than returning a link that resolves to nothing.
    /// </remarks>
    public string? LankaQrDeepLinkTemplate { get; set; }

    /// <summary>
    /// Template for the EMVCo QR payload, the fallback for a device where the deep link does not
    /// resolve (AL-15).
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not generated.</b> A LankaQR payload is an EMVCo TLV string whose merchant
    /// fields and CRC belong to the acquiring bank; composing one here would put a plausible,
    /// unscannable string in front of a driver. Unset means <c>qrPayload</c> is omitted and the deep
    /// link is the only route — which is what AL-15 makes the primary path anyway.
    /// </remarks>
    public string? LankaQrPayloadTemplate { get; set; }

    /// <summary>Secret the LankaQR / ComBank IPG confirm callback is verified against (D7' §4.2).</summary>
    /// <remarks>Same rule as <see cref="OnepayWebhookSecret"/>: unset means every callback is refused.</remarks>
    public string? LankaQrWebhookSecret { get; set; }

    /// <summary>
    /// The LankaQR merchant id (D7' §4.2's <c>LankaQr__MerchantId</c>), substituted into the templates.
    /// </summary>
    public string? LankaQrMerchantId { get; set; }

    // -------------------------------------------------------------------------------------------
    // Bounds and windows
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Smallest top-up this service will start.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> Rs 50 in minor units: below the cheapest daily fee, so a driver can
    /// always top up exactly what they need, and high enough that a gateway session is worth its fee.
    /// </remarks>
    [Range(1, 100_000_000)]
    public long MinTopupMinor { get; set; } = 5_000;

    /// <summary>
    /// Largest top-up this service will start.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> Rs 100,000 — an order of magnitude above the largest voucher (Rs 10,000),
    /// so bulk buying is unaffected, and a bound at all because a mistyped amount is otherwise a
    /// gateway session for a driver's life savings.
    /// </remarks>
    [Range(1, 1_000_000_000)]
    public long MaxTopupMinor { get; set; } = 10_000_000;

    /// <summary>
    /// How long a <c>Pending</c> gateway session stays open before it is treated as abandoned —
    /// D6' §7.1's "90 s pending window".
    /// </summary>
    /// <remarks>
    /// Nothing sweeps it: this service <b>reads</b> the window (a Pending session older than this is
    /// reported as such) and does not fail sessions on a timer, because a callback that arrives late is
    /// still a payment the driver made. The reconciling status poll §7.1 asks for needs a live OnePay
    /// API and is named in the C046 handoff rather than stubbed.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan TopupPendingWindow { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Largest transfer one driver may send another in a single operation.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> Equal to <see cref="MaxTopupMinor"/>: a transfer cannot move more than
    /// the platform will let a driver put in, which is the only bound that follows from anything.
    /// </remarks>
    [Range(1, 1_000_000_000)]
    public long MaxTransferMinor { get; set; } = 10_000_000;

    /// <summary>
    /// Rows one statement download may carry.
    /// </summary>
    /// <remarks><b>No spec.</b> A year of a busy driver's wallet is a few thousand lines.</remarks>
    [Range(1, 100_000)]
    public int MaxStatementRows { get; set; } = 10_000;

    // -------------------------------------------------------------------------------------------
    // The internal plane (D3' §0 — mTLS; a shared key until C042's mesh identity lands)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Shared secret for <c>/v1/internal/wallet/**</c>.
    /// </summary>
    /// <remarks>
    /// <b>Unset means the internal ledger routes are not mapped at all</b> — the convention every other
    /// internal family on this platform follows, and non-negotiable here: these two routes move money
    /// on behalf of a caller they cannot otherwise identify. subscription-svc (C047) cannot charge a
    /// daily fee without it, and says so at its own start-up.
    /// </remarks>
    public string? InternalApiKey { get; set; }
}
