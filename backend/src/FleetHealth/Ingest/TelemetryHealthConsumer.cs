using Confluent.Kafka;
using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth.Ingest;

/// <summary>
/// Consumes <c>telemetry.normalized</c> and keeps <c>telemetry.device_health.last_ping_at</c> — the
/// clock every US-3.13 state is measured against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <see cref="KafkaTopicConsumer"/>.</b> The kernel's consumer commits after every message,
/// which is right for a ride command and is a broker round trip per position here. This loop
/// accumulates one row per vehicle (<see cref="PingAccumulator"/>) and commits the offsets it has read
/// after the flush persists — the same shape C040 uses on the same topic, and for the same reason.
/// </para>
/// <para>
/// <b><see cref="AutoOffsetReset.Latest"/>, like C039 and unlike C040.</b> This is a current-state
/// rollup: replaying a day of samples writes values the very next sample overwrites, and the upsert's
/// <c>GREATEST</c> means the replay cannot even make a device look fresher than it is. So a replay is
/// work with no product, and the offsets a fresh group starts from should be the live edge.
/// <c>Health:StartFromEarliest</c> reverses it for a deliberate replay; the test harness is the only
/// thing that sets it.
/// </para>
/// <para>
/// <b>The ping clock is the platform's, not the device's.</b> <c>receivedTs</c> is stamped by
/// mqtt-bridge-svc, so a tracker with a wrong GNSS clock cannot pin itself Online or Stale — and for a
/// replayed backlog it is the instant the backlog arrived, which is when the device was demonstrably
/// reachable. A sample with no <c>receivedTs</c> falls back to the flush clock rather than to
/// <c>sampleTs</c>, for the same reason.
/// </para>
/// <para>
/// <b>A sample carrying no <c>fleetId</c> does not clear the stored one.</b> C040 is what denormalises
/// the fleet onto a sample (<c>mqtt-topics.md</c> §6) and it caches its misses, so an early sample can
/// legitimately arrive without one; treating that as "this vehicle left its fleet" would empty a
/// fleet's dashboard. The authority on fleet membership here is
/// <see cref="ProvisioningEventConsumer"/>, which hears it from the binding plane.
/// </para>
/// </remarks>
public sealed class TelemetryHealthConsumer : BackgroundService
{
    private readonly IDeviceHealthRepository _repository;
    private readonly KafkaOptions _kafka;
    private readonly FleetHealthOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelemetryHealthConsumer> _logger;
    private readonly PingAccumulator _accumulator;
    private readonly Dictionary<TopicPartition, long> _offsets = [];

    private DateTimeOffset _flushDue;
    private long _pingsApplied;
    private long _flushFailures;

    public TelemetryHealthConsumer(
        IDeviceHealthRepository repository,
        IOptions<KafkaOptions> kafkaOptions,
        IOptions<FleetHealthOptions> options,
        TimeProvider clock,
        ILogger<TelemetryHealthConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(kafkaOptions);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _kafka = kafkaOptions.Value;
        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _accumulator = new PingAccumulator(_options.MaxBufferedDevices);
        _flushDue = _clock.GetUtcNow() + _options.FlushInterval;
    }

    /// <summary>Device rows this replica has written. Read by the ingest test.</summary>
    public long PingsApplied => Interlocked.Read(ref _pingsApplied);

    /// <summary>Flushes that threw and were retried. A non-zero reading is the alert.</summary>
    public long FlushFailures => Interlocked.Read(ref _flushFailures);

    /// <summary>Devices buffered awaiting a flush.</summary>
    public int Buffered => _accumulator.Count;

