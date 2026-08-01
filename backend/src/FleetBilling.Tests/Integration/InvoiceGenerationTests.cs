using Dapper;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// Generation: the C060 definition of done, one clause at a time.
/// </summary>
/// <remarks>
/// "An invoice's per-vehicle lines sum to its total", "Mode A vehicles never appear as a charged
/// line" and "re-running invoice generation for a month is idempotent" are all properties of what is
/// in the database after a run, so every assertion here reads the tables back rather than trusting a
/// return value.
/// </remarks>
[Collection<FleetBillingCollection>]
public sealed class InvoiceGenerationTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>The DoD's first clause, and the second in the same test.</summary>
    [Fact]
    public async Task Lines_sum_to_the_total_and_no_Mode_A_vehicle_is_on_the_invoice()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var busA = await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");
        var busB = await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");
        var vanA = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var vanB = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        var vanC = await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();

        var run = await harness.GenerateAsync();

        Assert.Equal(1, run.InvoicesRaised);
        Assert.Equal(3, run.LinesAdded);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal("DUE", invoice.Status);
        Assert.Equal(3, invoice.VehicleCount);
        Assert.Equal(90_000, invoice.AmountMinor);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{invoice.InvoiceId}", fleet.Bearer);

        // The DoD, literally: Σ lines = total.
        Assert.Equal(detail.Invoice.AmountMinor, detail.LineSumMinor);
        Assert.Equal(90_000, detail.Lines.Sum(line => line.AmountMinor));

        // And no Mode A vehicle anywhere on it — not as a zero line, not as a line at all.
        var billed = detail.Lines.Select(line => line.VehicleId).ToHashSet();

        Assert.Equal(new[] { vanA.Id, vanB.Id, vanC.Id }.Order(), billed.Order());
        Assert.DoesNotContain(busA.Id, billed);
        Assert.DoesNotContain(busB.Id, billed);
    }

    /// <summary>
    /// The fence, one level down: the table this component consolidates has no Mode A row to
    /// consolidate.
    /// </summary>
    /// <remarks>
    /// This is what makes the test above a real assertion rather than a statement about this suite's
    /// seed. The charges are raised with subscription-svc's own statement (C047
    /// <c>ModeBBillingRepository</c>, transcribed in <see cref="FleetBillingSeed.ModeBRaiseSql"/>),
    /// whose <c>WHERE v.mode = 'B'</c> is where AL-03 is actually enforced.
    /// </remarks>
    [Fact]
    public async Task The_raise_this_suite_seeds_with_produces_no_Mode_A_charge()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var bus = await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();

        Assert.Null(await harness.ChargeStatusAsync(bus.Id, FleetBillingHarness.DefaultPeriod));

        await using var connection = await harness.OpenAsync();

        var modeAcharges = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int
              FROM billing.monthly_subscriptions ms
              JOIN registry.vehicles v ON v.id = ms.vehicle_id
             WHERE v.mode <> 'B';
            """);

        Assert.Equal(0, modeAcharges);
    }

    /// <summary>The DoD's third clause.</summary>
    [Fact]
    public async Task Re_running_generation_for_a_month_changes_nothing()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();

        var first = await harness.GenerateAsync();
        var second = await harness.GenerateAsync();
        var third = await harness.GenerateAsync();

        Assert.Equal(1, first.InvoicesRaised);
        Assert.Equal(0, second.InvoicesRaised);
        Assert.Equal(0, second.LinesAdded);
        Assert.Equal(0, third.InvoicesRaised);
        Assert.Equal(0, third.LinesAdded);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.fleet_invoices WHERE fleet_id = @Id;", new { fleet.Id }));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM billing.fleet_invoice_lines l
              JOIN billing.fleet_invoices i ON i.id = l.invoice_id
             WHERE i.fleet_id = @Id;
            """,
            new { fleet.Id }));

        // Exactly one `fleet.invoice_issued`, however many times the generator ran.
        var issued = await harness.OutboxAsync("fleet.invoice_issued");
        Assert.Single(issued);
    }

    /// <summary>
    /// A re-run is how a vehicle approved on the 9th gets billed — the reason the runner is an
    /// interval and not a monthly alarm.
    /// </summary>
    [Fact]
    public async Task A_vehicle_added_mid_month_joins_the_invoice_on_the_next_run()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var late = await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.RaiseModeBChargesAsync();

        var second = await harness.GenerateAsync();

        Assert.Equal(0, second.InvoicesRaised);
        Assert.Equal(1, second.LinesAdded);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal(2, invoice.VehicleCount);
        Assert.Equal(60_000, invoice.AmountMinor);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{invoice.InvoiceId}", fleet.Bearer);

        Assert.Contains(detail.Lines, line => line.VehicleId == late.Id);
        Assert.Equal(detail.Invoice.AmountMinor, detail.LineSumMinor);
    }

    /// <summary>D5' §2.1 / §20: a vehicle's first Colombo month costs nothing, and is still listed.</summary>
    [Fact]
    public async Task A_vehicle_in_its_first_month_is_a_free_line_and_not_a_missing_one()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        var established = await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        // Registered inside the period being billed.
        var newcomer = await harness.Seed.AddVehicleAsync(
            fleet, mode: "B", createdAt: FleetBillingHarness.DefaultNow.AddDays(-2));

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{page.Items[0].InvoiceId}", fleet.Bearer);

        var free = Assert.Single(detail.Lines, line => line.VehicleId == newcomer.Id);
        var charged = Assert.Single(detail.Lines, line => line.VehicleId == established.Id);

        Assert.Equal("FREE", free.Status);
        Assert.Equal(0, free.AmountMinor);
        Assert.Equal("DUE", charged.Status);
        Assert.Equal(30_000, charged.AmountMinor);

        // Two vehicles on the breakdown, one month's fee on the total.
        Assert.Equal(2, detail.Invoice.VehicleCount);
        Assert.Equal(30_000, detail.Invoice.AmountMinor);
        Assert.Equal(detail.Invoice.AmountMinor, detail.LineSumMinor);
    }

    /// <summary>
    /// 1106's own table comment, honoured: "a Mode-A-only fleet gets a FREE invoice rather than no
    /// invoice — the row is the evidence the run considered them".
    /// </summary>
    [Fact]
    public async Task A_Mode_A_only_fleet_gets_a_free_invoice_with_no_lines()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal("FREE", invoice.Status);
        Assert.Equal(0, invoice.AmountMinor);
        Assert.Equal(0, invoice.VehicleCount);
        Assert.Null(invoice.JournalEntryId);

        var detail = await harness.GetAsync<FleetInvoiceDetailResponse>(
            $"/v1/fleets/{fleet.Id}/billing/{invoice.InvoiceId}", fleet.Bearer);

        Assert.Empty(detail.Lines);
        Assert.Equal(0, detail.LineSumMinor);
    }

    /// <summary>
    /// US-13.A7: an organisation waiting for a Verification Officer has no approved vehicles, so it
    /// has nothing to bill — and generation must not invent an invoice for it.
    /// </summary>
    [Fact]
    public async Task A_pending_organisation_is_not_invoiced()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var approved = await harness.Seed.CreateFleetAsync();
        var pending = await harness.Seed.CreateFleetAsync(status: "PENDING");

        await harness.Seed.AddVehicleAsync(approved, mode: "B");
        await harness.Seed.AddVehicleAsync(pending, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();

        var run = await harness.GenerateAsync();

        Assert.Equal(1, run.InvoicesRaised);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM billing.fleet_invoices WHERE fleet_id = @Id;", new { pending.Id }));
    }

    /// <summary>
    /// A vehicle that changed organisation mid-month is billed once, to whichever invoice claimed
    /// its charge first — `ux_fleet_invoice_lines_charge`, which exists for exactly this.
    /// </summary>
    [Fact]
    public async Task One_raised_charge_reaches_one_invoice_even_after_the_vehicle_changes_fleet()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var first = await harness.Seed.CreateFleetAsync();
        var second = await harness.Seed.CreateFleetAsync();
        var vehicle = await harness.Seed.AddVehicleAsync(first, mode: "B");

        // The second organisation needs a roster of its own, or it is not invoiced at all.
        await harness.Seed.AddVehicleAsync(second, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        // The bus moves next door, mid-month.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE registry.fleet_vehicles SET fleet_id = @To WHERE vehicle_id = @Vehicle;",
                new { To = second.Id, Vehicle = vehicle.Id });
        }

        await harness.GenerateAsync();

        await using var check = await harness.OpenAsync();

        var lines = await check.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int FROM billing.fleet_invoice_lines l
              JOIN billing.monthly_subscriptions ms ON ms.id = l.monthly_subscription_id
             WHERE ms.vehicle_id = @Vehicle;
            """,
            new { Vehicle = vehicle.Id });

        Assert.Equal(1, lines);
    }

    /// <summary>The event the Fleet Portal reacts to, written in the transaction that raised it (R-13).</summary>
    [Fact]
    public async Task Raising_an_invoice_queues_one_issued_event_keyed_by_fleet()
    {
        await using var harness = await FleetBillingHarness.StartAsync(postgres, redis, redpanda);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        var issued = Assert.Single(await harness.OutboxAsync("fleet.invoice_issued"));

        Assert.Equal(fleet.Id, issued.AggregateId);
        Assert.Equal(60_000, issued.Number("amountMinor"));
        Assert.Equal(2, issued.Number("vehicleCount"));
        Assert.Equal("DUE", issued.Text("status"));
    }
}
