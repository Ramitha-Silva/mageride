using Dapper;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// Dunning: the two signals C060's deliverable names — "to the Fleet Portal and notification-svc".
/// </summary>
[Collection<FleetBillingCollection>]
public sealed class DunningTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    [Fact]
    public async Task An_unpaid_invoice_goes_overdue_when_its_term_lapses_and_not_before()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        // Inside the seven-day term: nothing to say.
        harness.Clock.Advance(TimeSpan.FromDays(3));

        var early = await harness.DunAsync();

        Assert.Equal(0, early.MarkedOverdue);
        Assert.Empty(notifications.Sent);

        harness.Clock.Advance(TimeSpan.FromDays(5));

        var late = await harness.DunAsync();

        Assert.Equal(1, late.MarkedOverdue);
        Assert.Equal(1, late.Notified);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        var invoice = Assert.Single(page.Items);

        Assert.Equal("OVERDUE", invoice.Status);
        Assert.NotNull(invoice.OverdueAt);
        Assert.Null(invoice.SettledAt);
        Assert.Null(invoice.JournalEntryId);
    }

    /// <summary>
    /// The notice carries a type and values, and no rendered sentence: notification-svc resolves the
    /// wording and each recipient's language (D-26, migration 1906).
    /// </summary>
    [Fact]
    public async Task The_notice_names_a_type_and_the_owners_and_composes_no_string()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync(name: "C060 Kandy Coaches");
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        // A Manager, who must not be told about a bill they cannot pay (US-13.A5).
        await harness.Seed.AddMemberAsync(fleet.Id, "manager");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(9));
        await harness.DunAsync();

        var sent = Assert.Single(notifications.Sent);

        Assert.Equal("FLEET_INVOICE_OVERDUE", sent.NotificationType);
        Assert.Equal([fleet.OwnerId.ToString()], sent.Recipients);
        Assert.Equal("C060 Kandy Coaches", sent.Data["fleetName"]);
        Assert.Equal("2026-07", sent.Data["periodMonth"]);
        Assert.Equal("300.00", sent.Data["amount"]);
        Assert.Equal("30000", sent.Data["amountMinor"]);
        Assert.Equal("2", sent.Data["daysOverdue"]);

        // The internal plane's guard is presented, and the key separates one reminder round from
        // the next.
        Assert.Equal("c060-notification-key", sent.InternalKey);
        Assert.Contains("fleet-invoice-overdue:", sent.IdempotencyKey!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The claim is what makes it exactly-once across replicas: a second sweep in the same window
    /// announces nothing.
    /// </summary>
    [Fact]
    public async Task A_second_sweep_in_the_same_window_says_nothing_again()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(9));

        Assert.Equal(1, (await harness.DunAsync()).MarkedOverdue);

        for (var index = 0; index < 3; index++)
        {
            var again = await harness.DunAsync();

            Assert.Equal(0, again.MarkedOverdue);
            Assert.Equal(0, again.Notified);
        }

        Assert.Single(notifications.Sent);
        Assert.Single(await harness.OutboxAsync("fleet.invoice_overdue"));
    }

    /// <summary>
    /// And a reminder is not a second claim: after the interval the same invoice is dunned again,
    /// with a later <c>daysOverdue</c> and an idempotency key that says so.
    /// </summary>
    [Fact]
    public async Task A_reminder_goes_out_after_the_configured_interval()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(8));
        await harness.DunAsync();

        harness.Clock.Advance(TimeSpan.FromDays(4));
        var reminder = await harness.DunAsync();

        // Not a second transition — it was already OVERDUE — but a second notice.
        Assert.Equal(0, reminder.MarkedOverdue);
        Assert.Equal(1, reminder.Notified);
        Assert.Equal(2, notifications.Sent.Count);

        Assert.NotEqual(notifications.Sent[0].IdempotencyKey, notifications.Sent[1].IdempotencyKey);
        Assert.Equal("1", notifications.Sent[0].Data["daysOverdue"]);
        Assert.Equal("5", notifications.Sent[1].Data["daysOverdue"]);

        // `overdue_at` records when dunning began and is never moved by a reminder.
        await using var connection = await harness.OpenAsync();

        var (overdueAt, lastDunnedAt) = await connection.QuerySingleAsync<(DateTimeOffset OverdueAt, DateTimeOffset LastDunnedAt)>(
            "SELECT overdue_at, last_dunned_at FROM billing.fleet_invoices WHERE fleet_id = @Id;",
            new { fleet.Id });

        Assert.True(lastDunnedAt > overdueAt, "the reminder should move last_dunned_at and leave overdue_at alone.");
    }

    /// <summary>The Fleet Portal's half: a state and an event, whether or not anybody's phone rang.</summary>
    [Fact]
    public async Task The_portal_is_told_even_when_notification_svc_refuses()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        notifications.ResponseStatus = StatusCodes.Status500InternalServerError;

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(9));

        var result = await harness.DunAsync();

        Assert.Equal(1, result.MarkedOverdue);
        Assert.Equal(0, result.Notified);

        var page = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{fleet.Id}/billing", fleet.Bearer);

        Assert.Equal("OVERDUE", page.Items[0].Status);

        var queued = Assert.Single(await harness.OutboxAsync("fleet.invoice_overdue"));

        Assert.Equal(fleet.Id, queued.AggregateId);
        Assert.Equal(30_000, queued.Number("amountMinor"));
        Assert.Equal(2, queued.Number("daysOverdue"));
        Assert.Equal("FLEET_INVOICE_OVERDUE", queued.Text("notificationType"));
    }

    /// <summary>
    /// An invoice paid before its term lapses never becomes overdue, and one paid after it stops
    /// being dunned.
    /// </summary>
    [Fact]
    public async Task Paying_ends_the_dunning()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(9));
        await harness.DunAsync();

        await harness.Seed.CreditAsync(fleet.Id, 100_000);

        // An OVERDUE invoice is still payable — the settlement sweep takes DUE and OVERDUE alike.
        var settlement = await harness.SettleAsync(fleet.Id);
        Assert.Equal(1, settlement.Settled);

        harness.Clock.Advance(TimeSpan.FromDays(30));

        var after = await harness.DunAsync();

        Assert.Equal(0, after.MarkedOverdue);
        Assert.Equal(0, after.Notified);
        Assert.Single(notifications.Sent);
    }

    /// <summary>A FREE invoice never goes overdue: there is nothing to be late with.</summary>
    [Fact]
    public async Task A_free_invoice_is_never_dunned()
    {
        await using var notifications = await StubNotificationService.StartAsync();
        await using var harness = await StartAsync(notifications);

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "A", vehicleType: "bus");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();

        harness.Clock.Advance(TimeSpan.FromDays(60));

        var result = await harness.DunAsync();

        Assert.Equal(0, result.MarkedOverdue);
        Assert.Empty(notifications.Sent);
    }

    /// <summary>A harness whose notification hop points at this test's stub.</summary>
    private Task<FleetBillingHarness> StartAsync(StubNotificationService notifications) =>
        FleetBillingHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FleetBilling:NotificationBaseUrl"] = notifications.BaseAddress,
                ["FleetBilling:NotificationInternalApiKey"] = NotificationKey,
            });

    /// <summary>The guard the stub asserts it was handed.</summary>
    private const string NotificationKey = "c060-notification-key";
}
