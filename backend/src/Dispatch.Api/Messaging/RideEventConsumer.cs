using System.Text;
using Confluent.Kafka;
using MageRide.Dispatch.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Messaging;

/// <summary>
/// Consumes <c>ride.events</c> and hands each envelope to <see cref="IRideEventHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The consume loop itself — manual offset commit after the handler returns, poison messages
/// committed past, everything else redelivered — lives in <see cref="KafkaTopicConsumer"/>
/// (<c>MageRide.Shared.Messaging</c>). C023 wrote it here because dispatch-svc was the first
/// service that needed a consumer and asked for the promotion once there was a second;
/// position-processor-svc (C024) is that second caller.
/// </para>
/// <para>
/// <b>No DLQ.</b> D6' §2.3 specifies <c>ride.events.dlq</c> with three retries and jittered
/// backoff. That is C034's: until then an unparseable envelope is skipped (replaying it produces
/// the same nothing) and a handler failure stalls its partition, which is loud and better than
/// silently losing a booking.
/// </para>
/// </remarks>
public sealed class RideEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<DispatchOptions> dispatchOptions,
    ILogger<RideEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    /// <summary>D6' §2.1's topic registry: <c>ride.events</c>, produced by ride-svc's outbox.</summary>
    public const string TopicName = "ride.events";

    private readonly DispatchOptions _dispatch =
        dispatchOptions?.Value ?? throw new ArgumentNullException(nameof(dispatchOptions));

    protected override string Topic => TopicName;

    protected override string GroupId => _dispatch.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        // The outbox dispatcher produces UTF-8 JSON (MageRideJson.StorageOptions); telemetry topics
        // carry CBOR, which is why the base class hands out bytes and each consumer decodes.
        var json = message.Message.Value is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty;
        var envelope = RideEventEnvelope.TryParse(json);

        if (envelope is null)
        {
            throw new PoisonMessageException(
                $"Unparseable {TopicName} message at offset {message.Offset.Value} (C034 lands the DLQ).");
        }

        try
        {
            // A scope per message: IDispatchService takes scoped units of work, and a singleton
            // hosted service has no scope of its own.
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IRideEventHandler>();

            await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rethrown with the aggregate named, so the uncommitted-offset log line says which ride
            // is stuck rather than only which offset.
            throw new InvalidOperationException(
                $"Handling {envelope.EventType} for ride {envelope.RideId} failed.", ex);
        }
    }
}
