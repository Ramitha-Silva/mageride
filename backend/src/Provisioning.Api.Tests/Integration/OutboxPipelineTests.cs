using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Messaging;
using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>
/// The half a row in a table cannot show: a revocation reaches Redpanda <em>through the outbox</em>
/// (D6' §2.4, R-13, E-09), on the <c>provisioning.events</c> topic C030 added.
/// </summary>
/// <remarks>
/// This is the durable half of T-12. The Redis message is fire-and-forget — a subscriber that was
/// down misses it — so a revoke that committed and then failed to publish would leave a
/// decommissioned tracker publishing until its 90-day certificate expired. A capturing publisher
/// would prove the dispatcher was called; it would not prove anything was produced, and it could
/// not tell a direct publish from an outbox drain. So this runs against a real broker and asserts
/// the property that distinguishes the two — the <c>outboxId</c> header the dispatcher stamps, and
/// the row it came from marked dispatched. Same shape as registry-svc's (C028).
/// </remarks>
[Collection<ProvisioningCollection>]
public sealed class OutboxPipelineTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private const string Topic = EventTopics.ProvisioningEvents;

    [Fact]
    public async Task Tracker_revoked_reaches_redpanda_through_the_outbox()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(Topic);

        await using var harness = await ProvisioningHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            settings: new Dictionary<string, string?>
            {
                // LISTEN/NOTIFY is the trigger under test; a short poll would mask a broken listener.
                ["Outbox:PollInterval"] = "00:01:00",
            });

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(bearer, imei, vehicleId);
        var serial = bound.GetProperty("credentialSerial").GetString()!;

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer)).StatusCode);

        var revocation = await ConsumeAsync(record =>
            record.Message.Headers.TryGetLastBytes("eventType", out var type)
            && Encoding.UTF8.GetString(type) == TrackerEventTypes.TrackerRevoked
            && record.Message.Value.Contains(imei, StringComparison.Ordinal));

        // The partition key is the vehicle, so a re-bind of the same IMEI to a new vehicle cannot
        // have its `tracker.bound` overtake the `tracker.unbound` that released it.
        Assert.Equal(vehicleId.ToString(), revocation.Message.Key);

        using var payload = JsonDocument.Parse(revocation.Message.Value);

        Assert.Equal(imei, payload.RootElement.GetProperty("imei").GetString());
        Assert.Contains(
            serial,
            payload.RootElement.GetProperty("credentialSerials").EnumerateArray().Select(item => item.GetString()));

        // The header only an outbox drain stamps, and the row it came from.
        Assert.True(revocation.Message.Headers.TryGetLastBytes("outboxId", out var outboxIdBytes));
        var outboxId = long.Parse(Encoding.UTF8.GetString(outboxIdBytes), CultureInfo.InvariantCulture);

        await using var connection = await harness.OpenAsync();
        var dispatchedAt = await connection.QuerySingleAsync<DateTimeOffset?>(
            "SELECT dispatched_at FROM prov.outbox WHERE id = @Id;", new { Id = outboxId });

        Assert.NotNull(dispatchedAt);
    }

    private async Task<ConsumeResult<string, string>> ConsumeAsync(Func<ConsumeResult<string, string>, bool> match)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"provisioning-outbox-test-{Guid.NewGuid()}",
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
