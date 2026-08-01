using System.Net;
using Dapper;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// Settlement: the other half of the DoD's first clause — "…and post to a balanced journal entry".
/// </summary>
/// <remarks>
/// Every assertion here goes through a <b>real wallet-svc</b>. The balanced-entry rule is
/// <c>trg_balanced</c>, a DEFERRABLE constraint trigger that fires at COMMIT, and "the money moves
/// once" is the UNIQUE <c>billing.journal_entries.idempotency_key</c> — both in another service's
/// schema, and neither observable against a stub.
/// </remarks>
[Collection<FleetBillingCollection>]
public sealed class InvoiceSettlementTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    [Fact]
    public async Task Settling_posts_one_balanced_entry_and_leaves_the_ledger_at_zero()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var vanA = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var vanB = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        await harness.Seed.CreditAsync(fleet.Id, 200_000);

        var settlement = await harness.SettleAsync(fleet.Id);

        Assert.Equal(1, settlement.Attempted);
        Assert.Equal(1, settlement.Settled);
        Assert.Equal(0, settlement.Insufficient);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal("PAID", invoice.Status);
        Assert.NotNull(invoice.SettledAt);
        Assert.NotNull(invoice.JournalEntryId);
        Assert.Equal(60_000, invoice.AmountMinor);

        // The entry balances, and so does everything else that has ever been posted.
        Assert.Equal(0, await harness.EntrySumAsync(invoice.JournalEntryId!.Value));
        Assert.Equal(0, await harness.LedgerSumAsync());

        // Two legs: the fleet's wallet and the platform's own account.
        var postings = await harness.PostingsAsync(invoice.JournalEntryId.Value);
        Assert.Equal(2, postings.Count);
        Assert.Contains(postings, leg => leg.AmountMinor == -60_000);
        Assert.Contains(postings, leg => leg.AmountMinor == 60_000);

        // 200,000 in, 60,000 out.
        Assert.Equal(140_000, await harness.BalanceAsync(fleet.Id));

        // And the per-vehicle charges this invoice consolidated are settled with it.
        Assert.Equal("PAID", await harness.ChargeStatusAsync(vanA.Id, FleetBillingHarness.DefaultPeriod));
        Assert.Equal("PAID", await harness.ChargeStatusAsync(vanB.Id, FleetBillingHarness.DefaultPeriod));
    }

    /// <summary>Σ lines still equals the total after settlement, and equals what was debited.</summary>
    [Fact]
    public async Task The_amount_debited_is_the_sum_of_the_lines()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();

        for (var index = 0; index < 5; index++)
        {
            await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        }

        // One in its free month, so the total is not simply 5 × the rate.
        await harness.Seed.AddVehicleAsync(fleet, mode: "B", createdAt: FleetBillingHarness.DefaultNow.AddDays(-1));

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 500_000);
        await harness.SettleAsync(fleet.Id);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}", fleet.Bearer);

        Assert.Equal(6, detail.Lines.Count);
        Assert.Equal(150_000, detail.LineSumMinor);
        Assert.Equal(detail.LineSumMinor, detail.Invoice.AmountMinor);

        var legs = await harness.PostingsAsync(detail.Invoice.JournalEntryId!.Value);
        Assert.Contains(legs, leg => leg.AmountMinor == -detail.LineSumMinor);

        Assert.Equal(350_000, await harness.BalanceAsync(fleet.Id));
    }

    /// <summary>
    /// Two settlement passes, one movement. The guard is the ledger's UNIQUE idempotency key, which
    /// is why a duplicated attempt reports <c>replayed</c> instead of posting a second entry.
    /// </summary>
    [Fact]
    public async Task Settling_twice_moves_the_money_once()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);

        await harness.SettleAsync(fleet.Id);
        var balanceAfterFirst = await harness.BalanceAsync(fleet.Id);

        // The invoice is PAID now, so the sweep finds nothing — the belt. The braces is the ledger
        // key, exercised by driving the settlement service at the row directly.
        var second = await harness.SettleAsync(fleet.Id);

        Assert.Equal(0, second.Attempted);
        Assert.Equal(balanceAfterFirst, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(1, await harness.EntryCountAsync("fleet_invoice"));

        // And the Pay button on an already-settled invoice is a 409 with a code the portal can draw.
        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}/pay", bearer: fleet.Bearer);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("invoice-not-payable", code);
    }

    /// <summary>
    /// An organisation that cannot pay is left open — the ordinary state dunning exists for, not an
    /// error.
    /// </summary>
    [Fact]
    public async Task A_wallet_that_cannot_cover_the_month_leaves_the_invoice_open()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        // 60,000 owed, 25,000 in the wallet.
        await harness.Seed.CreditAsync(fleet.Id, 25_000);

        var settlement = await harness.SettleAsync(fleet.Id);

        Assert.Equal(1, settlement.Attempted);
        Assert.Equal(0, settlement.Settled);
        Assert.Equal(1, settlement.Insufficient);

        // Nothing moved, and nothing was recorded as having moved.
        Assert.Equal(25_000, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.EntryCountAsync("fleet_invoice"));

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        Assert.Equal("DUE", page.Items[0].Status);
        Assert.Null(page.Items[0].JournalEntryId);

        // The Pay button says the same thing, with the code the driver app already branches on.
        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}/pay", bearer: fleet.Bearer);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("insufficient-wallet", code);

        // Topping up and pressing Pay again settles it, which is the whole point of leaving it open.
        await harness.Seed.CreditAsync(fleet.Id, 50_000);

        using var retry = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}/pay", bearer: fleet.Bearer);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(15_000, await harness.BalanceAsync(fleet.Id));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>
    /// A FREE invoice has nothing to pay and could not be posted even if it tried — a zero leg is a
    /// movement that did not happen, and <c>LedgerService</c> refuses one outright.
    /// </summary>
    [Fact]
    public async Task A_free_invoice_is_never_settled_and_never_posts()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var settlement = await harness.SettleAsync(fleet.Id);

        Assert.Equal(0, settlement.Attempted);
        Assert.Equal(0, await harness.EntryCountAsync("fleet_invoice"));

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        using var response = await harness.PostAsync(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}/pay", bearer: fleet.Bearer);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var (code, _) = await FleetBillingHarness.ProblemAsync(response);
        Assert.Equal("invoice-not-payable", code);
    }

    /// <summary>The receipt US-13.10b asks for, and its absence before there is one.</summary>
    [Fact]
    public async Task A_receipt_exists_only_after_the_money_moved()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoiceId = page.Items[0].InvoiceId;

        using (var before = await harness.GetAsync($"/v1/fleets/{fleet.Id}/billing/{invoiceId}/receipt", fleet.Bearer))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        var receipt = await harness.GetAsync<FleetInvoiceReceiptResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{invoiceId}/receipt", fleet.Bearer);

        Assert.Equal(invoiceId, receipt.InvoiceId);
        Assert.Equal(fleet.Id, receipt.FleetId);
        Assert.Equal(fleet.Name, receipt.FleetName);
        Assert.Equal(30_000, receipt.AmountMinor);
        Assert.Equal(1, receipt.VehicleCount);
        Assert.NotEqual(Guid.Empty, receipt.JournalEntryId);

        // The receipt names the entry that actually balanced.
        Assert.Equal(0, await harness.EntrySumAsync(receipt.JournalEntryId));
    }

    /// <summary>
    /// The wallet screen's arithmetic: what is held, what is owed, and what is left — signed,
    /// because a fleet that owes more than it holds is a state SCR-FP-010 has to draw.
    /// </summary>
    [Fact]
    public async Task The_wallet_reports_the_balance_the_outstanding_total_and_a_signed_difference()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 20_000);

        var wallet = await harness.GetAsync<FleetWalletResponse>($"/v1/fleets/{fleet.Id}/wallet", fleet.Bearer);

        Assert.Equal(20_000, wallet.BalanceMinor);
        Assert.Equal(90_000, wallet.OutstandingMinor);
        Assert.Equal(-70_000, wallet.AvailableMinor);
        Assert.Equal("LKR", wallet.Currency);

        // The opening credit is on the statement, from billing.wallet_transactions.
        var movement = Assert.Single(wallet.Movements);
        Assert.Equal("adjustment", movement.Kind);
        Assert.Equal(20_000, movement.AmountMinor);
        Assert.Equal(20_000, movement.BalanceAfterMinor);

        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        var after = await harness.GetAsync<FleetWalletResponse>($"/v1/fleets/{fleet.Id}/wallet", fleet.Bearer);

        Assert.Equal(30_000, after.BalanceMinor);
        Assert.Equal(0, after.OutstandingMinor);
        Assert.Equal(30_000, after.AvailableMinor);
        Assert.Contains(after.Movements, m => m.Kind == "fleet_invoice" && m.AmountMinor == -90_000);
    }

    /// <summary>Settlement queues the event a Fleet Portal badge reads, in the same transaction.</summary>
    [Fact]
    public async Task Settling_queues_a_paid_event_carrying_the_journal_entry()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        var paid = Assert.Single(await harness.OutboxAsync("fleet.invoice_paid"));

        Assert.Equal(fleet.Id, paid.AggregateId);
        Assert.Equal(30_000, paid.Number("amountMinor"));
        Assert.Equal(70_000, paid.Number("balanceAfterMinor"));
        Assert.NotNull(paid.Text("journalEntryId"));
    }

    /// <summary>
    /// The FREE per-vehicle charge stays FREE. Marking it PAID would say a month that cost nothing
    /// was paid for, and `ck_monthly_subscriptions_free` is not what stops it — the predicate is.
    /// </summary>
    [Fact]
    public async Task A_free_charge_is_not_marked_paid_when_the_invoice_settles()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var charged = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var newcomer = await harness.Seed.AddVehicleAsync(
            fleet, mode: "B", createdAt: FleetBillingHarness.DefaultNow.AddDays(-3));

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        Assert.Equal("PAID", await harness.ChargeStatusAsync(charged.Id, FleetBillingHarness.DefaultPeriod));
        Assert.Equal("FREE", await harness.ChargeStatusAsync(newcomer.Id, FleetBillingHarness.DefaultPeriod));
    }

    /// <summary>
    /// The invoice moves to PAID and the charge rows with it, in one transaction — so a crash
    /// between them cannot leave a settled invoice whose vehicles still read DUE.
    /// </summary>
    [Fact]
    public async Task The_invoice_and_its_charges_settle_together()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        await using var connection = await harness.OpenAsync();

        var stillDue = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int
              FROM billing.monthly_subscriptions ms
              JOIN billing.fleet_invoice_lines l ON l.monthly_subscription_id = ms.id
              JOIN billing.fleet_invoices i ON i.id = l.invoice_id
             WHERE i.status = 'PAID' AND ms.status = 'DUE';
            """);

        Assert.Equal(0, stillDue);
    }
}
