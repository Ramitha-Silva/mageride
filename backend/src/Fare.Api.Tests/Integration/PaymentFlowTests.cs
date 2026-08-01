using System.Net;
using MageRide.Fare.Endpoints;
using MageRide.Fare.Persistence;
using MageRide.Fare.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fare.Tests.Integration;

/// <summary>
/// D-10's machine end to end: every settlement path, R-19's late callback, and E-05's refund.
/// </summary>
[Collection<FareCollection>]
public sealed class PaymentFlowTests(PostgresFixture postgres)
{
    // -----------------------------------------------------------------------------------------
    // Δ AL-57 — three tests REMOVED with the behaviour they described:
    //
    //   A_duplicate_gateway_callback_settles_once
    //   A_late_success_after_cash_becomes_an_overpayment_on_the_finance_queue
    //   A_failed_payment_can_be_retried_on_a_new_attempt
    //
    // All three needed a ride payment to reach `Pending` or `Failed`, and both states were only ever
    // reached by a provider callback. No ride fare touches an acquirer any more: a `wallet` payment
    // is a ledger move that either happens or is refused before any row changes, and cash and
    // driver-QR never had a provider. The states, the R-19 dedupe and the retry chain remain in the
    // machine — `PaymentStateMachineTests` still asserts them against D5' §8.1, and historical rows
    // carry them — but nothing on this surface can reach them, so a test that drove one would be
    // testing its own fixture.
    // -----------------------------------------------------------------------------------------

