using System.ComponentModel.DataAnnotations;

namespace MageRide.Fare.Configuration;

/// <summary>fare-svc's settings. The section is <c>Fare</c>.</summary>
/// <remarks>
/// <c>Fare:EstimateTokenKey</c> and <c>Fare:EstimateTokenTtl</c> are <b>not</b> here — they belong
/// to <c>MageRide.Shared.Fares.FareEstimateTokenOptions</c>, because ride-svc binds the same section
/// to verify what this service signs. One section, two readers, one key.
/// </remarks>
public sealed class FareOptions
{
    public const string SectionName = "Fare";

    // -------------------------------------------------------------------------------------------
    // Distance
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Multiplier applied to the straight-line pickup→dropoff distance to approximate the road
    /// distance an estimate is priced on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An interim, and it says so.</b> D5' §1.2 prices the upfront estimate on the OSRM/Valhalla
    /// <em>route</em> distance, and ADD §7.6 puts routing in Phase 3 — there is no road network on
    /// this platform to measure a path against yet. The alternatives were to quote the straight line,
    /// which under-charges every ride by whatever the detour is, or to state the method and make it a
    /// setting. This is the second.
    /// </para>
    /// <para>
    /// <b>1.3 deliberately matches <c>Query:EtaDetourFactor</c>.</b> Two services approximating the
    /// same road network with two different constants would put a different detour in the ETA and in
    /// the price of one journey. Retune them together, and delete both when the router lands.
    /// </para>
    /// </remarks>
    [Range(1.0, 3.0)]
    public double RouteDetourFactor { get; set; } = 1.3;

    /// <summary>
    /// Positions read for one ride's Kalman track.
    /// </summary>
    /// <remarks>
    /// <b>No spec</b> — a bound, not a working limit. A one-second tracker running for four hours
    /// fits inside it; beyond that the ride is not a ride.
    /// </remarks>
    [Range(100, 200_000)]
    public int MaxTrackSamples { get; set; } = 20_000;

    /// <summary>E-04's filter tuning. Each field is argued at its declaration.</summary>
    public Distance.KalmanTrackOptions Kalman { get; set; } = new();

    // -------------------------------------------------------------------------------------------
    // Settlement
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Apply the D-05 cross-trip Rs 50 cancellation settlement on a completed trip. On.
    /// </summary>
    /// <remarks>
    /// <b>Off ⇒ a passenger who cancelled after an accept never pays the Rs 50 and the driver they
    /// stood up is never compensated.</b> The debt keeps accruing on
    /// <c>dispatch.cancellation_penalties</c> and nothing collects it. Announced at start-up.
    /// </remarks>
    public bool PenaltySettlementEnabled { get; set; } = true;

    /// <summary>
    /// Base URL of dispatch-svc, which owns <c>dispatch.cancellation_penalties</c> (D5' §7.1).
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ no penalty is ever read or settled</b> and every completed trip is priced without
    /// the outstanding balance. The fare is still correct for the trip itself, which is why this
    /// degrades rather than refuses.
    /// </remarks>
    public string? DispatchBaseUrl { get; set; }

    /// <summary>The interim shared secret dispatch-svc's internal plane demands.</summary>
    public string? DispatchInternalApiKey { get; set; }

    /// <summary>Base URL of wallet-svc — the D-09 ledger seam the penalty settlement posts through.</summary>
    /// <remarks>
    /// <b>Unset ⇒ the penalty is added to the fare but never moves between wallets.</b> That is the
    /// worse half to lose, so the settlement is skipped entirely rather than half-applied when this
    /// is missing — see <c>PenaltySettlementService</c>.
    /// </remarks>
    public string? WalletBaseUrl { get; set; }

    /// <summary>The interim shared secret wallet-svc's internal ledger plane demands.</summary>
    public string? WalletInternalApiKey { get; set; }

    /// <summary>
    /// Base URL of ride-svc — R-05's settlement hop (Δ C050).
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ no ride ever leaves <c>PaymentPending</c>.</b> ride-svc is the sole writer of
    /// <c>rides.state</c> (R-01) and nothing but a terminal payment may settle a ride, so this hop
    /// is the only way the two facts meet. Announced at start-up.
    /// </remarks>
    public string? RideBaseUrl { get; set; }

    /// <summary>The interim shared secret ride-svc's internal plane demands (its `Ride:InternalApiKey`).</summary>
    public string? RideInternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // Gateways (D-10, D-12, Δ C050)
    // -------------------------------------------------------------------------------------------

    // -------------------------------------------------------------------------------------------
    // Δ AL-57/AL-59 — REMOVED, do not re-add:
    //
    //   OnepaySurchargeBps   the +5% recovered OnePay's ~3% on the ride. No ride rail touches an
    //                        acquirer any more, so there is no fee on a ride to recover; OnePay's
    //                        cost is borne on the wallet top-up, where MageRide is the payee.
    //   OnepayBaseUrl · OnepayApiKey · OnepayWebhookSecret
    //   LankaQrWebhookSecret · LankaQrMerchantId · LankaQrDeepLinkTemplate
    //                        the two ride gateways are gone. OnePay has one merchant account per
    //                        merchant, so a card fare could only ever land in MageRide's own
    //                        account; the LankaQR ride rail pointed at the platform's own merchant
    //                        while crediting the driver nothing but a read-model row.
    //
    // The equivalents that survive are wallet-svc's — `Onepay:*` and `LankaQr:*` there settle a
    // TOP-UP, which is the one case a single merchant account serves correctly.

    // -------------------------------------------------------------------------------------------
    // AL-47 driver-QR attestation (Δ C050)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How long a passenger's driver-QR claim may sit unconfirmed before the driver is nudged
    /// (US-26.1's "+5 min").
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan QrNudgeAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Runs the AL-47 nudge sweep in this process. On.</summary>
    /// <remarks>
    /// <b>Off ⇒ a driver who missed the first prompt is never reminded</b>, and a passenger who paid
    /// bank-to-bank waits for a confirmation that has to be chased by hand.
    /// </remarks>
    public bool QrNudgeEnabled { get; set; } = true;

    /// <summary>How often the sweep runs.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan QrNudgeInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Claims nudged per pass. A bound, not a working limit.</summary>
    [Range(1, 10_000)]
    public int QrNudgeBatchSize { get; set; } = 200;

    /// <summary>Timeout for an internal hop. D6' §8.3 budgets one at 2 s.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan InternalTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Guards <c>/v1/internal/fare/**</c> until C042's mesh identity lands.
    /// </summary>
    /// <remarks>
    /// <b>Unset ⇒ <c>POST /v1/fare/calculate</c> is not mapped at all.</b> D3' puts it on mTLS
    /// internal and every completed ride goes through it, so an unauthenticated caller who could
    /// reach it could price somebody else's journey. ride-svc must present this value.
    /// </remarks>
    public string? InternalApiKey { get; set; }
}
