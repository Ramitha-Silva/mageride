using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.Shared.Messaging;
using MageRide.TestKit;
using MageRide.TripState.Sessions;
using MageRide.TripState.Tests.Infrastructure;

namespace MageRide.TripState.Tests.Integration;

/// <summary>
/// The half a row in a table cannot show: a session transition reaches Redpanda <em>through the
/// outbox</em> (D6' §2.4, R-13, E-09), on D6' §2.1's <c>trip.events</c>.
/// </summary>
/// <remarks>
/// An end that committed and then failed to publish leaves fanout-svc showing a finished journey
/// on the passenger live map, with no way for the driver to take it off — which is what the
/// outbox exists to prevent. A capturing publisher would prove the dispatcher was called; it would
/// not prove anything was produced, and it could not tell a direct publish from an outbox drain.
/// So this asserts the property that distinguishes the two: the <c>outboxId</c> header the
/// dispatcher stamps, and the row it came from marked dispatched.
/// </remarks>
[Collection<TripStateCollection>]
public sealed class OutboxPipelineTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private const string Topic = EventTopics.TripEvents;

    [Fact]
    public async Task A_session_start_and_end_reach_redpanda_through_the_outbox()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(Topic);

        await using var harness = await TripStateHarness.StartAsync(
            postgres,
            redis,
            redpanda,
            new Dictionary<string, string?>
            {
                // LISTEN/NOTIFY is the trigger under test; a short poll would mask a broken listener.
                ["Outbox:PollInterval"] = "00:01:00",
            });

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(bearer, vehicleId);
        var sessionId = started.GetProperty("sessionId").GetString()!;

        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer)).StatusCode);

        var ended = await ConsumeAsync(record =>
            record.Message.Headers.TryGetLastBytes("eventType", out var type)
            && Encoding.UTF8.GetString(type) == SessionEventTypes.SessionEnded
            && record.Message.Value.Contains(sessionId, StringComparison.Ordinal));

        // The partition key is the vehicle, so an end and the start that follows it cannot be
        // reordered — a consumer that saw them the other way round would remove the vehicle from
        // the live map immediately after adding it back.
        Assert.Equal(vehicleId.ToString(), ended.Message.Key);

        using var payload = JsonDocument.Parse(ended.Message.Value);

        Assert.Equal(sessionId, payload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(driverId.ToString(), payload.RootElement.GetProperty("driverId").GetString());
        Assert.Equal("driver_ended", payload.RootElement.GetProperty("endReason").GetString());

        // The header only an outbox drain stamps, and the row it came from.
        Assert.True(ended.Message.Headers.TryGetLastBytes("outboxId", out var outboxIdBytes));
        var outboxId = long.Parse(Encoding.UTF8.GetString(outboxIdBytes), CultureInfo.InvariantCulture);

        await using var connection = await harness.OpenAsync();

        // POLLED, because arriving on the topic does not mean the row has been stamped yet.
        // The dispatcher produces first and marks `dispatched_at` after the delivery report —
        // it must, or a crash between the two would lose an event the broker never took — so
        // between this test consuming the message and the UPDATE committing there is a window of
        // milliseconds in which the column is legitimately still NULL. Reading once landed inside
        // it on CI (2026-08-15, `main` @ 92d7b50: "Assert.NotNull() Failure: Value of type
        // 'Nullable<DateTimeOffset>' does not have a value").
        //
        // The assertion is unchanged in substance: a dispatcher that never stamps the row still
        // fails, five seconds later and with a message that says which row.
        DateTimeOffset? dispatchedAt = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            dispatchedAt = await connection.QuerySingleAsync<DateTimeOffset?>(
                "SELECT dispatched_at FROM trips.outbox WHERE id = @Id;", new { Id = outboxId });

            if (dispatchedAt is not null)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(
            dispatchedAt is not null,
            $"trips.outbox row {outboxId} reached Redpanda but was never marked dispatched. "
            + "The drain produces and then stamps; a row that stays NULL is a drain that "
            + "published and did not record it, which redelivers the event on the next sweep.");
    }

    private async Task<ConsumeResult<string, string>> ConsumeAsync(Func<ConsumeResult<string, string>, bool> match)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"trip-state-outbox-test-{Guid.NewGuid()}",
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
