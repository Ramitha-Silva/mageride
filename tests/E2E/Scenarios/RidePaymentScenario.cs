using System.Net;
using MageRide.E2E.Infrastructure;
using MageRide.Fare.Domain;
using MageRide.TestKit;

namespace MageRide.E2E.Scenarios;

/// <summary>
/// D-10 — how a Mode C fare is paid: cash, wallet, the driver's own QR, and what happens when it
/// goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three rails and no fourth</b> (AL-57/AL-59). <c>onepay</c> and platform-merchant
/// <c>lankaqr</c> were removed with the +5 % surcharge, because no ride fare may be charged to a
/// platform merchant account — so a passenger who wants to pay by card tops up their wallet, where
/// MageRide legitimately is the payee, and spends it with <c>method: "wallet"</c>. The mechanism is
/// absence: there is no value for either, which is what
/// <see cref="A_ride_fare_cannot_be_charged_to_a_platform_merchant_account"/> asserts.
/// </para>
/// <para>
/// <b>Only one of the three moves a ledger.</b> Cash and driver-QR settle outside the platform
/// entirely — the driver has the notes in their hand, or the transfer went bank-to-bank into their
/// own account — so the correct number of journal entries for both is zero, and that absence is
/// asserted rather than assumed. The wallet rail is one balanced <c>trip_payment</c> entry with two
/// wallet legs and no platform leg: the passenger's balance simply becomes the driver's, and
/// MageRide is the custodian rather than a party to the fare.
/// </para>
/// <para>
/// <b>Every fare here is charged for a journey somebody took.</b> The ride is booked through
/// ride-svc, dispatched by dispatch-svc, driven to completion and priced by fare-svc's own
/// <c>POST /v1/fare/calculate</c> — which this suite has to call because nothing in the platform
/// does (C120's finding, re-raised in the C123 handoff).
/// </para>
/// </remarks>
[Collection<MoneyCollection>]
[Trait("Category", "Money")]
public sealed class RidePaymentScenario(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
    : MoneyScenario(postgres, redis, redpanda)
{
    /// <summary>
    /// D5' §8.1 — the default rail. Cash settles on the tap and moves no ledger at all.
    /// </summary>
    /// <remarks>
    /// <c>FellBackToCash</c> is the cash <em>terminal</em> and not only the fallback: D5' §8.1 names
    /// cash as the default method and the payment CHECK has no separate "cash settled" value —
    /// <c>CashSettled</c> is a <em>ride</em> state, and ride-svc maps this onto it. A ride paid in
    /// cash and one that fell back reach the same payment state by design, and <c>method</c> is what
    /// tells them apart.
    /// </remarks>
    [Fact]
    public Task A_cash_fare_settles_on_the_tap_and_moves_no_ledger() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var paid = await fleet.PayAsync(ride.RideId, passenger))
            {
                await MoneyFleet.AssertOkAsync(paid, $"paying ride {ride.RideId} in cash");

                var body = await MoneyFleet.ReadJsonAsync(paid);

                Assert.Equal("FellBackToCash", body.GetProperty("state").GetString());
                Assert.Equal("cash", body.GetProperty("method").GetString());

                // Δ AL-57: no surviving rail carries a surcharge. The +5 % recovered OnePay's ~3 %
                // on the ride, and no ride rail touches an acquirer any more.
                Assert.Equal(0, body.GetProperty("surchargeMinor").GetInt64());
            }

            var payment = await fleet.ReadRidePaymentAsync(fare.PaymentId);

            Assert.Equal("FellBackToCash", payment.State);

            // P-04: the person in the vehicle hands over the notes, so the rider pays.
            Assert.Equal("rider", payment.PayerRole);
            Assert.Equal(passenger.Id, payment.PayerUserId);

            // R-05: the earning posts on the terminal, and only there.
            await fleet.UntilAsync(
                async () => (await fleet.ReadEarningsAsync(driver.DriverId)).Trips == 1,
                $"driver {driver.DriverId}'s earning posting on the cash terminal");

            var (_, gross) = await fleet.ReadEarningsAsync(driver.DriverId);
            Assert.Equal(payment.AmountMinor, gross);

            // And the whole point of the rail: nobody's ledger moved. The driver has the money.
            Assert.Null(await fleet.ReadAccountAsync("driver", driver.DriverId));
            Assert.Null(await fleet.ReadAccountAsync("passenger", passenger.Id));
            Assert.Null(await fleet.ReadEntryAsync($"trip_payment:{fare.PaymentId}"));
        });

    /// <summary>
    /// AL-57 — the wallet rail: one balanced entry, two wallet legs, no <c>Pending</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this cannot be two calls to the debit/credit seam.</b> Those post one wallet leg
    /// against the platform, so a fare would be a passenger debit and a driver credit in two
    /// entries — and a crash between them either creates money or destroys it. Here the two legs are
    /// one entry that Σ = 0 by construction, which is what "MageRide is the custodian rather than a
    /// party to the fare" means in rows.
    /// </para>
    /// <para>
    /// <b>P-04 is resolved from the method chosen at payment, not at booking.</b> A stored balance
    /// belongs to whoever booked and topped it up, so a wallet fare is charged to the booker — and
    /// this ride was booked <c>cash</c> and paid from the wallet, which is exactly the substitution
    /// the rule exists for.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_wallet_fare_moves_the_passengers_balance_to_the_driver_in_one_entry() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            await fleet.OpenPassengerBalanceAsync(passenger, 500_000);

            var platformBefore = (await fleet.ReadPlatformAccountAsync()).BalanceMinor;
            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var paid = await fleet.PayAsync(ride.RideId, passenger, "wallet"))
            {
                await MoneyFleet.AssertOkAsync(paid, $"paying ride {ride.RideId} from the passenger's wallet");

                var body = await MoneyFleet.ReadJsonAsync(paid);

                // No Pending. Every other rail waits on an acquirer; this one either moved the
                // ledger or it did not.
                Assert.Equal("Succeeded", body.GetProperty("state").GetString());
                Assert.Equal("wallet", body.GetProperty("method").GetString());
            }

            var payment = await fleet.ReadRidePaymentAsync(fare.PaymentId);

            Assert.Equal("Succeeded", payment.State);
            Assert.Equal("booker", payment.PayerRole);

            Assert.Equal(500_000 - payment.AmountMinor, await fleet.PassengerBalanceOfAsync(passenger.Id));
            Assert.Equal(payment.AmountMinor, await fleet.BalanceOfAsync(driver.DriverId));

            var entry = await fleet.ReadEntryAsync($"trip_payment:{fare.PaymentId}");

            Assert.True(
                entry is not null,
                "1101's header fixes the key as 'trip_payment:' || ride_payment_id, and wallet-svc composes "
                + "it rather than accepting one — a caller free to choose the key is free to pay one fare twice.");

            Assert.Equal("trip_payment", entry!.Kind);
            Assert.Equal(0, entry.SumMinor);
            Assert.Equal(2, entry.Legs.Count);

            Assert.DoesNotContain(
                entry.Legs,
                leg => leg.OwnerType is "platform" or "suspense");

            Assert.Equal(
                ["driver", "passenger"],
                entry.Legs.Select(leg => leg.OwnerType).Order(StringComparer.Ordinal));

            // The platform is not a party: its balance is exactly what it was before the fare.
            Assert.Equal(platformBefore, (await fleet.ReadPlatformAccountAsync()).BalanceMinor);

            await fleet.UntilAsync(
                async () => (await fleet.ReadEarningsAsync(driver.DriverId)).Trips == 1,
                "the driver's earning posting on the wallet terminal");

            // Tapping Pay again finds the payment settled rather than paying twice — and the ledger
            // key stands behind that even if the state check somehow did not.
            using var again = await fleet.PayAsync(ride.RideId, passenger, "wallet");

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("payment-already-settled", await MoneyFleet.ProblemCodeAsync(again));
            Assert.Equal(payment.AmountMinor, await fleet.BalanceOfAsync(driver.DriverId));
        });

    /// <summary>
    /// A short balance is answered, not silently turned into cash.
    /// </summary>
    /// <remarks>
    /// D5' §8.1 is explicit: a short balance is <c>402 insufficient-wallet</c> "with cash and
    /// driver-QR still offered rather than a silent fallback to cash". The passenger chose a rail
    /// and has to be told it is short — and the payment must stay open, because a payment closed as
    /// cash by a rail that failed would tell a driver they had been paid.
    /// </remarks>
    [Fact]
    public Task A_wallet_fare_a_passenger_cannot_cover_is_refused_and_leaves_the_payment_open() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var refused = await fleet.PayAsync(ride.RideId, passenger, "wallet"))
            {
                Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
                Assert.Equal("insufficient-wallet", await MoneyFleet.ProblemCodeAsync(refused));
            }

            Assert.Equal("Initiated", (await fleet.ReadRidePaymentAsync(fare.PaymentId)).State);
            Assert.Null(await fleet.ReadEntryAsync($"trip_payment:{fare.PaymentId}"));
            Assert.Equal(0, (await fleet.ReadEarningsAsync(driver.DriverId)).Trips);

            // Cash is still offered, and still works.
            using var cash = await fleet.PayAsync(ride.RideId, passenger);

            await MoneyFleet.AssertOkAsync(cash, "paying in cash after the wallet came up short");
            Assert.Equal("FellBackToCash", (await MoneyFleet.ReadJsonAsync(cash)).GetProperty("state").GetString());
        });

    /// <summary>
    /// <b>A gap, asserted as a gap:</b> AL-57's passenger wallet has no way to be funded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AL-57 retired D-11's per-driver merchant and moved card acceptance to the wallet top-up,
    /// "where MageRide IS the payee" — a passenger tops up and pays the ride with <c>wallet</c>.
    /// The paying half exists and works (see above). <b>The topping-up half does not.</b> Both rails
    /// are role-gated to <c>driver</c> and <c>fleet_owner</c>, so a passenger's bearer is refused;
    /// and the internal credit seam resolves accounts with <c>EnsureDriverAccountAsync</c>, so a
    /// passenger id posted there opens a <c>driver</c> account that the fare will not spend from —
    /// <c>ux_accounts_owner</c> is over <c>(owner_type, owner_id, currency)</c>, and the fare debits
    /// the <c>passenger</c> one.
    /// </para>
    /// <para>
    /// So on today's platform the <c>wallet</c> rail can only ever answer <c>402</c>, and card
    /// acceptance — the thing AL-57 exists to restore — does not work end to end. This test fails
    /// the day it is fixed, which is the point: it is a ledger entry with a test that it is still a
    /// gap, and <see cref="MoneyFleet.OpenPassengerBalanceAsync"/> is what this suite has to do
    /// instead. Recorded in the C123 handoff.
    /// </para>
    /// </remarks>
    [Fact]
    public Task No_route_on_this_platform_can_put_money_in_a_passengers_wallet() =>
        RunAsync(async (fleet, parties) =>
        {
            var passenger = await fleet.CreatePassengerAsync();
            parties.Add(passenger.Id);

            foreach (var rail in new[] { "onepay", "lankaqr" })
            {
                using var refused = await MoneyFleet.PostAsync(
                    fleet.WalletClient, $"/v1/wallet/topup/{rail}", new { amountMinor = 100_000 }, passenger.Bearer);

                Assert.True(
                    refused.StatusCode is HttpStatusCode.Forbidden,
                    $"POST /v1/wallet/topup/{rail} answered {(int)refused.StatusCode} for a passenger's bearer. "
                    + "If this has become a 200, AL-57's card rail now works end to end and this test — and "
                    + "MoneyFleet.OpenPassengerBalanceAsync — should be deleted.");
            }

            Assert.Null(await fleet.ReadAccountAsync("passenger", passenger.Id));

            // The seam subscription-svc and fare-svc use resolves a *driver* account, so it cannot
            // fund a passenger either: what it opens is an account of the wrong owner type that no
            // wallet fare will ever spend from.
            using (var seam = await fleet.PostLedgerAsync(
                passenger.Id,
                "credit",
                100_000,
                "adjustment",
                $"adjustment:e2e-{Guid.NewGuid():N}",
                "An attempt to fund a passenger through the internal seam"))
            {
                await MoneyFleet.AssertOkAsync(seam, "crediting a passenger id on the internal seam");
            }

            Assert.Null(await fleet.ReadAccountAsync("passenger", passenger.Id));

            var wrongType = await fleet.ReadAccountAsync("driver", passenger.Id);

            Assert.True(
                wrongType is not null && wrongType.BalanceMinor == 100_000,
                "The internal credit seam opened an owner_type='driver' account for a passenger id. That is "
                + "the shape of the gap: the money is real, it is on the wrong account, and the wallet fare "
                + "debits the passenger one.");
        });

    /// <summary>
    /// AL-47 — the driver's own QR: claim, confirm, and no ledger entry anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The passenger transfers bank-to-bank into the driver's own account, so the platform sees no
    /// callback, takes no commission and holds nothing — there is no gateway-verified
    /// <c>Succeeded</c> to be had and the settlement is an attestation instead. <b>The driver's
    /// confirm is valid without a claim and the claim settles nothing</b>, and that asymmetry is the
    /// design: the driver's bank app is the only party that actually saw the money, and the
    /// passenger's claim is evidence rather than proof.
    /// </para>
    /// <para>
    /// The absence of a ledger entry is asserted against a wallet-svc that is running and reachable,
    /// which is what makes it a proof rather than an assumption.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_driver_QR_fare_settles_by_attestation_and_never_touches_the_ledger() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var scanned = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/scan-driver-qr",
                new { rideId = ride.RideId.ToString(), qrPayload = "00020101021230..." },
                passenger.Bearer))
            {
                await MoneyFleet.AssertOkAsync(scanned, "scanning the driver's QR");

                var body = await MoneyFleet.ReadJsonAsync(scanned);

                // The method is recorded and nothing else moves: since AL-47 this no longer waits
                // for a webhook, because no gateway will ever tell us it happened.
                Assert.Equal("scan_driver_qr", body.GetProperty("method").GetString());
                Assert.Equal("Initiated", body.GetProperty("state").GetString());
            }

            using (var claimed = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/driver-qr/claim",
                new { rideId = ride.RideId.ToString() },
                passenger.Bearer))
            {
                // 202, not 200: the claim is recorded and the driver prompted, and nothing is
                // settled until they answer.
                await MoneyFleet.AssertStatusAsync(claimed, HttpStatusCode.Accepted, "the passenger's 'I've paid'");

                Assert.Equal(
                    "QrClaimedByPassenger",
                    (await MoneyFleet.ReadJsonAsync(claimed)).GetProperty("state").GetString());
            }

            // A claim settles nothing: the driver has not been paid as far as this platform knows.
            Assert.Equal(0, (await fleet.ReadEarningsAsync(driver.DriverId)).Trips);

            // And the driver cannot claim on the passenger's behalf — that would be attesting to
            // their own payment.
            using (var notTheirs = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/driver-qr/claim",
                new { rideId = ride.RideId.ToString() },
                driver.Bearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, notTheirs.StatusCode);
            }

            using (var confirmed = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/driver-qr/confirm",
                new { rideId = ride.RideId.ToString() },
                driver.Bearer))
            {
                await MoneyFleet.AssertOkAsync(confirmed, "the driver confirming the money arrived");

                Assert.Equal(
                    "DriverConfirmedQR",
                    (await MoneyFleet.ReadJsonAsync(confirmed)).GetProperty("state").GetString());
            }

            await fleet.UntilAsync(
                async () => (await fleet.ReadEarningsAsync(driver.DriverId)).Trips == 1,
                "the driver's earning posting on the AL-47 terminal");

            // The fence. wallet-svc is running and reachable, and it was never asked: the money went
            // bank-to-bank into the driver's own account and MageRide holds none of it.
            Assert.Null(await fleet.ReadAccountAsync("driver", driver.DriverId));
            Assert.Null(await fleet.ReadAccountAsync("passenger", passenger.Id));
            Assert.Null(await fleet.ReadEntryAsync($"trip_payment:{fare.PaymentId}"));
        });

    /// <summary>
    /// AL-47 / US-26.1 — an unanswered claim becomes a dispute that lands in the Finance queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C123's second definition-of-done item. <b>No wallet movement, because there is nothing to
    /// reverse</b> — the platform never held this money — so what Finance adjudicates on is the
    /// evidence, and the evidence is what this asserts: the ticket carries the ride, the amount, the
    /// state the payment was in and the instant the passenger claimed, all of it composed by
    /// fare-svc from the row rather than from anything the discloser typed.
    /// </para>
    /// <para>
    /// <c>Disputed</c> closes the payment and earns nothing (R-05): it is a terminal of the
    /// <em>ride</em> and not of the money — the fare is not the driver's until Finance says whose it
    /// is.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_driver_QR_dispute_lands_in_the_finance_queue_with_its_evidence() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var scanned = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/scan-driver-qr",
                new { rideId = ride.RideId.ToString(), qrPayload = "00020101021230..." },
                passenger.Bearer))
            {
                await MoneyFleet.AssertOkAsync(scanned, "scanning the driver's QR");
            }

            using (var claimed = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/driver-qr/claim",
                new { rideId = ride.RideId.ToString() },
                passenger.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(claimed, HttpStatusCode.Accepted, "the passenger's claim");
            }

            Guid ticketId;

            // The driver disputes: their bank app never showed the transfer.
            using (var disputed = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/fare/pay/driver-qr/dispute",
                new { rideId = ride.RideId.ToString(), note = "No transfer arrived in my account." },
                driver.Bearer))
            {
                await MoneyFleet.AssertStatusAsync(disputed, HttpStatusCode.Created, "the driver disputing the claim");
                ticketId = (await MoneyFleet.ReadJsonAsync(disputed)).GetProperty("ticketId").GetGuid();
            }

            var payment = await fleet.ReadRidePaymentAsync(fare.PaymentId);
            Assert.Equal("Disputed", payment.State);

            var ticket = await fleet.ReadSupportTicketAsync(ticketId);

            Assert.True(ticket is not null, $"The dispute answered with ticket {ticketId} and no such row exists.");
            Assert.Equal(driver.DriverId, ticket!.UserId);
            Assert.Equal(ride.RideId, ticket.RideId);
            Assert.Equal("OPEN", ticket.Status);

            // The evidence Finance adjudicates on, composed from the payment row.
            Assert.Contains(payment.AmountMinor.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                ticket.Description, StringComparison.Ordinal);
            Assert.Contains("claimed at", ticket.Description, StringComparison.Ordinal);
            Assert.Contains("No transfer arrived in my account.", ticket.Description, StringComparison.Ordinal);

            // R-05: Disputed closes the payment and earns nothing.
            Assert.Equal(0, (await fleet.ReadEarningsAsync(driver.DriverId)).Trips);

            // And still no ledger entry: the platform never held this money, so there is nothing on
            // either side to reverse.
            Assert.Null(await fleet.ReadEntryAsync($"trip_payment:{fare.PaymentId}"));
        });

    /// <summary>
    /// E-05 — the refund workflow, and <b>a defect asserted as a defect</b>: its ledger leg cannot
    /// post.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>fares.refunds</c> is the Finance queue.</b> <c>ix_refunds_open</c> (migration 1003) is a
    /// partial index over the unsettled statuses ordered by <c>requested_at</c>, and its own comment
    /// names SCR-AP-009 — so a refund becoming visible to Finance is a row landing there, not an
    /// event and not a screen fare-svc owns. That half works, and so do the two guards in front of
    /// it: the role gate and the ceiling.
    /// </para>
    /// <para>
    /// <b>The money half does not.</b> <c>RefundService.PostReversalAsync</c> posts the reversal
    /// through <c>POST /v1/internal/wallet/{driverId}/<u>debit</u></c> with
    /// <c>kind = "payment_refund"</c> — and <c>payment_refund</c> is in wallet-svc's
    /// <c>InternalCreditKinds</c>, not its debit whitelist. So wallet-svc answers <c>400</c>,
    /// fare-svc's client turns a non-<c>402</c> failure into <c>dependency-unavailable</c>, and the
    /// route answers <b><c>503</c> — after the refund row and the payment's own transition have
    /// already committed</b>. Finance is told the request failed while the payment reads
    /// <c>PartiallyRefunded</c> and a <c>Requested</c> row sits on the queue with no money behind it.
    /// </para>
    /// <para>
    /// This test drives it and asserts exactly that, because the alternative — softening it to "a
    /// refund answers 503" without saying what did commit — would hide which half of a split
    /// operation survived. <b>It fails the day the whitelist and the direction are reconciled</b>,
    /// which is the fix: either <c>payment_refund</c> joins the debit kinds, or the reversal credits
    /// the party being refunded, and D5' §11.14 draws the second (<c>DR platform / CR passenger</c>).
    /// Raised in the C123 handoff with fare-svc named as the owner.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_refund_raises_the_finance_queue_row_and_its_ledger_leg_cannot_post() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            var finance = await fleet.CreateFinanceOfficerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            await fleet.OpenPassengerBalanceAsync(passenger, 500_000);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var paid = await fleet.PayAsync(ride.RideId, passenger, "wallet"))
            {
                await MoneyFleet.AssertOkAsync(paid, "paying the fare from the wallet");
            }

            var payment = await fleet.ReadRidePaymentAsync(fare.PaymentId);
            var earnedBefore = await fleet.ReadEarningsAsync(driver.DriverId);
            var driverBefore = await fleet.BalanceOfAsync(driver.DriverId);
            var passengerBefore = await fleet.PassengerBalanceOfAsync(passenger.Id);

            // Only Finance may. A passenger asking for their own money back is a rider-initiated
            // dispute (§11.14's second paragraph), which is a different door.
            using (var notFinance = await MoneyFleet.PostAsync(
                fleet.FareClient,
                "/v1/admin/fare/refund",
                new
                {
                    paymentId = fare.PaymentId.ToString(),
                    kind = "full",
                    amountMinor = payment.AmountMinor,
                    reasonCode = "passenger_request",
                },
                passenger.Bearer))
            {
                Assert.Equal(HttpStatusCode.Forbidden, notFinance.StatusCode);
            }

            // A payment cannot be refunded for more than it took. Checked before anything is
            // written, so this one refuses cleanly.
            using (var tooMuch = await fleet.RefundAsync(
                finance, fare.PaymentId, "partial", payment.AmountMinor + 1, "double_dip"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
                Assert.Equal("invalid-amount", await MoneyFleet.ProblemCodeAsync(tooMuch));
            }

            Assert.Empty(await fleet.ReadRefundsAsync(fare.PaymentId));

            using (var refunded = await fleet.RefundAsync(
                finance, fare.PaymentId, "partial", 5_000, "route_shorter_than_quoted"))
            {
                Assert.True(
                    refunded.StatusCode == HttpStatusCode.ServiceUnavailable,
                    $"Finance's refund answered {(int)refunded.StatusCode}. If this has become a 201, "
                    + "PostReversalAsync's `payment_refund` debit is now accepted by wallet-svc's kind "
                    + "whitelist and E-05 posts its ledger leg — delete this ratchet and assert the "
                    + "balanced reversal instead.");

                Assert.Equal("dependency-unavailable", await MoneyFleet.ProblemCodeAsync(refunded));
            }

            // What committed anyway, before the hop that failed: the payment moved and the queue row
            // landed. Both are correct on their own; together with the 503 they are a split
            // operation Finance has to reconcile by hand.
            Assert.Equal("PartiallyRefunded", (await fleet.ReadRidePaymentAsync(fare.PaymentId)).State);

            var raised = Assert.Single(await fleet.ReadRefundsAsync(fare.PaymentId));

            Assert.Equal("partial", raised.Kind);
            Assert.Equal(5_000, raised.AmountMinor);
            Assert.Equal("Requested", raised.Status);
            Assert.Equal("route_shorter_than_quoted", raised.ReasonCode);
            Assert.Equal(finance.Id, raised.RequestedBy);

            // And what did not: no money moved in either direction, on either side.
            Assert.Empty(await fleet.ReadEntriesForAsync("driver", driver.DriverId, "payment_refund"));
            Assert.Empty(await fleet.ReadEntriesForAsync("passenger", passenger.Id, "payment_refund"));
            Assert.Equal(driverBefore, await fleet.BalanceOfAsync(driver.DriverId));
            Assert.Equal(passengerBefore, await fleet.PassengerBalanceOfAsync(passenger.Id));

            // A refund never un-earns a driver: the rollup is a read model of what was collected,
            // and putting a reversal through it would make yesterday's Earnings screen change
            // overnight. Finance reconciles against the ledger instead.
            Assert.Equal(earnedBefore, await fleet.ReadEarningsAsync(driver.DriverId));
        });

    /// <summary>
    /// <b>A gap, asserted as a gap:</b> R-19's late callback has no door left to arrive through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §11.14 is a complete workflow — a provider <c>Succeeded</c> arriving after a ride settled in
    /// cash becomes <c>Overpaid</c>, raises an <c>overpaid_reversal</c> on the Finance queue, and is
    /// refunded. <b>AL-57/AL-59 removed the only way it can start.</b> The two ride-side provider
    /// callbacks were deleted with the ride gateways ("REMOVED, do not re-add" at the line), because
    /// no ride fare reaches an acquirer any more — so nothing on this platform can deliver a late
    /// <c>Succeeded</c>, and <c>Overpaid</c> is unreachable.
    /// </para>
    /// <para>
    /// The machine still carries the transitions, deliberately: historical rows are in those states
    /// and D5' §8.1 still describes them. So this asserts three things that are all currently true
    /// and that would each break in a different way if the rail came back — the edges exist in the
    /// machine, nothing can fire them, and a cash-settled payment is closed against a refund.
    /// Recorded in the C123 handoff, and it is the one deliverable of this component that could not
    /// be driven end to end.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_late_gateway_callback_cannot_reach_a_settled_cash_fare_because_no_ride_rail_has_one() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            var finance = await fleet.CreateFinanceOfficerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var paid = await fleet.PayAsync(ride.RideId, passenger))
            {
                await MoneyFleet.AssertOkAsync(paid, "settling the ride in cash");
            }

            Assert.Equal("FellBackToCash", (await fleet.ReadRidePaymentAsync(fare.PaymentId)).State);

            // (1) D5' §8.1's edges are still in the machine, both of them.
            Assert.Contains(
                PaymentStateMachine.All,
                transition => transition.From == RidePaymentStates.FellBackToCash
                              && transition.Trigger == PaymentTrigger.LateGatewaySucceeded
                              && transition.To == RidePaymentStates.Overpaid);

            Assert.Contains(
                PaymentStateMachine.All,
                transition => transition.From == RidePaymentStates.Overpaid
                              && transition.Trigger == PaymentTrigger.RefundedInFull
                              && transition.To == RidePaymentStates.Refunded);

            // (2) Nothing on fare-svc's surface can fire the first of them. The two callbacks §11.14
            // draws were removed by AL-57/AL-59 and there is no anonymous route on the payment
            // group at all.
            foreach (var removed in new[] { "onepay/webhook", "lankaqr/confirm", "callback" })
            {
                using var gone = await fleet.Acquirer.ConfirmAsync(
                    fleet.FareCallbackUrl(removed),
                    MoneyFleet.OnepayWebhookSecret,
                    new
                    {
                        providerTransactionId = $"late-{Guid.NewGuid():N}",
                        paymentId = fare.PaymentId.ToString(),
                        status = "SUCCESS",
                        amountMinor = fare.AmountMinor,
                    });

                Assert.True(
                    gone.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                        or HttpStatusCode.Unauthorized,
                    $"POST /v1/fare/pay/{removed} answered {(int)gone.StatusCode}. AL-57/AL-59 removed the ride "
                    + "gateway callbacks; if one has come back, R-19's late-callback path is reachable again "
                    + "and this test should become a real Overpaid → Refunded drive.");
            }

            Assert.Equal("FellBackToCash", (await fleet.ReadRidePaymentAsync(fare.PaymentId)).State);

            // (3) And the cash settlement stands: a payment in a cash terminal is closed, so Finance
            // cannot refund it either. §11.14's own note — "UPDATE rides SET state='Disputed' is NOT
            // done" — describes a workflow that begins at Overpaid, and there is no way in.
            using var refused = await fleet.RefundAsync(
                finance, fare.PaymentId, "full", fare.AmountMinor, "late_callback_after_cash");

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("payment-already-settled", await MoneyFleet.ProblemCodeAsync(refused));
            Assert.Empty(await fleet.ReadRefundsAsync(fare.PaymentId));
        });

    /// <summary>
    /// E-10 — an optional post-trip tip is credited directly to the driver's wallet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one movement a <em>cash</em> fare makes, and the only reason a cash-paying passenger's
    /// ride touches the ledger at all: the fare itself went hand to hand, and the tip is a credit the
    /// platform hands the driver against its own account. Keyed <c>tip_payout:{ridePaymentId}</c>, so
    /// a settlement retried after the commit pays one tip.
    /// </para>
    /// <para>
    /// <b>It is posted after the commit, and a failure there does not fail the caller.</b> The
    /// passenger has paid; a 500 would invite a retry that finds the payment terminal. So the
    /// assertion waits for the credit rather than reading it out of the response — the hop is real
    /// and it is the last thing to happen.
    /// </para>
    /// </remarks>
    [Fact]
    public Task A_tip_is_credited_straight_to_the_drivers_wallet() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var platformBefore = (await fleet.ReadPlatformAccountAsync()).BalanceMinor;
            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            using (var paid = await fleet.PayAsync(ride.RideId, passenger, "cash", tipMinor: 20_000))
            {
                await MoneyFleet.AssertOkAsync(paid, "paying in cash with a tip");
            }

            // The initiation response carries no tip — `PaymentInitiationResponse` has no member for
            // one, and the status shape does. Read from the row instead, which is what the poll
            // renders anyway.
            Assert.Equal(20_000, (await fleet.ReadRidePaymentAsync(fare.PaymentId)).TipAmountMinor);

            await fleet.UntilAsync(
                async () => await fleet.BalanceOfAsync(driver.DriverId) == 20_000,
                $"the tip on ride {ride.RideId} reaching driver {driver.DriverId}'s wallet");

            var entry = await fleet.ReadEntryAsync($"tip_payout:{fare.PaymentId}");

            Assert.True(entry is not null, "The tip is keyed on the ride payment, so a retried settlement pays one.");
            Assert.Equal("tip_payout", entry!.Kind);
            Assert.Equal(0, entry.SumMinor);

            // The platform is the counterparty — this is a credit it hands out, exactly like a
            // top-up, and unlike a wallet fare it is not a movement between two wallets.
            Assert.Equal(platformBefore - 20_000, (await fleet.ReadPlatformAccountAsync()).BalanceMinor);

            // The tip is not the fare: the earning rollup carries what the ride was priced at.
            var (_, gross) = await fleet.ReadEarningsAsync(driver.DriverId);
            Assert.Equal((await fleet.ReadRidePaymentAsync(fare.PaymentId)).AmountMinor, gross);
        });

    /// <summary>
    /// AL-57/AL-59 — no ride fare may be charged to a platform merchant account.
    /// </summary>
    /// <remarks>
    /// The mechanism is absence rather than a check that could be relaxed: <c>PayableMethods</c> has
    /// no value for <c>onepay</c> or platform-merchant <c>lankaqr</c>, and <c>fare.yaml</c>'s enum
    /// has none. <c>cod</c> is refused for its own reason — it is a booking-time choice (P-08) and
    /// settles through ride-svc at the door, not by a passenger tapping Pay.
    /// </remarks>
    [Fact]
    public Task A_ride_fare_cannot_be_charged_to_a_platform_merchant_account() =>
        RunAsync(async (fleet, parties) =>
        {
            var driver = await fleet.CreateDriverAsync();
            var passenger = await fleet.CreatePassengerAsync();
            parties.AddRange(driver.DriverId, passenger.Id);

            var (ride, fare) = await fleet.PayableRideAsync(passenger, driver);

            foreach (var retired in new[] { "onepay", "lankaqr", "cod", "bank_transfer" })
            {
                using var refused = await fleet.PayAsync(ride.RideId, passenger, retired);

                Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
                Assert.Equal("payment-method-invalid", await MoneyFleet.ProblemCodeAsync(refused));
            }

            Assert.Equal("Initiated", (await fleet.ReadRidePaymentAsync(fare.PaymentId)).State);
            Assert.Equal(0, (await fleet.ReadEarningsAsync(driver.DriverId)).Trips);
        });
}