    /// <summary>
    /// Drives one consume-and-flush cycle. Exposed so a test can drive the pipeline deterministically
    /// instead of waiting on the host's background loop.
    /// </summary>
    internal async Task<int> DrainOnceAsync(IConsumer<string, byte[]> consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        Consume(consumer, cancellationToken);
        return await FlushAsync(consumer, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the synchronous start-up path: Consume() blocks, and a BackgroundService that blocks in
        // ExecuteAsync stalls the host's start.
        await Task.Yield();

        using var consumer = BuildConsumer();

        consumer.Subscribe(EventTopics.TelemetryNormalized);

        _logger.LogInformation(
            "Tracking device health from {Topic} as group {Group}, flushing every {Interval}",
            EventTopics.TelemetryNormalized, _options.ConsumerGroup, _options.FlushInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Consume(consumer, stoppingToken);

                if (_clock.GetUtcNow() >= _flushDue)
                {
                    await FlushAsync(consumer, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Anything still buffered is uncommitted and will be redelivered.
        }
        finally
        {
            consumer.Close();
        }
    }

    internal IConsumer<string, byte[]> BuildConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            ClientId = _kafka.ClientId,
            AutoOffsetReset = _options.StartFromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        };

        return new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => _logger.LogWarning(
                "Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))

            // A revoked partition's buffered devices are forgotten rather than flushed: the member
            // taking the partition over resumes from the last committed offset and re-reads them.
            .SetPartitionsRevokedHandler((_, revoked) =>
            {
                foreach (var partition in revoked)
                {
                    _offsets.Remove(partition.TopicPartition);
                }
            })
            .Build();
    }

    private void Consume(IConsumer<string, byte[]> consumer, CancellationToken cancellationToken)
    {
        if (_accumulator.IsFull)
        {
            // The fence: stop consuming and leave the backlog on the topic (see the remarks on
            // PingAccumulator.IsFull).
            return;
        }

        ConsumeResult<string, byte[]>? result;

        try
        {
            // Short, so the flush deadline is honoured to within a poll rather than to within a batch.
            result = consumer.Consume(TimeSpan.FromMilliseconds(50));
        }
        catch (ConsumeException exception)
        {
            _logger.LogWarning(exception, "Failed to consume from {Topic}", EventTopics.TelemetryNormalized);
            return;
        }

        if (result?.Message is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The offset advances even for a payload this service cannot read: position-processor already
        // dropped the undecodable before republishing (C039), so anything unreadable here is a
        // producer that has changed shape, and redelivering it produces the same nothing for ever.
        _offsets[result.TopicPartition] = result.Offset.Value;

        var sample = PositionSampleCodec.TryDecode(result.Message.Value);

        if (sample is null || !sample.IsWellFormed)
        {
            _logger.LogError(
                "Undecodable {Topic} payload at {Partition}/{Offset}; committing past it",
                EventTopics.TelemetryNormalized, result.Partition.Value, result.Offset.Value);

            return;
        }

        _accumulator.Add(ToPing(sample, _clock.GetUtcNow()));
    }

    /// <summary>Projects the liveness facts out of a sample.</summary>
    internal static DeviceHealthPing ToPing(PositionSample sample, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return new DeviceHealthPing(
            sample.VehicleId,
            sample.FleetId,
            PingAt: (sample.ReceivedTs ?? now).ToUniversalTime(),
            SampleTs: sample.SampleTs.ToUniversalTime(),
            Source: (short)sample.Source,

            // The one US-3.12 diagnostic a position sample carries. Clamped away rather than clamped
            // to a bound: `sat_count` is SMALLINT and a decoder bug reporting 70,000 satellites must
            // not fail the whole flush batch on a CHECK.
            SatCount: sample.SatCount is { } sats and >= 0 and <= short.MaxValue ? (short)sats : null);
    }

    private async Task<int> FlushAsync(IConsumer<string, byte[]> consumer, CancellationToken cancellationToken)
    {
        _flushDue = _clock.GetUtcNow() + _options.FlushInterval;

        var pending = _accumulator.Drain();

        if (pending.Count == 0)
        {
            // Still commit: a partition carrying only unreadable payloads would otherwise never
            // advance and would be re-read for ever.
            Commit(consumer);
            return 0;
        }

        try
        {
            var applied = await _repository.UpsertPingsAsync(pending, cancellationToken);

            Interlocked.Add(ref _pingsApplied, applied);
            MageRideDiagnostics.DeviceHealthUpdates.Add(applied, new KeyValuePair<string, object?>("input", "ping"));

            // Only after the write. A process killed in between re-reads the samples, and every column
            // is GREATEST/COALESCE, so the redelivery is a no-op.
            Commit(consumer);

            return applied;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _flushFailures);

            // Nothing is lost: the rows go back into the accumulator and the offsets stay uncommitted,
            // so the buffer of record is Redpanda's seven-day retention (D6' §2.1) rather than this
            // process. The health plane falling behind costs an operator a stale dashboard; it costs
            // the live map and the system of record nothing, because this service writes to neither.
            _accumulator.Restore(pending);

            _logger.LogError(
                exception, "Could not write {Devices} device-health rows; retrying in place", pending.Count);

            return 0;
        }
    }

    private void Commit(IConsumer<string, byte[]> consumer)
    {
        if (_offsets.Count == 0)
        {
            return;
        }

        // offset + 1: Kafka commits the next offset to read, not the last one read.
        var positions = _offsets
            .Select(static entry => new TopicPartitionOffset(entry.Key, new Offset(entry.Value + 1)))
            .ToList();

        try
        {
            consumer.Commit(positions);
            _offsets.Clear();
        }
        catch (KafkaException exception)
        {
            // A rebalance in flight. The offsets stay pending and are committed after the next flush;
            // the write itself has already happened and is idempotent.
            _logger.LogWarning(exception, "Could not commit {Topic} offsets", EventTopics.TelemetryNormalized);
        }
    }
}
