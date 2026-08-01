using System.Net;
using Dapper;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// The fleet wallet's top-up (US-13.10b): the two rails AL-05 leaves, and the callback that is the
/// only thing on this platform that credits an organisation.
/// </summary>
/// <remarks>
/// LankaQR is the rail these tests open sessions on: AL-15 makes it a template substitution with no
/// outbound call, so the assertions are about this service rather than about a gateway stub. The
/// OnePay path's own failure mode — an unconfigured rail answering 503 — is asserted separately.
/// </remarks>
[Collection<FleetBillingCollection>]
public sealed class FleetWalletTopupTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private const string OnepayWebhook = "/v1/fleet-billing/topup/onepay/webhook";
    private const string LankaQrConfirm = "/v1/fleet-billing/topup/lankaqr/confirm";

    [Fact]
    public async Task A_lankaqr_top_up_hands_back_a_deep_link_and_credits_nothing_yet()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor = 500_000, method = "lankaqr" },
            fleet.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var topup = await response.Content.ReadFromJsonAsync<FleetTopupResponse>(
            MageRide.Shared.Http.MageRideJson.Options);

        Assert.NotNull(topup);
        Assert.Equal("Pending", topup!.State);
        Assert.Equal(500_000, topup.AmountMinor);
        Assert.Equal("lankaqr", topup.Method);
        Assert.NotNull(topup.PaymentLink);
        Assert.Contains("combank://pay", topup.PaymentLink!, StringComparison.Ordinal);
        // No payload template is configured, so the AL-15 fallback is omitted rather than invented.
        Assert.Null(topup.QrPayload);

        // A session the gateway accepted has moved no money.
        Assert.Equal(0, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.EntryCountAsync("topup"));

        // The poll answers without the single-use artefacts, which were never stored.
        var polled = await harness.GetAsync<FleetTopupResponse>(
            $"/v1/fleets/{fleet.Id}/wallet/topup/{topup.TopupId}", fleet.Bearer);

        Assert.Equal("Pending", polled.State);
        Assert.Null(polled.PaymentLink);
        Assert.False(polled.Expired);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var expired = await harness.GetAsync<FleetTopupResponse>(
            $"/v1/fleets/{fleet.Id}/wallet/topup/{topup.TopupId}", fleet.Bearer);

        Assert.True(expired.Expired);
    }

    [Fact]
    public async Task A_signed_success_callback_credits_the_fleet_wallet_exactly_once()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var topup = await StartTopupAsync(harness, fleet, 250_000);

        using var first = await harness.PostSignedCallbackAsync(
            LankaQrConfirm,
            new
            {
                providerTransactionId = "combank-c060-1",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 250_000,
            },
            FleetBillingHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var settled = await first.Content.ReadFromJsonAsync<TopupCallbackResponse>(
            MageRide.Shared.Http.MageRideJson.Options);

        Assert.True(settled!.Credited);
        Assert.False(settled.Replayed);
        Assert.Equal("Succeeded", settled.State);

        Assert.Equal(250_000, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.LedgerSumAsync());
        Assert.Equal(1, await harness.EntryCountAsync("topup"));

        // R-19: the same gateway transaction, redelivered. 200 with the same body — that is what
        // stops a provider retrying for ever — and not a second credit.
        using var redelivery = await harness.PostSignedCallbackAsync(
            LankaQrConfirm,
            new
            {
                providerTransactionId = "combank-c060-1",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 250_000,
            },
            FleetBillingHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, redelivery.StatusCode);

        var replay = await redelivery.Content.ReadFromJsonAsync<TopupCallbackResponse>(
            MageRide.Shared.Http.MageRideJson.Options);

        Assert.False(replay!.Credited);
        Assert.True(replay.Replayed);

        Assert.Equal(250_000, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(1, await harness.EntryCountAsync("topup"));
    }

    [Fact]
    public async Task An_unsigned_or_wrongly_signed_callback_credits_nothing()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var topup = await StartTopupAsync(harness, fleet, 100_000);

        var body = new
        {
            providerTransactionId = "combank-c060-forged",
            topupId = topup.TopupId,
            status = "SUCCESS",
            amountMinor = 100_000,
        };

        using var forged = await harness.PostSignedCallbackAsync(
            LankaQrConfirm, body, FleetBillingHarness.LankaQrWebhookSecret, signatureOverride: "deadbeef");

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // Signed with the *other* rail's secret: the two have different keys precisely so one
        // cannot settle the other's money.
        using var crossed = await harness.PostSignedCallbackAsync(
            LankaQrConfirm, body, FleetBillingHarness.OnepayWebhookSecret);

        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);

        Assert.Equal(0, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.EntryCountAsync("topup"));
    }

    /// <summary>
    /// A OnePay callback confirming a LankaQR session. Refused, because honouring it would let
    /// either secret settle the other rail's money.
    /// </summary>
    [Fact]
    public async Task A_callback_on_the_wrong_rail_for_a_session_is_refused()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var topup = await StartTopupAsync(harness, fleet, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            OnepayWebhook,
            new
            {
                providerTransactionId = "onepay-c060-1",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 100_000,
            },
            FleetBillingHarness.OnepayWebhookSecret);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await harness.BalanceAsync(fleet.Id));
    }

    /// <summary>
    /// D6' §7.2's settlement exception: crediting the callback's number lets a spoofed provider set
    /// the balance, crediting the session's credits money nobody paid. Both are wrong.
    /// </summary>
    [Fact]
    public async Task A_callback_whose_amount_disagrees_with_its_session_credits_nothing()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var topup = await StartTopupAsync(harness, fleet, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            LankaQrConfirm,
            new
            {
                providerTransactionId = "combank-c060-mismatch",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 900_000,
            },
            FleetBillingHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.EntryCountAsync("topup"));

        // The session is left Pending for Finance rather than failed, because nobody knows yet
        // which of the two numbers is real.
        var polled = await harness.GetAsync<FleetTopupResponse>(
            $"/v1/fleets/{fleet.Id}/wallet/topup/{topup.TopupId}", fleet.Bearer);

        Assert.Equal("Pending", polled.State);
    }

    /// <summary>A FAILED callback closes the session and moves nothing.</summary>
    [Fact]
    public async Task A_failed_callback_closes_the_session()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var topup = await StartTopupAsync(harness, fleet, 100_000);

        using var response = await harness.PostSignedCallbackAsync(
            LankaQrConfirm,
            new { providerTransactionId = "combank-c060-failed", topupId = topup.TopupId, status = "FAILED" },
            FleetBillingHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var polled = await harness.GetAsync<FleetTopupResponse>(
            $"/v1/fleets/{fleet.Id}/wallet/topup/{topup.TopupId}", fleet.Bearer);

        Assert.Equal("Failed", polled.State);
        Assert.Equal(0, await harness.BalanceAsync(fleet.Id));
    }

    /// <summary>AL-05, at the boundary and at the database.</summary>
    [Fact]
    public async Task Bank_transfer_is_not_a_top_up_method()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor = 100_000, method = "bank_transfer" },
            fleet.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);

        // And there is no session row of any kind, whatever the request said.
        await using var connection = await harness.OpenAsync();

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.fleet_topups WHERE fleet_id = @Id;", new { fleet.Id }));
    }

    /// <summary>
    /// The card rail with no OnePay configured. 503 with the reason, and LankaQR unaffected — AL-05
    /// leaves exactly two rails and there is no bank-transfer fallback.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_onepay_answers_503_and_leaves_lankaqr_working()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var card = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor = 100_000, method = "onepay" },
            fleet.Bearer);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, card.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(card);
        Assert.Equal("dependency-unavailable", code);

        using var qr = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor = 100_000, method = "lankaqr" },
            fleet.Bearer);

        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
    }

    /// <summary>A top-up outside the configured range never reaches a gateway.</summary>
    [Fact]
    public async Task An_amount_outside_the_configured_range_is_refused()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        using var tooSmall = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor = 100, method = "lankaqr" },
            fleet.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(tooSmall);
        Assert.Equal("invalid-amount", code);
    }

    /// <summary>A top-up settles the invoice the next sweep reaches — the whole flow, end to end.</summary>
    [Fact]
    public async Task Topping_up_lets_the_next_sweep_settle_the_month()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        Assert.Equal(0, (await harness.SettleAsync(fleet.Id)).Settled);

        var topup = await StartTopupAsync(harness, fleet, 100_000);

        using var callback = await harness.PostSignedCallbackAsync(
            LankaQrConfirm,
            new
            {
                providerTransactionId = "combank-c060-flow",
                topupId = topup.TopupId,
                status = "SUCCESS",
                amountMinor = 100_000,
            },
            FleetBillingHarness.LankaQrWebhookSecret);

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);

        var settlement = await harness.SettleAsync(fleet.Id);

        Assert.Equal(1, settlement.Settled);
        Assert.Equal(40_000, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    private static async Task<FleetTopupResponse> StartTopupAsync(
        FleetBillingHarness harness, SeededFleet fleet, long amountMinor)
    {
        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/wallet/topup",
            new { amountMinor, method = "lankaqr" },
            fleet.Bearer);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"topup returned {(int)response.StatusCode}: {text}");

        return System.Text.Json.JsonSerializer.Deserialize<FleetTopupResponse>(
            text, MageRide.Shared.Http.MageRideJson.Options)!;
    }
}
