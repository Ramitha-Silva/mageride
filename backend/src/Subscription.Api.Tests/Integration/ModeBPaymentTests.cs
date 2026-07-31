using System.Net;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// Epic 23's money half: the pay sheet AL-49 gates on a verified payout profile, the four
/// settlement paths, and the due date that moves when a month is actually paid for (BR-23.9/23.10).
/// </summary>
/// <remarks>
/// <b>Nothing here asserts a ledger entry, and that is the point.</b> §18b forbids
/// <c>subscription.payments</c> ever posting to <c>billing.journal_entries</c>: this money is a
/// pass-through to the fleet owner. <see cref="No_platform_ledger_entry_is_written_for_a_subscription_payment"/>
/// asserts the absence directly.
/// </remarks>
[Collection<SubscriptionCollection>]
public sealed class ModeBPaymentTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>09:30 Colombo on 5 June 2026 — BR-23.9's worked example, as an instant.</summary>
    private static readonly DateTimeOffset FifthOfJune = new(2026, 6, 5, 4, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Definition of done: "a join on 5 June with join_anniversary cycle sets next_due to 6 July".
    /// </summary>
    [Fact]
    public async Task A_join_on_5_June_sets_next_due_to_6_July()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var card = Assert.Single((await ModeBScenario.SubscriptionsAsync(harness, passenger)).Items);

        Assert.Equal("join_anniversary", card.Cycle);
        Assert.Equal(5, card.JoinDay);
        Assert.Equal(new DateOnly(2026, 7, 6), card.NextDue);
        Assert.Equal(new DateOnly(2026, 7, 6), await harness.NextDueAsync(accepted.SubscriptionId));
    }

    /// <summary>
    /// Definition of done: "pay returns payTo only from a verified profile". Online transfer gets the
    /// account details the passenger types into their banking app.
    /// </summary>
    [Fact]
    public async Task Pay_returns_the_owners_account_details_from_the_verified_payout_profile()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");

        Assert.Equal("initiated", payment.Status);
        Assert.Equal(250_000, payment.AmountMinor);
        Assert.Equal("LKR", payment.Currency);
        Assert.Equal(new DateOnly(2026, 7, 1), payment.PeriodMonth);

        Assert.NotNull(payment.PayTo);
        Assert.Equal("Commercial Bank", payment.PayTo.Bank);
        Assert.Equal("Nugegoda", payment.PayTo.Branch);
        Assert.Equal("8001234567", payment.PayTo.AccountNo);
        Assert.Equal("Sunrise Transport (Pvt) Ltd", payment.PayTo.AccountHolderName);
        Assert.Null(payment.PayTo.LankaqrImageUrl);
    }

    /// <summary>
    /// The LankaQR methods get a signed, expiring URL of the owner's bank-app QR — and it resolves.
    /// </summary>
    [Fact]
    public async Task Pay_returns_a_signed_lankaqr_image_url_that_serves_the_owners_qr()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var ownerId = await harness.Seed.UserAsync("fleet_owner");
        var qrPath = WriteTempImage(out var bytes);

        var uploadId = await harness.Seed.UploadAsync(
            ownerId, "lankaqr_code", new UriBuilder("file", string.Empty) { Path = qrPath }.Uri.ToString());

        var fleet = await ModeBScenario.FleetAsync(harness, lankaqrUploadId: uploadId);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "lankaqr_scan");

        Assert.NotNull(payment.PayTo?.LankaqrImageUrl);
        Assert.Null(payment.PayTo.Bank);

        using var image = await harness.GetAsync(payment.PayTo.LankaqrImageUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());

        // The signature is the credential, and it covers the document. Tampering with either half
        // is a 403 rather than somebody else's bank QR.
        var tampered = payment.PayTo.LankaqrImageUrl.Replace("signature=", "signature=00", StringComparison.Ordinal);
        await ModeBScenario.AssertProblemAsync(
            await harness.GetAsync(tampered), HttpStatusCode.Forbidden, "forbidden");
    }

    /// <summary>
    /// Definition of done: "an unverified org yields 409 payout-profile-not-verified" — and the
    /// versioned table means an owner's later edit cannot break collection that was already working.
    /// </summary>
    [Fact]
    public async Task Pay_refuses_an_org_whose_payout_profile_is_not_verified()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness, payoutProfileStatus: "pending_verification");
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/pay",
                new { method = "online_transfer" },
                passenger.Bearer),
            HttpStatusCode.Conflict,
            "payout-profile-not-verified");

        // The officer approves the profile; the same call now collects, against that row.
        await harness.Seed.PayoutProfileAsync(fleet.FleetId, "verified");

        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");
        Assert.Equal("Commercial Bank", payment.PayTo?.Bank);

        // An edit re-enters pending_verification as a NEW row and leaves the verified one alone, so
        // collection continues against the last verified snapshot (AL-49).
        await harness.Seed.PayoutProfileAsync(fleet.FleetId, "pending_verification");

        var stillCollecting = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");
        Assert.Equal("Commercial Bank", stillCollecting.PayTo?.Bank);
    }

    /// <summary>
    /// An individually-owned Mode B vehicle belongs to no org, so there is no payout profile for the
    /// money to reach — the same 409, for the same reason.
    /// </summary>
    [Fact]
    public async Task Pay_refuses_a_vehicle_that_belongs_to_no_fleet()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var ownerId = await harness.Seed.UserAsync("fleet_owner");
        var vehicle = await harness.Seed.VehicleAsync(
            ownerId, "van", mode: "B", modeBBilling: "paid", defaultMonthlyFareMinor: 100_000);

        var owner = new ModeBFleet(ownerId, harness.Tokens.FleetOwner(ownerId), Guid.Empty, vehicle);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, owner);

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/pay",
                new { method = "lankaqr_deeplink" },
                passenger.Bearer),
            HttpStatusCode.Conflict,
            "payout-profile-not-verified");
    }

    /// <summary>BR-23.8: a Free service payment has no fare and no payment UI.</summary>
    [Fact]
    public async Task A_free_subscription_has_nothing_to_pay()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness, billing: "free", defaultFareMinor: null);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/pay",
                new { method = "online_transfer" },
                passenger.Bearer),
            HttpStatusCode.Conflict,
            "conflict");
    }

    /// <summary>
    /// US-23.4 / item 16f: the slip arrives, the month waits, the owner confirms, the due date moves.
    /// </summary>
    [Fact]
    public async Task A_transfer_slip_waits_for_the_owner_and_the_confirm_advances_the_due_date()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");

        var uploaded = await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostFileAsync(
                $"/v1/mode-b/payments/{payment.PaymentId}/transfer-slip", passenger.Bearer, [1, 2, 3, 4]),
            "upload the transfer slip");

        Assert.Equal("pending_verification", uploaded.Status);
        Assert.NotNull(uploaded.SlipUrl);

        // The owner's roster shows the month as awaiting verification rather than paid.
        Assert.Equal(
            "pending_verification",
            Assert.Single((await ModeBScenario.RosterAsync(harness, fleet)).Items).ThisMonthStatus);

        Assert.Equal(new DateOnly(2026, 7, 6), await harness.NextDueAsync(accepted.SubscriptionId));

        var confirmed = await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/payments/{payment.PaymentId}/confirm", null, fleet.OwnerBearer),
            "confirm the slip");

        Assert.Equal("paid", confirmed.Status);
        Assert.NotNull(confirmed.PaidAt);

        // One month bought, one month rolled: 6 July becomes 6 August (BR-23.9).
        Assert.Equal(new DateOnly(2026, 8, 6), await harness.NextDueAsync(accepted.SubscriptionId));

        Assert.Equal(
            "paid",
            Assert.Single((await ModeBScenario.RosterAsync(harness, fleet)).Items).ThisMonthStatus);

        // Confirming twice does not buy a second month.
        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync($"/v1/mode-b/payments/{payment.PaymentId}/confirm", null, fleet.OwnerBearer),
            HttpStatusCode.Conflict,
            "conflict");

        Assert.Equal(new DateOnly(2026, 8, 6), await harness.NextDueAsync(accepted.SubscriptionId));
    }

    /// <summary>US-23.6: cash is handed to a collector, and only the Owner may say it arrived.</summary>
    [Fact]
    public async Task Only_the_owner_marks_cash_received()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        // The passenger cannot record their own cash — that would be marking their own month paid.
        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/pay",
                new { method = "cash" },
                passenger.Bearer),
            HttpStatusCode.BadRequest,
            "validation-failed");

        var manager = await harness.Seed.UserAsync("fleet_owner");
        await harness.Seed.FleetMemberAsync(fleet.FleetId, manager, "manager");

        await ModeBScenario.AssertProblemAsync(
            await harness.PostAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/mark-cash",
                new { amountMinor = 250_000 },
                harness.Tokens.FleetOwner(manager)),
            HttpStatusCode.Forbidden,
            "not-owner");

        var paid = await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/mark-cash",
                new { amountMinor = 250_000 },
                fleet.OwnerBearer),
            "mark cash received");

        Assert.Equal("cash", paid.Method);
        Assert.Equal("paid", paid.Status);
        Assert.Equal(250_000, paid.AmountMinor);
        Assert.Equal(new DateOnly(2026, 7, 1), paid.PeriodMonth);
        Assert.Equal(new DateOnly(2026, 8, 6), await harness.NextDueAsync(accepted.SubscriptionId));

        // And the passenger's card shows it (US-23.6's "flips the card to Paid").
        var history = await harness.GetAsync<CursorPage<SubscriptionPaymentResponse>>(
            $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/payments", passenger.Bearer);

        Assert.Equal("paid", Assert.Single(history.Items).Status);
    }

    /// <summary>
    /// R-19: the provider callback settles once. A redelivery is answered 200 and moves nothing —
    /// a second advance would hand the subscriber a free month.
    /// </summary>
    [Fact]
    public async Task A_redelivered_gateway_callback_settles_once_and_advances_the_due_date_once()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "onepay");

        // No pay-to block for OnePay: a gateway session would have to be opened against the owner's
        // merchant account, and no schema binds one to a fleet. Named in the C048 handoff.
        Assert.Null(payment.PayTo);

        var callback = new
        {
            providerTransactionId = "ONEPAY-C048-0001",
            paymentId = payment.PaymentId.ToString(),
            status = "SUCCESS",
            amountMinor = 250_000L,
        };

        using (var first = await harness.PostSignedAsync(
            "/v1/mode-b/pay/onepay/webhook", callback, SubscriptionHarness.OnepayWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        Assert.Equal(new DateOnly(2026, 8, 6), await harness.NextDueAsync(accepted.SubscriptionId));

        using (var redelivery = await harness.PostSignedAsync(
            "/v1/mode-b/pay/onepay/webhook", callback, SubscriptionHarness.OnepayWebhookSecret))
        {
            Assert.Equal(HttpStatusCode.OK, redelivery.StatusCode);
        }

        Assert.Equal(new DateOnly(2026, 8, 6), await harness.NextDueAsync(accepted.SubscriptionId));

        var history = await harness.GetAsync<CursorPage<SubscriptionPaymentResponse>>(
            $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/payments", passenger.Bearer);

        var settled = Assert.Single(history.Items);
        Assert.Equal("paid", settled.Status);
        Assert.Equal(payment.PaymentId, settled.PaymentId);
    }

    /// <summary>
    /// An unsigned or wrongly signed callback settles nothing. The money it would falsely mark
    /// received is the fleet owner's.
    /// </summary>
    [Fact]
    public async Task An_unsigned_gateway_callback_is_refused()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);
        var payment = await PayAsync(harness, passenger, accepted.SubscriptionId, "lankaqr_deeplink");

        var callback = new
        {
            providerTransactionId = "LANKAQR-C048-0002",
            paymentId = payment.PaymentId.ToString(),
            status = "SUCCESS",
        };

        await ModeBScenario.AssertProblemAsync(
            await harness.PostSignedAsync("/v1/mode-b/pay/lankaqr/confirm", callback, "the-wrong-secret"),
            HttpStatusCode.Unauthorized,
            "unauthorized");

        using (var unsigned = await harness.PostAsync("/v1/mode-b/pay/lankaqr/confirm", callback))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        }

        var history = await harness.GetAsync<CursorPage<SubscriptionPaymentResponse>>(
            $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/payments", passenger.Bearer);

        Assert.Equal("initiated", Assert.Single(history.Items).Status);
    }

    /// <summary>
    /// US-23.7: the owner overrides one subscriber's fare, and the next pay sheet asks for the new
    /// amount. Subscribers on one vehicle may pay different amounts.
    /// </summary>
    [Fact]
    public async Task An_overridden_fare_is_what_the_next_pay_sheet_asks_for()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);
        var (other, otherAccepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var updated = await harness.OkAsync<SubscriberRowResponse>(
            await harness.PutAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/fare",
                new { monthlyFareMinor = 180_000 },
                fleet.OwnerBearer),
            "override the fare");

        Assert.Equal(180_000, updated.MonthlyFareMinor);
        Assert.Equal(passenger.Id, updated.PassengerId);

        var discounted = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");
        Assert.Equal(180_000, discounted.AmountMinor);

        // The other subscriber is untouched — the override is per subscriber, not per vehicle.
        var full = await PayAsync(harness, other, otherAccepted.SubscriptionId, "online_transfer");
        Assert.Equal(250_000, full.AmountMinor);
    }

    /// <summary>SCR-FP-012: the owner's per-subscriber ledger, and only the owner's.</summary>
    [Fact]
    public async Task The_owner_reads_a_per_subscriber_ledger_and_a_passenger_reads_only_their_own()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis, now: FifthOfJune);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/mark-cash",
                new { amountMinor = 250_000 },
                fleet.OwnerBearer),
            "mark cash received");

        var ledger = await harness.GetAsync<CursorPage<SubscriptionPaymentResponse>>(
            $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/payments", fleet.OwnerBearer);

        Assert.Equal("cash", Assert.Single(ledger.Items).Method);

        var stranger = await harness.Seed.PassengerAsync();

        await ModeBScenario.AssertProblemAsync(
            await harness.GetAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/payments", stranger.Bearer),
            HttpStatusCode.Forbidden,
            "not-owner");

        await ModeBScenario.AssertProblemAsync(
            await harness.GetAsync(
                $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/payments", stranger.Bearer),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    /// <summary>
    /// The fence, asserted as an absence: MageRide holds none of this money and takes no cut, so a
    /// settled subscription payment writes no journal entry and moves no balance (§18b).
    /// </summary>
    [Fact]
    public async Task No_platform_ledger_entry_is_written_for_a_subscription_payment()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");

        await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/{fleet.VehicleId}/subscribers/{accepted.GrantId}/mark-cash",
                new { amountMinor = 250_000 },
                fleet.OwnerBearer),
            "mark cash received");

        Assert.Equal(0, await harness.EntryCountAsync("subscription"));
        Assert.Equal(0, await harness.EntryCountAsync("daily_fee"));
        Assert.Equal(0, await harness.LedgerSumAsync());
        Assert.Equal(0, await harness.BalanceAsync(fleet.OwnerId));
        Assert.Equal(0, await harness.BalanceAsync(passenger.Id));
    }

    /// <summary>
    /// <c>ux_subpay_period</c> admits one live payment per month: re-opening the sheet under a
    /// different method reuses the row rather than raising a second charge for the same month.
    /// </summary>
    [Fact]
    public async Task Re_opening_the_pay_sheet_reuses_the_months_payment()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var fleet = await ModeBScenario.FleetAsync(harness);
        var (passenger, accepted) = await ModeBScenario.SubscribeAsync(harness, fleet);

        var first = await PayAsync(harness, passenger, accepted.SubscriptionId, "online_transfer");
        var second = await PayAsync(harness, passenger, accepted.SubscriptionId, "lankaqr_scan");

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal("lankaqr_scan", second.Method);

        var history = await harness.GetAsync<CursorPage<SubscriptionPaymentResponse>>(
            $"/v1/mode-b/subscriptions/{accepted.SubscriptionId}/payments", passenger.Bearer);

        Assert.Single(history.Items);
    }

    private static async Task<SubscriptionPaymentResponse> PayAsync(
        SubscriptionHarness harness, SeededDriver passenger, Guid subscriptionId, string method) =>
        await harness.OkAsync<SubscriptionPaymentResponse>(
            await harness.PostAsync(
                $"/v1/mode-b/subscriptions/{subscriptionId}/pay", new { method }, passenger.Bearer),
            $"pay by {method}");

    /// <summary>
    /// A file on disk standing in for the object-store pointer <c>docs.uploads.storage_url</c> holds.
    /// D-36's bucket is C125's; until then the store reads a <c>file://</c> URL.
    /// </summary>
    private static string WriteTempImage(out byte[] bytes)
    {
        bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        var directory = Path.Combine(Path.GetTempPath(), "mageride-c048-qr");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, bytes);

        return path;
    }
}
