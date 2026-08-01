using System.Text;
using Confluent.Kafka;
using MageRide.FleetBilling.Endpoints;
using MageRide.FleetBilling.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.FleetBilling.Tests.Integration;

/// <summary>
/// The hourly runner, and the topic its outbox drains to.
/// </summary>
/// <remarks>
/// Both are switched off in the default harness on purpose — a background pass raising invoices
/// underneath an assertion makes "the route did it" indistinguishable from "the runner did", and a
/// dispatcher draining underneath an outbox assertion makes "the row was queued" indistinguishable
/// from "something took it". These two tests turn each on and assert exactly the thing the switch
/// hides.
/// </remarks>
[Collection<FleetBillingCollection>]
public sealed class BillingRunnerTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    /// <summary>
    /// One pass does all three phases in order: generate before settling, or the month just opened
    /// is settled empty; settle before dunning, or an invoice a fresh top-up already covers is
    /// announced overdue.
    /// </summary>
    [Fact]
    public async Task One_pass_generates_settles_and_duns()
    {
        await using var notifications = await StubNotificationService.StartAsync();

        await using var harness = await FleetBillingHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FleetBilling:InvoicingEnabled"] = "true",
                // A long interval so the hosted service's own timer cannot fire during the test;
                // what is under test is one pass, driven directly.
                ["FleetBilling:RunInterval"] = "24:00:00",
                ["FleetBilling:NotificationBaseUrl"] = notifications.BaseAddress,
                ["FleetBilling:NotificationInternalApiKey"] = "c060-notification-key",
            });

        var solvent = await harness.Seed.CreateFleetAsync();
        var broke = await harness.Seed.CreateFleetAsync();

        await harness.Seed.AddVehicleAsync(solvent, mode: "B");
        await harness.Seed.AddVehicleAsync(broke, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.Seed.CreditAsync(solvent.Id, 100_000);

        var runner = harness.Services.GetRequiredService<MageRide.FleetBilling.Billing.FleetBillingRunner>();

        await runner.RunOnceAsync(CancellationToken.None);

        // The one that could pay, did.
        var settled = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{solvent.Id}/billing", solvent.Bearer);

        Assert.Equal("PAID", settled.Items[0].Status);
        Assert.Equal(70_000, await harness.BalanceAsync(solvent.Id));

        // The one that could not is DUE, and not yet overdue — the term has not lapsed.
        var open = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{broke.Id}/billing", broke.Bearer);

        Assert.Equal("DUE", open.Items[0].Status);
        Assert.Empty(notifications.Sent);

        // A week later, the same pass duns it. And it does not re-invoice, re-settle or re-announce
        // anything for the organisation that paid.
        harness.Clock.Advance(TimeSpan.FromDays(9));

        await runner.RunOnceAsync(CancellationToken.None);

        var dunned = await harness.GetAsync<CursorPage<FleetInvoiceResponse>>(
            $"/v1/fleets/{broke.Id}/billing", broke.Bearer);

        Assert.Equal("OVERDUE", dunned.Items[0].Status);

        var notice = Assert.Single(notifications.Sent);
        Assert.Equal(broke.OwnerId.ToString(), Assert.Single(notice.Recipients));

        Assert.Single(await harness.OutboxAsync("fleet.invoice_paid"));
        Assert.Equal(0, await harness.LedgerSumAsync());
    }

    /// <summary>
    /// The outbox reaches the broker, on the topic and with the key C044 opened <c>fleet.events</c>
    /// for.
    /// </summary>
    /// <remarks>
    /// Asserted by consuming the topic rather than by reading the table this service wrote: a row in
    /// <c>billing.fleet_outbox</c> proves the transaction, and only the broker proves the delivery.
    /// The key is the fleet, because two verdicts about one organisation have to arrive in the order
    /// they were reached — a paid notice that overtook the invoice it settles would make the Fleet
    /// Portal's badge flicker backwards.
    /// </remarks>
    [Fact]
    public async Task The_outbox_drains_to_fleet_events_keyed_by_organisation()
    {
        Assert.SkipUnless(redpanda.IsAvailable, "Redpanda is not available on this host.");

        await using var harness = await FleetBillingHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Outbox:DispatcherEnabled"] = "true",
            });

        var fleet = await harness.Seed.CreateFleetAsync();
        await harness.Seed.AddVehicleAsync(fleet, mode: "B");

        await harness.Seed.RaiseModeBChargesAsync();
        await harness.GenerateAsync();
        await harness.Seed.CreditAsync(fleet.Id, 100_000);
        await harness.SettleAsync(fleet.Id);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"c060-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(EventTopics.FleetEvents);

        var seen = new List<(string Key, string Type)>();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && seen.Count < 2)
        {
            var message = consumer.Consume(TimeSpan.FromSeconds(2));

            if (message?.Message is null)
            {
                continue;
            }

            var type = message.Message.Headers.TryGetLastBytes("eventType", out var bytes)
                ? Encoding.UTF8.GetString(bytes)
                : string.Empty;

            seen.Add((message.Message.Key, type));
        }

        consumer.Close();

        Assert.Contains(seen, entry => entry.Type == "fleet.invoice_issued");
        Assert.Contains(seen, entry => entry.Type == "fleet.invoice_paid");
        Assert.All(seen, entry => Assert.Equal(fleet.Id.ToString(), entry.Key));
    }
}
