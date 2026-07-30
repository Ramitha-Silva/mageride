using System.Text.Json;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Messaging;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using StackExchange.Redis;
// `PositionProcessor` is both a namespace and the class inside it; from a MageRide.HotPath.*
// namespace the namespace wins, so the class is named explicitly (as in PositionProcessorTests).
using Processor = MageRide.HotPath.PositionProcessor.Processing.PositionProcessor;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// The D-18/T-07 filter and D-17's second line through the real processor, against a real Redis:
/// what a refused sample does <b>not</b> leave behind.
/// </summary>
/// <remarks>
/// <see cref="PlausibilityTests"/> asserts the verdicts; this asserts the consequences. They are
/// different claims — a gate that returns the right answer and writes the sample anyway would pass
/// the first and fail this one, and it is this one the DoD is written against.
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "PositionProcessor")]
public sealed class PositionGateTests(RedisFixture redis)
{
    private const string Live = TelemetryHeaders.Live;

    /// <summary>The DoD's first line, end to end.</summary>
    [Fact]
    public async Task A_teleporting_sample_is_rejected_and_leaves_the_live_state_untouched()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection);
        var db = connection.GetDatabase();

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);

        var first = await Process(processor, Sample(vehicleId, Samples.ColomboFort, seq: 1, at: captured));
        Assert.Equal(PositionOutcome.Indexed, first.Outcome);

        // Kandy, ten seconds later. 95 km at 34,000 km/h.
        var teleport = await Process(
            processor, Sample(vehicleId, Samples.Kandy, seq: 2, at: captured.AddSeconds(10)));

        Assert.Equal(PositionOutcome.Implausible, teleport.Outcome);
        Assert.Equal(PlausibilityCheck.Jump, teleport.Check);

        // The vehicle is still where the plausible sample put it — not 95 km inland.
        var position = await db.GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString());
        Assert.Equal(Samples.ColomboFort.Latitude, position!.Value.Latitude, precision: 3);

        // And the refused sample did not advance the watermark. If it had, every genuine sample
        // behind it would look like a replay — one spoofed frame would take the vehicle off the map
        // until its seq caught up.
        Assert.Equal(1, (long)await db.StringGetAsync(RedisKeys.VehicleSeq(vehicleId)));

        // …nor did it drag the plausible envelope with it: the next genuine sample is still measured
        // against Colombo Fort, so a spoofer cannot walk a vehicle across the island one refused
        // jump at a time.
        var recovered = await Process(
            processor,
            Sample(vehicleId, Samples.ColomboFort, seq: 3, at: captured.AddSeconds(20)));

        Assert.Equal(PositionOutcome.Indexed, recovered.Outcome);
    }

    [Fact]
    public async Task A_sample_with_an_accuracy_circle_over_two_hundred_metres_never_reaches_the_map()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection);

        // Discarded, not smoothed (this component's second fence). A first sample, so nothing else
        // could have caught it — the accuracy gate is the only thing standing here.
        var result = await Process(
            processor,
            Sample(vehicleId, Samples.ColomboFort, seq: 1) with { AccuracyM = 450 });

        Assert.Equal(PositionOutcome.Implausible, result.Outcome);
        Assert.Equal(PlausibilityCheck.Accuracy, result.Check);

        var db = connection.GetDatabase();
        Assert.Null(await db.GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString()));
        Assert.False(await db.KeyExistsAsync(RedisKeys.VehicleMeta(vehicleId)));
        Assert.False(await db.KeyExistsAsync(RedisKeys.VehicleSeq(vehicleId)));
    }

    [Fact]
    public async Task A_hardware_sample_whose_GNSS_clock_stood_still_is_refused()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);

        var tracker = Sample(vehicleId, Samples.ColomboFort, seq: 1, at: captured) with
        {
            Source = PositionSource.Gt06,
            SatCount = 9,
        };

        Assert.Equal(PositionOutcome.Indexed, (await Process(processor, tracker)).Outcome);

        // T-07: a frame carrying a real position and a clock that has not moved. Nothing else would
        // catch it — the vehicle has not gone anywhere.
        var stalled = await Process(processor, tracker with { Seq = 2 });

        Assert.Equal(PositionOutcome.Implausible, stalled.Outcome);
        Assert.Equal(PlausibilityCheck.Clock, stalled.Check);

        Assert.Equal(PositionOutcome.Indexed,
            (await Process(processor, tracker with { Seq = 3, SampleTs = captured.AddSeconds(5) })).Outcome);
    }

    [Fact]
    public async Task A_hardware_sample_with_too_few_satellites_is_refused()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection);

        var result = await Process(
            processor,
            Sample(vehicleId, Samples.ColomboFort, seq: 1) with
            {
                Source = PositionSource.Jt808,
                SatCount = 2,
            });

        Assert.Equal(PositionOutcome.Implausible, result.Outcome);
        Assert.Equal(PlausibilityCheck.Satellites, result.Check);
    }

    /// <summary>
    /// The backlog exemption, through the pipeline: a replayed sample is filtered by <c>seq</c> and
    /// not by where the vehicle is now.
    /// </summary>
    [Fact]
    public async Task A_replayed_sample_is_deduped_on_seq_rather_than_refused_as_a_teleport()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection);

        var now = DateTimeOffset.UtcNow;

        // The vehicle is live in Kandy…
        Assert.Equal(
            PositionOutcome.Indexed,
            (await Process(processor, Sample(vehicleId, Samples.Kandy, seq: 100, at: now))).Outcome);

        // …and bursts an hour of Colombo backlog. Judged as live samples these are 95 km teleports;
        // as replays they are history, and T-05's watermark is what decides them.
        var seen = await Process(
            processor,
            Sample(vehicleId, Samples.ColomboFort, seq: 40, at: now.AddHours(-1)),
            TelemetryHeaders.Replay);

        Assert.Equal(PositionOutcome.Replayed, seen.Outcome);

        // A backlog entry ahead of the watermark is indexed rather than refused. That is the R-17
        // contract: a vehicle that was offline sends the samples nobody has, and they are not
        // teleports just because they are old.
        var fresh = await Process(
            processor,
            Sample(vehicleId, Samples.ColomboFort, seq: 101, at: now.AddHours(-1)),
            TelemetryHeaders.Replay);

        Assert.Equal(PositionOutcome.Indexed, fresh.Outcome);
    }

    // --- D-17, second line (D5' §5.3, mqtt-topics.md §4) -----------------------------------------

    [Fact]
    public async Task A_vehicle_over_the_second_line_ceiling_is_dropped_and_flagged()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();

        // Ten per second over a two-second window is twenty; the twenty-first is over the ceiling.
        // The window is shortened only so the test does not have to publish two hundred samples —
        // the *rate* under test is the shipped one.
        var options = ProcessorParts.Defaults();
        options.RateCheckWindow = TimeSpan.FromSeconds(2);
        options.PublishNormalized = false;
        options.PlausibilityEnabled = false;

        var publisher = new ProcessorParts.CollectingPublisher();
        var processor = ProcessorParts.Build(connection, publisher, options);

        await AlignToWindowAsync(options.RateCheckWindow);

        var outcomes = new List<PositionOutcome>();

        for (var seq = 1; seq <= 25; seq++)
        {
            outcomes.Add(
                (await Process(processor, Sample(vehicleId, Samples.ColomboFort, seq))).Outcome);
        }

        Assert.Equal(20, outcomes.Count(outcome => outcome is PositionOutcome.Indexed));
        Assert.Equal(5, outcomes.Count(outcome => outcome is PositionOutcome.RateLimited));

        // Dropped, and flagged — `mqtt-topics.md` §4 says both, and the drop without the flag is a
        // vehicle silently disappearing from the map with nothing to investigate.
        var audit = Assert.Single(publisher.Messages, message => message.Topic == EventTopics.AuditEvents);

        // One report per vehicle per cooldown, however many samples went over: a vehicle at 50 msg/s
        // would otherwise turn a rate problem into a larger one on audit.events.
        Assert.Equal(vehicleId.ToString(), audit.Key);

        var body = JsonDocument.Parse(audit.Value.ToArray()).RootElement;

        Assert.Equal(AuditEvent.MqttRateViolation, body.GetProperty("action").GetString());
        Assert.Equal(AuditEvent.VehicleEntity, body.GetProperty("entityType").GetString());
        Assert.Equal(vehicleId.ToString(), body.GetProperty("entityId").GetString());

        var after = body.GetProperty("after");

        // `detectedBy` is what tells the two D-17 lines apart on one action — the bridge reports the
        // broker's 5 msg/s ceiling and drops nothing; this one drops.
        Assert.Equal("position-processor-svc", after.GetProperty("detectedBy").GetString());
        Assert.Equal("second", after.GetProperty("line").GetString());
        Assert.Equal("dropped", after.GetProperty("action").GetString());
        Assert.Equal(10, after.GetProperty("ceilingPerSecond").GetInt32());
    }

    [Fact]
    public async Task The_rate_counter_is_shared_in_Redis_rather_than_held_per_replica()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();

        var options = ProcessorParts.Defaults();
        options.RateCheckWindow = TimeSpan.FromSeconds(2);
        options.PublishNormalized = false;
        options.PlausibilityEnabled = false;

        // Two processors, as two replicas of the service. An in-process counter would let each pass
        // the full ceiling and the vehicle publish at twice it — which is precisely the failure
        // `mqtt-topics.md` §4 records for the bridge's counters.
        var first = ProcessorParts.Build(connection, new ProcessorParts.CollectingPublisher(), options);
        var second = ProcessorParts.Build(connection, new ProcessorParts.CollectingPublisher(), options);

        await AlignToWindowAsync(options.RateCheckWindow);

        var admitted = 0;

        for (var seq = 1; seq <= 30; seq++)
        {
            var processor = seq % 2 == 0 ? first : second;
            var result = await Process(processor, Sample(vehicleId, Samples.ColomboFort, seq));

            if (result.Outcome is PositionOutcome.Indexed)
            {
                admitted++;
            }
        }

        Assert.Equal(20, admitted);
    }

    // ---------------------------------------------------------------------------------------------

    private static Task<PositionResult> Process(
        Processor processor, PositionSample sample, string stream = Live) =>
        processor.ProcessAsync(
            PositionSampleCodec.Encode(sample), sample.VehicleId, stream, TestContext.Current.CancellationToken);

    private static PositionSample Sample(
        Guid vehicleId, GeoPoint point, long seq, DateTimeOffset? at = null) =>
        Samples.At(vehicleId, point, seq, sampleTs: at ?? DateTimeOffset.UtcNow);

    private Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);

    /// <summary>
    /// Every gate on, and <c>telemetry.normalized</c> off — these tests are about what a refused
    /// sample leaves behind, and a publisher that threw would mask the assertion.
    /// </summary>
    private static Processor NewProcessor(IConnectionMultiplexer connection) =>
        ProcessorParts.Build(connection, options: new PositionProcessorOptions { PublishNormalized = false });

    /// <summary>
    /// Waits until a fixed rate window has just opened.
    /// </summary>
    /// <remarks>
    /// The window is a wall-clock bucket, so a burst that started near a boundary would be counted
    /// across two of them and twice as many samples would be admitted. Without this the two rate
    /// tests below would fail a few times an hour, which is worse than failing always.
    /// </remarks>
    private static async Task AlignToWindowAsync(TimeSpan window)
    {
        var period = (long)window.TotalMilliseconds;

        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % period > period / 8)
        {
            await Task.Delay(20);
        }
    }
}
