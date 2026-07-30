using System.Text;
using Confluent.Kafka;
using MageRide.Fanout.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.Fanout.Messaging;

/// <summary>
/// Consumes <c>ride.events</c> — the input to the US-7.16 engagement rule, the ride participant
/// projection and three of the seven server-to-client events.
/// </summary>
/// <remarks>
/// <b><see cref="AutoOffsetReset.Earliest"/>, and that is the opposite of the position consumers.</b>
/// position-processor-svc and dispatch-svc's presence consumer read from Latest because a position
/// is current state and replaying old ones would push stale vehicles to passengers as live. A ride
/// is not current state: a ride accepted while this service was restarting must still take its
/// vehicle off the public map, and a passenger who reconnects must still be able to subscribe to it.
/// Replaying the topic is how a cold replica learns both.
/// </remarks>
public sealed class RideEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<FanoutOptions> fanoutOptions,
    ILogger<RideEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly FanoutOptions _fanout = fanoutOptions?.Value ?? throw new ArgumentNullException(nameof(fanoutOptions));

    protected override string Topic => EventTopics.RideEvents;

    protected override string GroupId => _fanout.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        var json = message.Message.Value is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty;
        var envelope = RideEventEnvelope.TryParse(json);

        if (envelope is null)
        {
            throw new PoisonMessageException(
                $"Unparseable {EventTopics.RideEvents} message at offset {message.Offset.Value}.");
        }

        try
        {
            // A scope per message: the handler's collaborators are singletons today, and the scope
            // is what stops that being a constraint on the next one.
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IRideEventHandler>();

            await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Fanning out {envelope.EventType} for ride {envelope.RideId} failed.", ex);
        }
    }
}
