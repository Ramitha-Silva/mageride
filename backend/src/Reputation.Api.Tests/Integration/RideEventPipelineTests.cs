using System.Text;
using Confluent.Kafka;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Tests.Infrastructure;
using MageRide.Ride.Domain;
using MageRide.Ride.Rides;
using MageRide.Shared.Messaging;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.Reputation.Tests.Integration;

/// <summary>
/// The live intake: <c>ride.events</c> off a real Redpanda, into the counters.
/// </summary>
/// <remarks>
/// <para>
/// D6' §2.1 lists reputation-svc among <c>ride.events</c>' consumers and ride-svc (C032) publishes
/// rather than calling the gRPC reports, so <b>this is the path that actually counts anything in a
/// deployed stack</b>. The envelopes are built by ride-svc's own <see cref="RideEvents"/> rather
/// than hand-written JSON, because the two services meet on that envelope and nothing else checks
/// that they agree about it — a producer-side field rename would fail here rather than in
/// production, silently, as a counter that stopped moving.
/// </para>
/// <para>
/// The consumer is the only worker turned on for these tests.
/// </para>
/// </remarks>
[Collection(ReputationCollection.Name)]
public sealed class RideEventPipelineTests(PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_driver_cancel_published_by_ride_svc_delists_the_driver()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(EventTopics.RideEvents);

        await using var harness = await StartConsumingAsync();

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();
        var ride = Ride(passenger, driver, "CancelledByDriver");

        await ProduceAsync(RideEvents.BuildReputation(
            ride,
            new RideReputationPayload(
                DriverId: driver,
                VehicleId: null,
                PassengerId: passenger,
                FromState: "Accepted",
                ToState: "CancelledByDriver",
                ReasonCode: RideReasonCodes.DriverCancelled,
                SystemInitiated: false),
            Guid.NewGuid(),
            harness.Clock.GetUtcNow()));

        await WaitForAsync(async () => (await harness.ReadBlockStateAsync(driver))?.State == BlockStates.Delisted);

        var stored = await harness.ReadBlockStateAsync(driver);
        Assert.Equal(BlockReasons.DriverCancelDelist, stored!.Reason);
    }

    /// <summary>DoD 4, through the real topic: three cancels disable, a completion resets.</summary>
    [Fact]
    public async Task Three_rider_cancels_disable_booking_and_a_completion_lifts_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(EventTopics.RideEvents);

        await using var harness = await StartConsumingAsync();

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        for (var i = 0; i < 3; i++)
        {
            await ProduceAsync(RideEvents.Build(
                RideEventTypes.Cancelled,
                Ride(passenger, driver, "CancelledByRiderAfterAccept"),
                Guid.NewGuid(),
                harness.Clock.GetUtcNow(),
                reasonCode: RideReasonCodes.RiderCancelledAfterAccept));
        }

        await WaitForAsync(async () =>
            (await harness.ReadBlockStateAsync(passenger))?.State == BlockStates.BookingDisabled);

        await ProduceAsync(RideEvents.Build(
            RideEventTypes.Completed,
            Ride(passenger, driver, "Completed"),
            Guid.NewGuid(),
            harness.Clock.GetUtcNow()));

        await WaitForAsync(async () => (await harness.ReadCountersAsync(passenger))?.CancellationsContinuous == 0);

        // The state follows the counter — the run is what disabled booking, so clearing it clears
        // the block. (The cooldown would have lifted it too; the completed ride is faster.)
        Assert.Equal(BlockStates.Ok, (await harness.ReadBlockStateAsync(passenger))!.State);

        // Both sides of the completed ride were counted, which is what the E-07 pair detector reads.
        Assert.Equal(0, (await harness.ReadCountersAsync(driver))!.CancellationsContinuous);
    }

    /// <summary>D5' §7.2: a pre-acceptance cancel moves nothing, however many arrive.</summary>
    [Fact]
    public async Task Pre_acceptance_cancels_move_no_counter()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(EventTopics.RideEvents);

        await using var harness = await StartConsumingAsync();

        var passenger = await harness.CreateUserAsync();
        var driver = await harness.CreateDriverAsync();

        for (var i = 0; i < 5; i++)
        {
            await ProduceAsync(RideEvents.Build(
                RideEventTypes.Cancelled,
                Ride(passenger, driver, "CancelledByRiderBeforeAccept"),
                Guid.NewGuid(),
                harness.Clock.GetUtcNow(),
                reasonCode: RideReasonCodes.RiderCancelledBeforeAccept));
        }

        // A completion afterwards proves the consumer really did keep up — without it, "no counter
        // moved" and "nothing was consumed" look the same.
        await ProduceAsync(RideEvents.Build(
            RideEventTypes.Completed, Ride(passenger, driver, "Completed"), Guid.NewGuid(), harness.Clock.GetUtcNow()));

        await WaitForAsync(async () => await harness.ReadCountersAsync(passenger) is not null);

        Assert.Equal(0, (await harness.ReadCountersAsync(passenger))!.CancellationsContinuous);
        Assert.Equal(BlockStates.Ok, (await harness.ReadBlockStateAsync(passenger))!.State);
    }

    // -----------------------------------------------------------------------------------------

    private Task<ReputationHarness> StartConsumingAsync() =>
        ReputationHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Reputation:ConsumerEnabled"] = "true",

                // A group per test class run, so a suite re-run does not start from another run's
                // committed offsets — AutoOffsetReset.Earliest would otherwise skip this test's
                // messages entirely on the second execution.
                ["Reputation:ConsumerGroup"] = $"reputation-svc-test-{Guid.NewGuid():N}",
            });

    private async Task ProduceAsync(OutboxRecord record)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = redpanda.BootstrapServers }).Build();

        // Keyed by the aggregate id, exactly as the outbox dispatcher does (D6' §2.3): ordering per
        // ride is what stops a cancellation overtaking the completion that preceded it.
        await producer.ProduceAsync(
            EventTopics.RideEvents,
            new Message<string, byte[]>
            {
                Key = record.AggregateId.ToString(),
                Value = Encoding.UTF8.GetBytes(record.Payload),
            });

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Condition was still false after {Patience.TotalSeconds:0} s.");
    }

    /// <summary>A ride row shaped as ride-svc would hold it at the moment the event was raised.</summary>
    private static RideRow Ride(Guid passengerId, Guid driverId, string state) =>
        new(
            Id: Guid.NewGuid(),
            PassengerId: passengerId,
            ClientRequestId: Guid.NewGuid(),
            BookerId: passengerId,
            RiderId: null,
            RiderPhoneHash: null,
            RiderName: null,
            IsProxy: false,
            Kind: 0,
            VehicleType: "three_wheeler",
            PickupGeo: new GeoPoint(6.9344, 79.8428),
            DropoffGeo: new GeoPoint(6.8514, 79.8653),
            State: state,
            AcceptedDriverId: driverId,
            AcceptedVehicleId: null,
            OfferedDriverId: null,
            OfferedVehicleId: null,
            CurrentOfferId: null,
            OfferExpiresAt: null,
            PaymentMethod: "cash",

            // The Δ C037 package columns: a passenger ride carries none of them.
            PackageSize: null,
            PackageDescription: null,
            RecipientName: null,
            RecipientPhone: null,
            PickupOtpAttempts: 0,
            DeliveryOtpAttempts: 0,
            FareEstimateMinor: 74_000,
            FareSurchargeMinor: 0,
            Currency: "LKR",
            Version: 4,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            TerminalAt: null);
}
