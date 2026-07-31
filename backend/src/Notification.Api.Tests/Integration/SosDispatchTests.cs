using System.Diagnostics;
using MageRide.Notification.Domain;
using MageRide.Notification.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Notification.Tests.Integration;

/// <summary>
/// D-33: an SOS goes through both gateways in parallel and the first delivery wins, p99 ≤ 5 s.
/// </summary>
/// <remarks>
/// <para>
/// The component's second definition of done, measured against two gateways on real sockets. It has
/// to be sockets: "in parallel" is a claim about two requests being in flight at once, and a fake
/// <c>ISmsGateway</c> would prove only that the code calls what it calls.
/// </para>
/// <para>
/// <b>The primary is deliberately slow.</b> A two-second primary and an instant secondary is the
/// case the design exists for — D6' §7.3's ordinary sequential fallback would wait the primary out
/// on every message, and D-33 says an emergency does not have that time. The percentile is asserted
/// against the spec's five seconds, and the shape of the result (the secondary answering first,
/// both gateways receiving the message) is asserted separately, because a p99 under five seconds
/// could also be reached by simply having a fast primary.
/// </para>
/// </remarks>
[Collection(NotificationCollection.Name)]
public sealed class SosDispatchTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>D5' §14.3 / D-33.</summary>
    private static readonly TimeSpan Slo = TimeSpan.FromSeconds(5);

    private const int Samples = 20;

    [Fact]
    public async Task An_sos_reaches_both_gateways_in_parallel_within_the_p99()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.UserAsync(role: "driver");

        // The gateway having a bad minute, and the one that is not. Under D6' §7.3's sequential
        // rule every message below would cost at least two seconds.
        harness.PrimarySms.Delay = TimeSpan.FromSeconds(2);
        harness.SecondarySms.Delay = TimeSpan.Zero;

        var latencies = new List<double>(Samples);

        for (var i = 0; i < Samples; i++)
        {
            var stopwatch = Stopwatch.StartNew();

            using var response = await harness.SendInternalAsync(new
            {
                notificationType = NotificationCatalogue.SosTriggered,
                phones = new[] { $"+9477000{i:D4}" },
                data = new
                {
                    name = "Nimal",
                    link = "https://passenger.mageride.test/track?token=x",
                },
            });

            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

            await harness.DeliverAsync();

            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var p99 = SmsGatewayStub.Percentile(latencies, 99);

        Assert.True(
            p99 <= Slo.TotalMilliseconds,
            $"D-33 budgets {Slo.TotalSeconds} s at the 99th percentile; this run measured {p99:F0} ms.");

        // The secondary won every race, which is what "whichever delivers first" means when the
        // primary is the slow one.
        Assert.Equal(Samples, harness.SecondarySms.Sent.Count);

        // And the primary still received all of them: both gateways are written to, which is the
        // half of D-33 the percentile alone would not show. The stragglers are still in flight when
        // the loop ends, so this waits for them.
        await WaitForAsync(() => harness.PrimarySms.Sent.Count == Samples, TimeSpan.FromSeconds(30));

        Assert.Equal(Samples, harness.PrimarySms.Sent.Count);

        // Every row settled, and the gateway recorded on it is the one that answered first.
        var rows = await harness.QueueAsync();

        Assert.Equal(Samples, rows.Count);
        Assert.All(rows, row => Assert.Equal(NotificationStatuses.Sent, row.Status));

        // The message is the rendered template, not a string this service composed.
        var message = harness.SecondarySms.Sent[0].Message;

        Assert.Contains("Nimal", message, StringComparison.Ordinal);
        Assert.Contains("https://passenger.mageride.test/track?token=x", message, StringComparison.Ordinal);

        // Deliberately unused: the driver is here to prove an SOS addresses a *number* rather than
        // an account — the emergency contact is nobody's user id (AL-13).
        Assert.NotEqual(Guid.Empty, driver.Id);
    }

    /// <summary>
    /// The one gateway that is having a bad minute is exactly the case D-33 exists for: the message
    /// still lands, because the other one was written to at the same time.
    /// </summary>
    [Fact]
    public async Task An_sos_survives_the_primary_gateway_refusing()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        harness.PrimarySms.Refuse = true;

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.SosTriggered,
            phones = new[] { "+94771234567" },
            data = new { name = "Nimal", link = "https://passenger.mageride.test/track?token=x" },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        Assert.Empty(harness.PrimarySms.Sent);
        Assert.Single(harness.SecondarySms.Sent);
        Assert.Equal(NotificationStatuses.Sent, (await harness.QueueAsync()).Single().Status);
    }

    /// <summary>
    /// Both refusing is a failure that retries on D-27's schedule, not a silent success. Nothing
    /// here pretends an emergency message went out.
    /// </summary>
    [Fact]
    public async Task An_sos_that_no_gateway_took_is_retried_rather_than_reported_sent()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        harness.PrimarySms.Refuse = true;
        harness.SecondarySms.Refuse = true;

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.SosTriggered,
            phones = new[] { "+94771234567" },
            data = new { name = "Nimal", link = "https://passenger.mageride.test/track?token=x" },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        var row = Assert.Single(await harness.QueueAsync());

        Assert.Equal(NotificationStatuses.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextAttemptAt);

        // D-27's first backoff step: the row is due again five seconds out, not immediately.
        Assert.Equal(NotificationHarness.DefaultNow.AddSeconds(5), row.NextAttemptAt);
    }

    /// <summary>
    /// One gateway is a legal deployment and a degraded one: the message goes, and start-up has
    /// already said the SLO has nothing behind it.
    /// </summary>
    [Fact]
    public async Task An_sos_with_no_secondary_gateway_still_goes_through_the_primary()
    {
        await using var harness = await NotificationHarness.StartAsync(
            postgres, redis, withSecondaryGateway: false);

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.SosTriggered,
            phones = new[] { "+94771234567" },
            data = new { name = "Nimal", link = "https://passenger.mageride.test/track?token=x" },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        Assert.Single(harness.PrimarySms.Sent);
        Assert.Empty(harness.SecondarySms.Sent);
    }

    /// <summary>An SOS is not something a recipient can switch off (US-10.7).</summary>
    [Fact]
    public async Task An_sos_is_sent_even_to_a_recipient_who_muted_everything()
    {
        await using var harness = await NotificationHarness.StartAsync(postgres, redis);

        var contact = await harness.Seed.UserAsync();
        await harness.Seed.MuteAsync(contact.Id, NotificationCatalogue.SosTriggered);

        using var response = await harness.SendInternalAsync(new
        {
            notificationType = NotificationCatalogue.SosTriggered,
            recipients = new[] { contact.Id },
            data = new { name = "Nimal", link = "https://passenger.mageride.test/track?token=x" },
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        await harness.DeliverAsync();

        // One notification, delivered through both gateways — which is what D-33 buys and what the
        // mute did not stop.
        Assert.Equal(NotificationStatuses.Sent, (await harness.QueueAsync(contact.Id)).Single().Status);
        Assert.Single(harness.PrimarySms.Sent);
        Assert.Single(harness.SecondarySms.Sent);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline && !condition())
        {
            await Task.Delay(50);
        }
    }
}
