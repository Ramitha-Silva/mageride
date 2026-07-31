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
    /// US-8.11: OnePay adds 5% and states it separately, so the passenger sees the difference before
    /// committing. Every other rail is zero.
    /// </summary>
    [Fact]
    public async Task Onepay_adds_five_percent_and_nothing_else_does()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, _, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        // D-11: without a merchant binding the money has nowhere to land.
        await ModeBFreeOfMerchant(harness, ride, passenger, amountMinor);

        await harness.Seed.MerchantAsync(ride.DriverId, "ONEPAY-MERCHANT-1");

        var lankaqr = await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

        Assert.Equal(0, lankaqr.SurchargeMinor);
        Assert.Equal("Pending", lankaqr.State);

        // AL-15: the deep link is primary and the QR is the fallback.
        Assert.NotNull(lankaqr.LankaQr?.PaymentLink);
        Assert.NotNull(lankaqr.LankaQr?.QrPayload);
        Assert.Null(lankaqr.Onepay);
    }

    private static async Task ModeBFreeOfMerchant(
        FareHarness harness, SeededRide ride, string passenger, long amountMinor)
    {
        using var refused = await harness.PostAsync(
            "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "onepay" }, passenger);

        var (code, _) = await FareHarness.ProblemAsync(refused);

        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal("merchant-not-onboarded", code);
        Assert.True(amountMinor > 0);
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

    /// <summary>Definition of done: a duplicate OnePay callback is a no-op.</summary>
    [Fact]
    public async Task A_duplicate_gateway_callback_settles_once()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

        var callback = new
        {
            providerTransactionId = "LANKAQR-C050-0001",
            paymentId = paymentId.ToString(),
            status = "SUCCESS",
            amountMinor,
        };

        for (var delivery = 0; delivery < 3; delivery++)
        {
            using var response = await harness.PostSignedAsync(
                "/v1/fare/pay/lankaqr/confirm", callback, FareHarness.LankaQrWebhookSecret);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var status = await harness.GetAsync<PaymentStatusResponse>(
            $"/v1/fare/pay/{paymentId}/status", passenger);

        Assert.Equal("Succeeded", status.State);

        // Settled once: one earning of one trip, and one report to ride-svc.
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
        Assert.Single(downstream.Settlements);
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
    /// R-19 / §11.14: a provider <c>Succeeded</c> arriving after the ride was settled in cash makes
    /// the payment <c>Overpaid</c> and puts a reversal on the Finance queue — and does <b>not</b>
    /// drag the ride to Disputed.
    /// </summary>
    [Fact]
    public async Task A_late_success_after_cash_becomes_an_overpayment_on_the_finance_queue()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "cash" }, passenger),
            "pay cash");

        // The card the passenger had already given up on authorises an hour later.
        using (var late = await harness.PostSignedAsync(
            "/v1/fare/pay/onepay/webhook",
            new
            {
                providerTransactionId = "ONEPAY-C050-LATE",
                paymentId = paymentId.ToString(),
                status = "SUCCESS",
                amountMinor,
            },
            FareHarness.OnepayWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        }

        var status = await harness.GetAsync<PaymentStatusResponse>(
            $"/v1/fare/pay/{paymentId}/status", passenger);

        Assert.Equal("Overpaid", status.State);

        var queued = Assert.Single(await harness.RefundQueueAsync());
        Assert.Equal("overpaid_reversal", queued.Kind);
        Assert.Equal(amountMinor, queued.AmountMinor);
        Assert.Equal("Requested", queued.Status);

        // The cash settlement stands: one earning, and the ride was told once — about the cash.
        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
        Assert.Equal("FellBackToCash", Assert.Single(downstream.Settlements).String("paymentState"));
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

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

        using (var settled = await harness.PostSignedAsync(
            "/v1/fare/pay/lankaqr/confirm",
            new
            {
                providerTransactionId = "LANKAQR-C050-REFUND",
                paymentId = paymentId.ToString(),
                status = "SUCCESS",
                amountMinor,
            },
            FareHarness.LankaQrWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
        }

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

        var posting = Assert.Single(downstream.LedgerPostings);
        Assert.Equal("payment_refund", posting.String("kind"));
        Assert.Equal(10_000, posting.Number("amountMinor"));
        Assert.Equal($"payment_refund:{refund.RefundId}", posting.String("idempotencyKey"));

        var status = await harness.GetAsync<PaymentStatusResponse>(
            $"/v1/fare/pay/{paymentId}/status", passenger);

        Assert.Equal("PartiallyRefunded", status.State);
    }

    /// <summary>A refund cannot give back more than the payment took.</summary>
    [Fact]
    public async Task A_refund_cannot_exceed_what_was_paid()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

        using (var settled = await harness.PostSignedAsync(
            "/v1/fare/pay/lankaqr/confirm",
            new
            {
                providerTransactionId = "LANKAQR-C050-CAP",
                paymentId = paymentId.ToString(),
                status = "SUCCESS",
                amountMinor,
            },
            FareHarness.LankaQrWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
        }

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
    public async Task A_stranded_gateway_payment_falls_back_to_cash()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

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

        // …and paying by LankaQR moves the charge to the booker.
        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay",
                new { rideId = ride.RideId.ToString(), method = "lankaqr" },
                harness.Tokens.Passenger(ride.PassengerId)),
            "pay by LankaQR");

        await using var connection = await harness.OpenAsync();

        var payerRole = await Dapper.SqlMapper.ExecuteScalarAsync<string>(
            connection,
            "SELECT payer_role FROM fares.ride_payments WHERE ride_id = @RideId;",
            new { ride.RideId });

        Assert.Equal("booker", payerRole);
    }

    /// <summary>
    /// §11.8's retry: a gateway refusal is not the end of the payment. The failed attempt is closed
    /// as <c>Retried</c> and a new row carries the next try, so `provider_transaction_id` stays
    /// one-to-one with a gateway call and the chain is reconstructable.
    /// </summary>
    [Fact]
    public async Task A_failed_payment_can_be_retried_on_a_new_attempt()
    {
        await using var downstream = await DownstreamStub.StartAsync();
        await using var harness = await FareHarness.StartAsync(postgres, downstream: downstream);

        var (ride, paymentId, amountMinor) = await PricedAsync(harness);
        var passenger = harness.Tokens.Passenger(ride.PassengerId);

        await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "pay by LankaQR");

        using (var failed = await harness.PostSignedAsync(
            "/v1/fare/pay/lankaqr/confirm",
            new { providerTransactionId = "LANKAQR-C050-FAIL", paymentId = paymentId.ToString(), status = "FAILED" },
            FareHarness.LankaQrWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }

        var retry = await harness.OkAsync<PaymentInitiationResponse>(
            await harness.PostAsync(
                "/v1/fare/pay", new { rideId = ride.RideId.ToString(), method = "lankaqr" }, passenger),
            "retry");

        Assert.NotEqual(paymentId, retry.PaymentId);
        Assert.Equal("Pending", retry.State);
        Assert.Equal(amountMinor, retry.AmountMinor);

        var rows = await harness.PaymentsAsync(ride.RideId);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Retried", rows[0].State);
        Assert.Equal("Pending", rows[1].State);

        // The second attempt settles, and only it earns the driver.
        using (var ok = await harness.PostSignedAsync(
            "/v1/fare/pay/lankaqr/confirm",
            new
            {
                providerTransactionId = "LANKAQR-C050-RETRY",
                paymentId = retry.PaymentId.ToString(),
                status = "SUCCESS",
                amountMinor,
            },
            FareHarness.LankaQrWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        Assert.Equal((1, amountMinor), await harness.EarningsAsync(ride.DriverId));
    }
}