    /// <summary>Prices a completed ride the way ride-svc does, so there is a fare to pay.</summary>
    private static async Task<(SeededRide Ride, Guid PaymentId, long AmountMinor)> PricedAsync(FareHarness harness)
    {
        var ride = await harness.Seed.RideAsync();

        var fare = await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 6.0), "calculate");

        return (ride, fare.PaymentId, fare.AmountMinor);
    }

    /// <summary>Cash is the default and the driver already has the money — it settles on the tap.</summary>
    [Fact]
    public async Task A_cash_payment_settles_immediately_and_earns_the_driver()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, amountMinor) = await PricedAsync(harness);

        var paid = await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay",
                new { rideId = ride.RideId.ToString(), method = "cash" },
                harness.Tokens.Passenger(ride.PassengerId)),
            "pay cash");

        Assert.Equal("FellBackToCash", paid.State);
        Assert.Equal("cash", paid.Method);
        Assert.Equal(0, paid.SurchargeMinor);

        // R-05: the earning posts because the payment closed.
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));

        // …and ride-svc is told, so the ride can leave PaymentPending.
        var settle = Assert.Single(downstream.Settlements);
        Assert.Equal("FellBackToCash", settle.String("paymentState"));
    }

    /// <summary>
    /// AL-57/AL-59: <b>no ride fare on this surface can reach a platform merchant account</b>, and
    /// no surviving rail is surcharged.
    /// </summary>
    /// <remarks>
    /// The +5% existed to recover OnePay's ~3% on the ride. OnePay now sits on the wallet top-up,
    /// where MageRide is the payee, so there is no acquirer fee on a ride to recover — and the two
    /// methods that reached a platform merchant are not values this route accepts.
    /// </remarks>
    [Fact]
    public async Task Neither_platform_merchant_rail_is_a_payment_method_and_nothing_is_surcharged()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        foreach (var retired in new[] { "onepay", "lankaqr" })
        {
            using var refused = await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = retired }, passenger);

            var (code, _) = await FareHarness.ProblemAsync(refused);

            // 402, not 400: `payment-method-invalid` is "this rail is not usable for this ride",
            // which is what the app branches on to offer the passenger another one.
            Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
            Assert.Equal("payment-method-invalid", code);
        }

        // Nothing was charged by either refusal, and the surviving card rail carries no surcharge.
        Assert.Empty(downstream.LedgerPostings);

        var paid = await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "wallet" }, passenger),
            "pay from the wallet");

        Assert.Equal(0, paid.SurchargeMinor);
        Assert.Equal(amountMinor, paid.AmountMinor);
    }


    /// <summary>
    /// Definition of done: a driver-QR confirm posts the driver earning and closes the ride payment
    /// <b>with no money movement through MageRide</b>.
    /// </summary>
    [Fact]
    public async Task A_driver_qr_confirm_settles_without_moving_any_money_through_the_platform()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);

        await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync(
                "/v1/fare/pay/scan-driver-qr",
                new { rideId = ride.RideId.ToString(), qrPayload = "bank-qr-payload" },
                harness.Tokens.Passenger(ride.PassengerId)),
            "scan the driver's QR");

        var claimed = await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync(
                "/v1/fare/pay/driver-qr/claim",
                new { rideId = ride.RideId.ToString() },
                harness.Tokens.Passenger(ride.PassengerId)),
            "claim");

        Assert.Equal("QrClaimedByPassenger", claimed.State);
        Assert.Null(await harness.EarningsAsync(ride.DriverId));

        var confirmed = await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync(
                "/v1/fare/pay/driver-qr/confirm",
                new { rideId = ride.RideId.ToString() },
                harness.Tokens.Driver(ride.DriverId)),
            "confirm");

        Assert.Equal("DriverConfirmedQR", confirmed.State);
        Assert.Equal(paymentId, confirmed.PaymentId);

        // The earning posts (R-05) and the ride is closed…
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
        Assert.Equal("DriverConfirmedQR", Assert.Single(downstream.Settlements).String("paymentState"));

        // …and the fence: the money went bank-to-bank, so the ledger seam was never touched.
        Assert.Empty(downstream.LedgerPostings);
    }

    /// <summary>AL-47: only the driver may confirm, and only the payer may claim.</summary>
    [Fact]
    public async Task Only_the_driver_confirms_and_only_the_payer_claims()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, _) = await PricedAsync(harness);

        // The driver cannot claim to have paid themselves.
        using (var response = await harness.PostAsync(
            "/v1/fare/pay/driver-qr/claim",
            new { rideId = ride.RideId.ToString() },
            harness.Tokens.Driver(ride.DriverId)))
        {
            var (code, _) = await FareHarness.ProblemAsync(response);
            Assert.Equal("not-ride-participant", code);
        }

        // The passenger cannot confirm their own payment arrived.
        using (var response = await harness.PostAsync(
            "/v1/fare/pay/driver-qr/confirm",
            new { rideId = ride.RideId.ToString() },
            harness.Tokens.Passenger(ride.PassengerId)))
        {
            var (code, _) = await FareHarness.ProblemAsync(response);
            Assert.Equal("not-ride-participant", code);
        }
    }

    /// <summary>
    /// AL-47's dispute: a Support ticket that routes to Finance, and no wallet movement — the
    /// platform never held this money, so there is nothing to reverse.
    /// </summary>
    [Fact]
    public async Task A_driver_qr_dispute_opens_a_ticket_and_moves_nothing()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, _) = await PricedAsync(harness);

        await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync(
                "/v1/fare/pay/driver-qr/claim",
                new { rideId = ride.RideId.ToString() },
                harness.Tokens.Passenger(ride.PassengerId)),
            "claim");

        var ticket = await harness.OkAsync<DisputeTicketResponse>(
            await harness.PostAsync(
                "/v1/fare/pay/driver-qr/dispute",
                new { rideId = ride.RideId.ToString(), note = "Driver says it never arrived." },
                harness.Tokens.Passenger(ride.PassengerId)),
            "dispute");

        var raised = Assert.Single(await harness.TicketsAsync(SupportTicketRepository.DriverQrCategory));
        Assert.Equal(ticket.TicketId, raised.Id);
        Assert.Contains("never arrived", raised.Description, StringComparison.Ordinal);

        // Disputed closes the payment and earns nothing (R-05).
        Assert.Null(await harness.EarningsAsync(ride.DriverId));
        Assert.Empty(downstream.LedgerPostings);
    }


    /// <summary>An unsigned or wrongly signed callback settles nothing.</summary>
    [Fact]
    public async Task An_unsigned_callback_is_refused()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, _) = await PricedAsync(harness);

        var callback = new
        {
            providerTransactionId = "ONEPAY-C050-0002",
            paymentId = paymentId.ToString(),
            status = "SUCCESS",
        };

        using (var wrong = await harness.PostSignedAsync(
            "/v1/fare/pay/onepay/webhook", callback, "the-wrong-secret"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Null(await harness.EarningsAsync(ride.DriverId));
        Assert.Empty(downstream.Settlements);
    }


    /// <summary>
    /// Definition of done: a refund leaves the ledger balanced and is visible in the Finance queue.
    /// </summary>
    /// <remarks>
    /// "Balanced" is wallet-svc's invariant — a DB trigger over <c>billing.journal_postings</c> — so
    /// what is asserted here is that fare-svc posts <b>through the seam that enforces it</b>, with
    /// the <c>payment_refund</c> kind and a key composed from the refund row. C050's handoff records
    /// booting the real wallet-svc as the stronger form.
    /// </remarks>
    [Fact]
    public async Task A_refund_posts_through_the_ledger_seam_and_lands_on_the_finance_queue()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        // Δ AL-57: the wallet rail settles on the spot — one balanced ledger entry in
        // wallet-svc, no gateway and therefore no callback to wait for.
        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "wallet" }, passenger),
            "pay from the wallet");

        var financeUser = await harness.Seed.UserAsync("finance_officer");

        var refund = await harness.OkAsync<RefundResponse>(
            await harness.PostAsync(
                "/v1/admin/fare/refund",
                new
                {
                    paymentId = paymentId.ToString(),
                    kind = "partial",
                    amountMinor = 10_000,
                    reasonCode = "route_dispute",
                },
                harness.Tokens.Finance(financeUser)),
            "refund");

        Assert.Equal("Requested", refund.Status);
        Assert.Equal(10_000, refund.AmountMinor);

        var queued = Assert.Single(await harness.RefundQueueAsync());
        Assert.Equal(refund.RefundId, queued.Id);
        Assert.Equal("partial", queued.Kind);

        // Δ AL-57: the wallet fare is itself a ledger movement now, so the seam carries two calls —
        // the `trip-payment` that settled the ride and the refund that gave part of it back. The
        // refund is the one this test is about.
        var posting = Assert.Single(
            downstream.LedgerPostings, call => call.Path.EndsWith("/debit", StringComparison.Ordinal));

        Assert.Equal("payment_refund", posting.String("kind"));
        Assert.Equal(10_000, posting.Number("amountMinor"));
        Assert.Equal($"payment_refund:{refund.RefundId}", posting.String("idempotencyKey"));

        var status = await harness.GetAsync<PaymentStatusResponse>(
            $"/v1/fare/pay/{paymentId}/status", passenger);

        Assert.Equal("PartiallyRefunded", status.State);
    }

    /// <summary>
    /// DoD: a wallet payment moves <b>exactly one</b> balanced entry and is <c>Succeeded</c> without
    /// any gateway call.
    /// </summary>
    [Fact]
    public async Task A_wallet_fare_settles_on_the_spot_through_one_ledger_call()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        var paid = await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "wallet" }, passenger),
            "pay from the wallet");

        // No Pending: a wallet payment is a ledger move inside wallet-svc's transaction, so there is
        // nothing to wait for and no callback that could arrive.
        Assert.Equal("Succeeded", paid.State);
        Assert.Equal("wallet", paid.Method);
        Assert.Equal(0, paid.SurchargeMinor);

        var posting = Assert.Single(downstream.LedgerPostings);

        Assert.EndsWith("/trip-payment", posting.Path, StringComparison.Ordinal);
        Assert.Equal(amountMinor, posting.Number("amountMinor"));

        // Passenger and driver, no platform leg — MageRide is the custodian, not a party.
        Assert.Equal(ride.PassengerId.ToString(), posting.String("passengerId"));
        Assert.Equal(ride.DriverId.ToString(), posting.String("driverId"));

        // R-05: the earning posts because the payment closed, and ride-svc is told.
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
        Assert.Equal("Succeeded", Assert.Single(downstream.Settlements).String("paymentState"));
    }

    /// <summary>
    /// DoD: a wallet payment against a short balance <b>refuses and moves no money</b> — and the
    /// passenger keeps cash and the driver's QR rather than being silently fallen back.
    /// </summary>
    [Fact]
    public async Task A_wallet_fare_the_passenger_cannot_afford_settles_nothing()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        downstream.RefuseTripPayment = true;

        using (var refused = await harness.PostAsync(
            "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "wallet" }, passenger))
        {
            var (code, _) = await FareHarness.ProblemAsync(refused);

            Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
            Assert.Equal("insufficient-wallet", code);
        }

        // The payment did NOT move and the driver earned nothing: a refusal is not a settlement, and
        // it is deliberately not a silent fallback to cash either — the passenger chose a rail and
        // has to be told it is short.
        var status = await harness.GetAsync<PaymentStatusResponse>(
            $"/v1/fare/pay/{paymentId}/status", passenger);

        Assert.Equal("Initiated", status.State);
        Assert.Null(await harness.EarningsAsync(ride.DriverId));
        Assert.Empty(downstream.Settlements);

        // …and the rails that remain still work on the same payment.
        downstream.RefuseTripPayment = false;

        var cash = await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync($"/v1/fare/pay/{paymentId}/fallback-cash", null, passenger),
            "settle in cash instead");

        Assert.Equal("FellBackToCash", cash.State);
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
    }

    /// <summary>A refund cannot give back more than the payment took.</summary>
    [Fact]
    public async Task A_refund_cannot_exceed_what_was_paid()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        // Δ AL-57: the wallet rail settles on the spot — one balanced ledger entry in
        // wallet-svc, no gateway and therefore no callback to wait for.
        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "wallet" }, passenger),
            "pay from the wallet");

        var financeUser = await harness.Seed.UserAsync("finance_officer");

        using var tooMuch = await harness.PostAsync(
            "/v1/admin/fare/refund",
            new
            {
                paymentId = paymentId.ToString(),
                kind = "full",
                amountMinor = amountMinor + 1,
                reasonCode = "typo",
            },
            harness.Tokens.Finance(financeUser));

        var (code, _) = await FareHarness.ProblemAsync(tooMuch);
        Assert.Equal("invalid-amount", code);
    }

    /// <summary>A refund is Finance's, not a passenger's.</summary>
    [Fact]
    public async Task A_passenger_cannot_refund_their_own_fare()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, _) = await PricedAsync(harness);

        using var response = await harness.PostAsync(
            "/v1/admin/fare/refund",
            new { paymentId = paymentId.ToString(), kind = "full", amountMinor = 100, reasonCode = "nope" },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>US-8.15: a passenger stranded by a gateway outage settles in the vehicle.</summary>
    [Fact]
    public async Task A_payment_that_was_never_completed_falls_back_to_cash()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        // Δ AL-57: there is no gateway left to be stranded BY, so the payment is simply one that
        // was priced and never completed — `Initiated`. US-8.15's fallback is unchanged and still
        // the answer: the passenger settles in the vehicle.
        var cash = await harness.OkAsync<PaymentStatusResponse>(
            await harness.PostAsync($"/v1/fare/pay/{paymentId}/fallback-cash", null, passenger),
            "fall back to cash");

        Assert.Equal("FellBackToCash", cash.State);
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));

        // …and it cannot be settled a second time.
        using var again = await harness.PostAsync($"/v1/fare/pay/{paymentId}/fallback-cash", null, passenger);
        var (code, _) = await FareHarness.ProblemAsync(again);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("payment-already-settled", code);
    }

    /// <summary>Only the ride's own people may touch its money.</summary>
    [Fact]
    public async Task A_stranger_cannot_pay_or_poll_somebody_elses_ride()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, _) = await PricedAsync(harness);
        var stranger = harness.Tokens.Passenger(await harness.Seed.UserAsync("passenger"));

        using (var pay = await harness.PostAsync(
            "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "cash" }, stranger))
        {
            var (code, _) = await FareHarness.ProblemAsync(pay);
            Assert.Equal("not-ride-participant", code);
        }

        using var poll = await harness.GetAsync($"/v1/fare/pay/{paymentId}/status", stranger);
        var (pollCode, _) = await FareHarness.ProblemAsync(poll);

        Assert.Equal("not-ride-participant", pollCode);
    }

    /// <summary>
    /// P-04: cash is paid by the rider and LankaQR/OnePay by the booker, resolved from the method
    /// chosen at payment rather than at booking.
    /// </summary>
    [Fact]
    public async Task The_payer_follows_the_method_chosen_at_payment()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var booker = await harness.Seed.UserAsync("passenger");
        var ride = await harness.Seed.RideAsync(paymentMethod: "cash", bookerId: booker);

        await harness.OkAsync<FinalFareResponse>(
            await harness.CalculateAsync(ride.RideId, distanceKm: 4.0), "calculate");

        // Booked as cash — so C049 opened the row with payer_role = rider…
        Assert.Equal("rider", Assert.Single(await harness.PaymentsAsync(ride.RideId)).PayerRole);

        // …and paying from the wallet moves the charge to the booker (Δ AL-57: the rule was always
        // about whose instrument settles it, and a stored balance is the booker's).
        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay",
                new { rideId = ride.RideId.ToString(), method = "wallet" },
                harness.Tokens.Passenger(ride.PassengerId)),
            "pay from the wallet");

        await using var connection = await harness.OpenAsync();

        var payerRole = await Dapper.SqlMapper.ExecuteScalarAsync<string>(
            connection,
            "SELECT payer_role FROM fares.ride_payments WHERE ride_id = @RideId;",
            new { ride.RideId });

        Assert.Equal("booker", payerRole);
    }

}
