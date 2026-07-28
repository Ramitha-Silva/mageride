using Confluent.Kafka;
using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// The walking-skeleton seam, through a real broker: ride-svc books, its outbox dispatcher
/// publishes <c>ride.requested</c> to Redpanda, dispatch-svc consumes it and a driver ends up
/// holding an offer.
/// </summary>
/// <remarks>
/// Every other test in this suite calls <c>IDispatchService</c> directly, so a background consumer
/// cannot race an assertion. This one exists because that is precisely what those tests cannot
/// prove: that the topic name, the envelope shape, the consumer group and the offset handling all
/// line up with what C022 actually produces.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class EventPipelineTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    [Fact]
    public async Task A_booking_reaches_dispatch_through_ride_events_and_becomes_an_offer()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync("ride.events");
        await redpanda.CreateTopicAsync("dispatch.events");

        await using var harness = await DispatchHarness.StartAsync(
            postgres,
            redis,
            dispatchSettings: new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Dispatch:ConsumerEnabled"] = "true",
                // A group per run, so a previous run's committed offsets cannot make this one
                // skip the very message it is waiting for.
                ["Dispatch:ConsumerGroup"] = $"dispatch-svc-test-{Guid.NewGuid():N}",
                ["Outbox:DispatcherEnabled"] = "true",
            },
            rideSettings: new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                // ride-svc's LISTEN/NOTIFY dispatcher is what puts ride.requested on the wire
                // after COMMIT — R-13's "no phantom offers", now with a broker behind it.
                ["Outbox:DispatcherEnabled"] = "true",
            });

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var state = await WaitForRideStateAsync(harness, rideId, "Offered", TimeSpan.FromSeconds(60));
        Assert.Equal("Offered", state);

        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal(driver.DriverId, ride.OfferedDriverId);

        await using var connection = await harness.OpenAsync();

        var offer = await connection.QuerySingleAsync<OfferRow>(
            """
            SELECT id AS Id, ride_id AS RideId, driver_id AS DriverId, status AS Status,
                   sent_at AS SentAt, expires_at AS ExpiresAt, responded_at AS RespondedAt
              FROM dispatch.offers WHERE ride_id = @RideId;
            """,
            new { RideId = rideId });

        Assert.Equal(OfferStatuses.Offered, offer.Status);
        Assert.Equal(driver.DriverId, offer.DriverId);
        Assert.Equal(ride.CurrentOfferId, offer.Id);
    }

    [Fact]
    public async Task Offer_created_reaches_dispatch_events_only_after_the_commit()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync("dispatch.events");

        await using var harness = await DispatchHarness.StartAsync(
            postgres,
            redis,
            dispatchSettings: new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Outbox:DispatcherEnabled"] = "true",
            });

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);
        Assert.Equal(Dispatching.DispatchResult.Offered, outcome.Result);

        // R-13: the driver's push comes from this message, and the message exists only because a
        // transaction committed. A captured publisher would prove the dispatcher was called; a
        // real broker proves something was produced.
        var message = await ConsumeAsync("dispatch.events", rideId, TimeSpan.FromSeconds(60));

        Assert.NotNull(message);
        Assert.Contains("\"offer.created\"", message, StringComparison.Ordinal);
        Assert.Contains(outcome.OfferId!.Value.ToString(), message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(driver.DriverId.ToString(), message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ConsumeAsync(string topic, Guid key, TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapServers,
            GroupId = $"dispatch-assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));

            if (result?.Message is not null && string.Equals(result.Message.Key, key.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                consumer.Close();
                return result.Message.Value;
            }
        }

        consumer.Close();
        return null;
    }

    private static async Task<string> WaitForRideStateAsync(
        DispatchHarness harness, Guid rideId, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var state = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            state = (await harness.ReadRideAsync(rideId)).State;

            if (state == expected)
            {
                return state;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return state;
    }
}
