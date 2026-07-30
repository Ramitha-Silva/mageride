using Confluent.Kafka;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.PersistenceWriter.Persistence;
using MageRide.HotPath.PersistenceWriter.Sampling;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PersistenceWriter.Ingest;

/// <summary>
/// Consumes <c>telemetry.normalized</c> in batches and writes them to the system of record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <c>KafkaTopicConsumer</c>, and the difference is the whole component.</b> The kernel's
/// consumer commits after every message, which is exactly right for a ride command and exactly wrong
/// here: ADD §9.5 item 5 asks for 1k rows / 500 ms / partition through <c>COPY</c>, and a per-message
/// commit would mean a round trip to the broker for every position on the platform. This loop
/// accumulates per partition and commits the batch's high-water mark after the database transaction
/// commits. Promotion into the kernel is the right move once a second service needs batching; there
/// is no second one yet.
/// </para>
/// <para>
/// <b>Offsets are committed after the write, never before.</b> That is the entire durability story:
/// a process killed mid-batch has committed nothing, so the batch is redelivered, and
/// <c>ux_positions_vehicle_seq</c> plus <c>ux_possample_session_minute</c> make the redelivery a
/// no-op instead of a duplicate. There is no in-flight state worth checkpointing anywhere else.
/// </para>
/// <para>
/// <b>A failing flush retries in place, forever.</b> Not committing is what makes a database outage
/// cost latency rather than data — the backlog accumulates on <c>telemetry.normalized</c>, which
/// D6' §2.1 retains for seven days, and the live map is untouched because it is Redis and this
/// service cannot reach it. When the in-process buffer hits
/// <see cref="PersistenceWriterOptions.MaxBufferedRows"/> the loop stops consuming altogether, so the
/// degradation is a growing broker backlog and not an OOM kill.
/// </para>
/// <para>
/// <b>One loop, not a queue and a pool of writers.</b> A 1,000-row binary <c>COPY</c> plus one
/// set-based insert is single-digit milliseconds, so one loop clears ADD §9.5's 40k rows/s budget by
/// a wide margin — and it makes "which offsets are safe to commit" a question with one answer instead
/// of a synchronisation problem.
/// </para>
/// </remarks>
public sealed class TelemetryWriterWorker(
    IPositionBatchWriter writer,
    IVehicleContextResolver context,
    INpgsqlConnectionFactory connectionFactory,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<PersistenceWriterOptions> writerOptions,
    TimeProvider clock,
    ILogger<TelemetryWriterWorker> logger) : BackgroundService
{
    private readonly KafkaOptions _kafka = kafkaOptions?.Value ?? throw new ArgumentNullException(nameof(kafkaOptions));

    private readonly PersistenceWriterOptions _options =
        writerOptions?.Value ?? throw new ArgumentNullException(nameof(writerOptions));

    private readonly Dictionary<TopicPartition, PartitionBatch> _batches = [];

    private long _rowsWritten;
    private long _flushes;
    private long _failedFlushes;

    /// <summary>Rows this replica has offered to the database. Read by the throughput test.</summary>
    public long RowsWritten => Interlocked.Read(ref _rowsWritten);

    /// <summary>Batches flushed. Read by the batching test.</summary>
    public long Flushes => Interlocked.Read(ref _flushes);

    /// <summary>Flushes that threw and were retried. A non-zero reading is the alert.</summary>
    public long FailedFlushes => Interlocked.Read(ref _failedFlushes);

    /// <summary>Rows buffered across every partition, awaiting a flush.</summary>
    public int Buffered => _batches.Values.Sum(static batch => batch.Rows.Count);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the synchronous start-up path: Consume() blocks, and a BackgroundService that blocks in
        // ExecuteAsync stalls the host's start.
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            ClientId = _kafka.ClientId,

            // Earliest, and unlike C039 this is the ordinary choice. The hypertable is the system of
            // record (T-06): a writer that was down for ten minutes must persist the ten minutes it
            // missed, which is the opposite of what position-processor wants from the same topic —
            // that one is maintaining a current-state index and must not replay stale positions into
            // it. Two consumers, two groups, two answers, on purpose.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => logger.LogWarning(
                "Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))
            // A revoked partition's buffered rows are dropped rather than flushed: the member that
            // takes the partition over resumes from the last committed offset and will re-receive
            // them. Flushing here would race that member for the same rows, which the unique indexes
            // would survive but which makes the offset the loop commits meaningless.
            .SetPartitionsRevokedHandler((_, revoked) => Forget(revoked))
            .Build();

        consumer.Subscribe(EventTopics.TelemetryNormalized);

        logger.LogInformation(
            "Writing {Topic} to telemetry.positions as group {Group}, {Rows} rows / {Interval}",
            EventTopics.TelemetryNormalized, _options.ConsumerGroup, _options.BatchRows,
            _options.FlushInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Consume(consumer, stoppingToken);
                await FlushDueAsync(consumer, force: false, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Everything still buffered is uncommitted and will be redelivered.
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>Takes one message, if the buffer has room for it.</summary>
    private void Consume(IConsumer<string, byte[]> consumer, CancellationToken stoppingToken)
    {
        if (Buffered >= _options.MaxBufferedRows)
        {
            // The fence: stop consuming and let the backlog sit on the topic. See the class remarks.
            MageRideDiagnostics.TelemetryWriterStalls.Add(1);
            return;
        }

        ConsumeResult<string, byte[]>? result;

        try
        {
            // Short, so the FlushInterval deadline is honoured to within a poll rather than to
            // within a batch.
            result = consumer.Consume(TimeSpan.FromMilliseconds(50));
        }
        catch (ConsumeException ex)
        {
            logger.LogWarning(ex, "Failed to consume from {Topic}", EventTopics.TelemetryNormalized);
            return;
        }

        if (result?.Message is null || stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var sample = PositionSampleCodec.TryDecode(result.Message.Value);

        if (sample is null || !sample.IsWellFormed)
        {
            // position-processor already dropped the undecodable and the malformed before
            // republishing here (C039), so anything reaching this branch is a producer that has
            // changed shape. The offset is still advanced, because replaying it produces the same
            // nothing forever — and the batch's high-water mark is what carries it past.
            logger.LogError(
                "Undecodable {Topic} payload at {Partition}/{Offset}; committing past it",
                EventTopics.TelemetryNormalized, result.Partition.Value, result.Offset.Value);

            Batch(result.TopicPartition).Skip(result.Offset.Value, clock.GetUtcNow());
            return;
        }

        Batch(result.TopicPartition).Add(sample, result.Offset.Value, clock.GetUtcNow());
    }

    /// <summary>Flushes every partition that is full, aged, or — on shutdown — non-empty.</summary>
    private async Task FlushDueAsync(
        IConsumer<string, byte[]> consumer, bool force, CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow();

        // Materialised: a flush can be retried across many iterations and the dictionary is mutated
        // when a partition empties.
        foreach (var (partition, batch) in _batches.ToArray())
        {
            if (batch.Rows.Count == 0 && batch.HighWaterMark is null)
            {
                continue;
            }

            var due = force
                      || batch.Rows.Count >= _options.BatchRows
                      || (batch.OpenedAt is { } opened && now - opened >= _options.FlushInterval);

            if (!due)
            {
                continue;
            }

            await FlushAsync(consumer, partition, batch, stoppingToken);
        }
    }

    /// <summary>
    /// Writes one partition's batch and commits its offsets. Retries in place until it succeeds or
    /// the service stops.
    /// </summary>
    private async Task FlushAsync(
        IConsumer<string, byte[]> consumer,
        TopicPartition partition,
        PartitionBatch batch,
        CancellationToken stoppingToken)
    {
        var rows = await ResolveFleetsAsync(batch.Rows, stoppingToken);
        var delay = _options.RetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await writer.WriteAsync(rows, stoppingToken);

                Interlocked.Add(ref _rowsWritten, outcome.Rows);
                Interlocked.Increment(ref _flushes);

                // Only now. An offset committed before the transaction would turn a crash between
                // the two into silently lost telemetry, which is the one failure a system of record
                // may not have.
                if (batch.HighWaterMark is { } offset)
                {
                    consumer.Commit([new TopicPartitionOffset(partition, new Offset(offset + 1))]);
                }

                batch.Reset();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedFlushes);
                MageRideDiagnostics.TelemetryFlushFailures.Add(1);

                logger.LogError(
                    ex,
                    "Writing {Rows} rows from {Partition} failed; retrying in {Delay}. Offsets are " +
                    "uncommitted, so nothing is lost — the backlog sits on {Topic}.",
                    rows.Count, partition.Partition.Value, delay, EventTopics.TelemetryNormalized);

                try
                {
                    await Task.Delay(delay, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = delay + delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay + delay;
            }
        }
    }

    /// <summary>
    /// Denormalises the owning fleet onto each row — <c>mqtt-topics.md</c> §6's "C040 must populate
    /// it".
    /// </summary>
    /// <remarks>
    /// A sample that already carries a <c>fleetId</c> keeps it: the publisher knew, and re-resolving
    /// would let a stale cache overwrite a fresh fact. Its own connection, outside the write
    /// transaction, so a lookup failure retries the whole batch rather than half-poisoning one.
    /// </remarks>
    internal async Task<List<PositionRow>> ResolveFleetsAsync(
        IReadOnlyList<PositionSample> samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var unresolved = samples
            .Where(static sample => sample.FleetId is null)
            .Select(static sample => sample.VehicleId)
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<Guid, Guid> fleets = new Dictionary<Guid, Guid>();

        if (unresolved.Length > 0)
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            fleets = await context.ResolveFleetsAsync(connection, null, unresolved, cancellationToken);
        }

        var rows = new List<PositionRow>(samples.Count);

        foreach (var sample in samples)
        {
            rows.Add(new PositionRow(
                sample,
                sample.FleetId ?? (fleets.TryGetValue(sample.VehicleId, out var fleetId) ? fleetId : null)));
        }

        return rows;
    }

    /// <summary>Flushes everything still buffered. Called by tests that drive the loop directly.</summary>
    internal Task FlushAllAsync(IConsumer<string, byte[]> consumer, CancellationToken cancellationToken) =>
        FlushDueAsync(consumer, force: true, cancellationToken);

    private PartitionBatch Batch(TopicPartition partition)
    {
        if (!_batches.TryGetValue(partition, out var batch))
        {
            batch = new PartitionBatch();
            _batches[partition] = batch;
        }

        return batch;
    }

    private void Forget(IReadOnlyList<TopicPartitionOffset> revoked)
    {
        foreach (var partition in revoked)
        {
            if (_batches.Remove(partition.TopicPartition, out var batch) && batch.Rows.Count > 0)
            {
                logger.LogInformation(
                    "Partition {Partition} was revoked with {Rows} rows buffered; they are uncommitted " +
                    "and will be redelivered to whichever member takes it over",
                    partition.Partition.Value, batch.Rows.Count);
            }
        }
    }

    /// <summary>
    /// One partition's accumulating batch.
    /// </summary>
    /// <remarks>
    /// <see cref="HighWaterMark"/> is tracked separately from the rows because an undecodable payload
    /// contributes an offset and no row: without that, a partition carrying nothing but garbage would
    /// never commit and would be redelivered forever.
    /// </remarks>
    private sealed class PartitionBatch
    {
        public List<PositionSample> Rows { get; } = [];

        /// <summary>Highest offset in this batch, decodable or not.</summary>
        public long? HighWaterMark { get; private set; }

        /// <summary>
        /// When the batch's first message arrived — the <see cref="PersistenceWriterOptions.FlushInterval"/>
        /// deadline runs from here, so a vehicle reporting once a minute is not held for as long as the
        /// partition keeps receiving other traffic.
        /// </summary>
        public DateTimeOffset? OpenedAt { get; private set; }

        public void Add(PositionSample sample, long offset, DateTimeOffset now)
        {
            Rows.Add(sample);
            Advance(offset, now);
        }

        public void Skip(long offset, DateTimeOffset now) => Advance(offset, now);

        public void Reset()
        {
            Rows.Clear();
            HighWaterMark = null;
            OpenedAt = null;
        }

        private void Advance(long offset, DateTimeOffset now)
        {
            HighWaterMark = HighWaterMark is { } current ? Math.Max(current, offset) : offset;
            OpenedAt ??= now;
        }
    }
}
