using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapper;
using MageRide.Fare.Configuration;
using MageRide.Fare.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Gateways;

/// <summary>What a gateway hands back for the app to open.</summary>
/// <param name="PaymentLink">AL-15's "Pay" deep link into the bank app; the QR is the fallback.</param>
public sealed record GatewaySession(
    string? RedirectUrl, string? SessionToken, string? PaymentLink, string? QrPayload)
{
    public static readonly GatewaySession None = new(null, null, null, null);
}

/// <summary>Opens a payment session for one ride fare.</summary>
/// <remarks>
/// <b>Deliberately not wallet-svc's <c>IPaymentGateway</c>, and the difference is D-11.</b> A wallet
/// top-up is money moving to the platform; a ride fare is money moving to a *driver*, which needs
/// that driver's OnePay merchant binding (<c>registry.driver_payouts</c>) or there is nowhere for it
/// to land — <c>402 merchant-not-onboarded</c>. The two also disagree on the fallback: a failed
/// top-up is a 503 and the driver picks the other rail, while a failed ride payment falls back to
/// cash (D6' §7.1). Promoting a common client into the kernel is worth doing when a third caller
/// appears; raised in the C050 handoff.
/// </remarks>
// Δ AL-57/AL-59 — REMOVED, do not re-add:
//   IFareGateway, OnepayFareGateway, LankaQrFareGateway
//     No ride fare reaches an acquirer any more. OnePay supports one merchant account per merchant,
//     so a card fare could only ever land in MageRide's own account (AL-57), and the LankaQR ride
//     rail pointed at the platform's own merchant while crediting the driver nothing but a
//     `fares.driver_earnings` read-model row (AL-59). The surviving rails are cash, `wallet` — one
//     balanced ledger entry through wallet-svc — and `scan_driver_qr`, the driver's OWN bank QR,
//     which settles by AL-47 attestation because money moving into somebody else's bank produces no
//     platform webhook at all.
//   IDriverPayoutRepository / DriverPayoutRepository
//     D-11's per-driver OnePay merchant binding, which never existed. `registry.driver_payouts` is
//     dropped by migration 1008; where a driver's money goes is now
//     `registry.driver_payout_profiles` (AL-58) and the weekly payout run.
//
// `GatewaySession` above survives as `GatewaySession.None`: `InitiatedPayment` still carries the
// shape so `fare.yaml`'s response does not change shape for a client that reads it, and every
// surviving rail answers None.
