using System.Text;
using Confluent.Kafka;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PositionProcessor.Processing;

/// <summary>
/// Consumes <c>telemetry.raw</c> and hands each payload to <see cref="IPositionProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The consume loop — manual offset commit after the handler returns, poison messages committed
/// past, everything else redelivered — is the kernel's <see cref="KafkaTopicConsumer"/>. Nothing
/// here throws <see cref="PoisonMessageException"/>: an undecodable payload is not a poison
/// <i>message</i>, it is a normal outcome of consuming from devices nobody controls, and
/// <see cref="PositionProcessor"/> counts and drops it. What does leave the offset uncommitted is a
/// Redis or Redpanda failure, which is exactly the case that should be retried.
/// </para>
/// <para>
/// <b>Latest, not earliest.</b> This is the one consumer on the platform that wants
/// <see cref="AutoOffsetReset.Latest"/>. A processor that has been down for ten minutes must not
/// wake up and replay ten minutes of stale positions into the live map — every one of them would be
/// written to <c>geo:live</c> and pushed to a passenger as though it were current, and the newest
/// sample would arrive last. R-08 and the <c>signalr-hub.md</c> §1.1 resync exist because the live
/// index is a <i>current-state</i> index; history is Timescale's (T-06). This only affects a group
/// with no committed offset — an existing group resumes where it was, and the <c>seq</c> watermark
/// discards whatever it re-reads. <c>PositionProcessor:StartFromEarliest</c> reverses it for a
/// deliberate backlog replay.
/// </para>
/// </remarks>
public sealed class TelemetryRawConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<PositionProcessorOptions> processorOptions,
    ILogger<TelemetryRawConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly PositionProcessorOptions _options =
        processorOptions?.Value ?? throw new ArgumentNullException(nameof(processorOptions));

    protected override string Topic => EventTopics.TelemetryRaw;

    protected override string GroupId => _options.ConsumerGroup;

    protected override AutoOffsetReset OffsetReset =>
        _options.StartFromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        // The key is the vehicleId the bridge read off the authenticated MQTT topic. Falling back to
        // the header, and then to the payload, would each be a weaker claim about who published;
        // an unkeyed record is one this service cannot attribute and must not index.
        if (!Guid.TryParse(message.Message.Key, out var vehicleId))
        {
            throw new PoisonMessageException(
                $"A {EventTopics.TelemetryRaw} record at offset {message.Offset.Value} carries no vehicle key " +
                $"('{message.Message.Key}'). mqtt-bridge-svc keys every record by the topic's vehicleId.");
        }

        await using var scope = services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IPositionProcessor>();

        var result = await processor.ProcessAsync(message.Message.Value, vehicleId, cancellationToken);

        if (result.Outcome is not PositionOutcome.Indexed)
        {
            Logger.LogDebug(
                "Vehicle {VehicleId} sample at offset {Offset} was {Outcome} (stream {Stream})",
                vehicleId, message.Offset.Value, result.Outcome, ReadStream(message));
        }
    }

    /// <summary>The <c>stream</c> header the bridge stamps: <c>live</c> or <c>replay</c>.</summary>
    private static string ReadStream(ConsumeResult<string, byte[]> message)
    {
        if (message.Message.Headers is null
            || !message.Message.Headers.TryGetLastBytes("stream", out var value))
        {
            return "unknown";
        }

        return Encoding.UTF8.GetString(value);
    }
}
