using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// DoD item 3, the half a row in a table cannot show: <c>share.revoked</c> reaches Redpanda
/// <em>through the outbox</em> (D6' §2.4, R-13, E-09), on the <c>registry.events</c> topic C028
/// added.
/// </summary>
/// <remarks>
/// A capturing publisher would prove the dispatcher was called; it would not prove anything was
/// produced, and it could not tell a direct publish from an outbox drain. So this runs against a
/// real broker and asserts the property that distinguishes the two — the <c>outboxId</c> header
/// the dispatcher stamps, and the row it came from marked dispatched. Same shape as ride-svc's
/// <c>OutboxPipelineTests</c> (C022).
/// </remarks>
[Collection<RegistryCollection>]
public sealed class OutboxPipelineTests(PostgresFixture postgres, RedpandaFixture redpanda)
{
    private const string Topic = "registry.events";

    [Fact]
    public async Task Share_revoked_reaches_redpanda_through_the_outbox()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(Topic);

        await using var harness = await RegistryHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
            ["Outbox:DispatcherEnabled"] = "true",
            // LISTEN/NOTIFY is the trigger under test; a short poll would mask a broken listener.
            ["Outbox:PollInterval"] = "00:01:00",
        });

        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        var grantId = await harness.GrantShareAsync(vehicleId, granteeId, owner);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/share/{grantId}", owner)).StatusCode);

        var revocation = await ConsumeAsync(
            record => record.Message.Headers.TryGetLastBytes("eventType", out var type)
                      && Encoding.UTF8.GetString(type) == "share.revoked");

        // The partition key is the vehicle (D6' §2.1's default), so events about one vehicle stay
        // ordered and a later share.granted cannot overtake this.
        Assert.Equal(vehicleId, revocation.Message.Key);

        using var payload = JsonDocument.Parse(revocation.Message.Value);
        Assert.Equal(granteeId.ToString(), payload.RootElement.GetProperty("passengerId").GetString());

        // The header only an outbox drain stamps, and the row it came from.
        Assert.True(revocation.Message.Headers.TryGetLastBytes("outboxId", out var outboxIdBytes));
        var outboxId = long.Parse(Encoding.UTF8.GetString(outboxIdBytes), System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = await harness.OpenAsync();

        // POLLED, because arriving on the topic does not mean the row has been stamped yet.
        // The dispatcher produces first and marks `dispatched_at` after the delivery report — it
        // must, or a crash between the two would lose an event the broker never took — so between
        // this test consuming the message and the UPDATE committing there is a window of
        // milliseconds in which the column is legitimately still NULL. `TripState.Api.Tests`'
        // twin was fixed for this on 2026-08-15; provisioning-svc's landed inside the same window on
        // 2026-08-20 ("Assert.NotNull() Failure: Value of type 'Nullable<DateTimeOffset>' does
        // not have a value"); this twin carries the same race and has simply not lost the window
        // yet.
        //
        // The assertion is unchanged in substance: a dispatcher that never stamps the row still
        // fails, five seconds later and with a message that says which row.
        DateTimeOffset? dispatchedAt = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            dispatchedAt = await connection.QuerySingleAsync<DateTimeOffset?>(
                "SELECT dispatched_at FROM registry.outbox WHERE id = @Id;", new { Id = outboxId });

            if (dispatchedAt is not null)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(
            dispatchedAt is not null,
            $"registry.outbox row {outboxId} reached Redpanda but was never marked dispatched. "
            + "The drain produces and then stamps; a row that stays NULL is a drain that "
            + "published and did not record it, which redelivers the event on the next sweep.");
    }

    private async Task<ConsumeResult<string, string>> ConsumeAsync(Func<ConsumeResult<string, string>, bool> match)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"registry-outbox-test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(Topic);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (!deadline.IsCancellationRequested)
        {
            var record = consumer.Consume(TimeSpan.FromSeconds(1));

            if (record is not null && match(record))
            {
                return record;
            }

            await Task.Yield();
        }

        throw new TimeoutException($"No matching record arrived on {Topic} within 30 seconds.");
    }
}
