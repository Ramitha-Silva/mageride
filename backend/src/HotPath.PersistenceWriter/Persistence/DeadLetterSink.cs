using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Shared.Telemetry;

namespace MageRide.HotPath.PersistenceWriter.Persistence;

/// <summary>Where a sample the system of record will never accept goes.</summary>
public interface IDeadLetterSink
{
    /// <summary>Publishes one refused sample onto <c>telemetry.normalized.dlq</c>.</summary>
    Task SendAsync(PositionSample sample, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The D6' §2.3 dead-letter topic for <c>telemetry.normalized</c>.
/// </summary>
/// <remarks>
/// <para>
/// D6' §2.3 fixes the convention — <c>&lt;topic&gt;.dlq</c> carrying
/// <c>{originalOffset, error, attempts}</c> — and <c>EventTopics.DeadLetter</c> has spelled it since
/// C024 with nothing writing one. This is the first, and it is deliberately scoped to
/// <b>this service's own input topic only</b>: <c>telemetry.raw.dlq</c> is a different claim, made
/// against the kernel's shared <c>KafkaTopicConsumer</c>, and belongs to whoever changes that (see
/// the C040 handoff).
/// </para>
/// <para>
/// <b>The envelope carries the sample, not the offset alone.</b> D6' §2.3's three fields are enough
/// to find a message on a topic that still has it, and <c>telemetry.normalized</c> is retained seven
/// days — which is shorter than the time it takes anyone to notice a fleet's firmware is emitting
/// rows Postgres refuses. So the decoded sample travels with it, and the row can be repaired and
/// replayed from the DLQ itself.
/// </para>
/// <para>
/// <b>A failure to publish is swallowed.</b> The alternative is a batch that cannot be dead-lettered
/// and therefore cannot be committed past, which is the stall the DLQ exists to avoid — a broker
/// problem would turn one poison row into a stopped shard. The row is logged at error level either
/// way, and the counter is what an alert watches.
/// </para>
/// </remarks>
public sealed class DeadLetterSink(
    IEventPublisher publisher,
    TimeProvider clock,
    ILogger<DeadLetterSink> logger) : IDeadLetterSink
{
    /// <summary>The topic — <c>telemetry.normalized.dlq</c>.</summary>
    public static readonly string Topic = EventTopics.DeadLetter(EventTopics.TelemetryNormalized);

    public async Task SendAsync(PositionSample sample, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var envelope = new
        {
            // D6' §2.3's three fields. `attempts` is 1 and says so honestly: the batch was tried
            // once as a batch and once as a row, and a row this table refuses will be refused
            // identically however many times it is offered — retrying it is not the recovery path,
            // fixing the producer is.
            originalTopic = EventTopics.TelemetryNormalized,
            error = reason,
            attempts = 1,
            deadLetteredAt = clock.GetUtcNow(),
            deadLetteredBy = PersistenceWriterApplication.ServiceName,

            // Beyond the spec's envelope, and the reason this is useful at all: the sample itself,
            // so a repaired row can be replayed from here rather than from a topic that has aged out.
            vehicleId = sample.VehicleId,
            seq = sample.Seq,
            sampleTs = sample.SampleTs,
            sample = new
            {
                sample.Lat,
                sample.Lng,
                sample.SpeedMps,
                sample.HeadingDeg,
                sample.AccuracyM,
                sample.Hdop,
                sample.SatCount,
                source = (int)sample.Source,
                sample.Mode,
                sample.VehicleType,
                sample.FleetId,
                sample.TripId,
                sample.ReceivedTs,
            },
        };

        try
        {
            await publisher.PublishAsync(
                new EventMessage(
                    Topic,
                    // Keyed by vehicle, like every other topic on the telemetry plane (D6' §2.1), so
                    // one vehicle's refused rows stay in the order they were refused.
                    sample.VehicleId.ToString(),
                    JsonSerializer.SerializeToUtf8Bytes(envelope, MageRideJson.Options)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Could not dead-letter vehicle {VehicleId} seq {Seq} onto {Topic}; the row is dropped " +
                "and counted. Reason it was refused: {Reason}",
                sample.VehicleId, sample.Seq, Topic, reason);
        }
    }
}
