using Confluent.Kafka;
using MageRide.Notification.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Messaging;

/// <summary>Handles one decoded event. Scoped, so a handler can take scoped services.</summary>
public interface IEventHandler
{
    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// The consumer loop for one topic, resolving a scoped <typeparamref name="THandler"/> per message.
/// </summary>
/// <remarks>
/// <para>
/// The loop itself — manual offset commit after the handler returns, poison messages committed past,
/// everything else redelivered — is <see cref="KafkaTopicConsumer"/>'s (C023, promoted by C024).
/// </para>
/// <para>
/// <b>Redelivery is safe here by construction.</b> Every notification is claimed by
/// <c>ux_notifications_dedupe</c> before anything is sent, so replaying a batch sends nothing twice
/// — which is what lets this consumer prefer redelivery over commit on any failure that is not a
/// parse failure. An unparseable envelope is poison and is committed past, because replaying it
/// produces the same nothing for ever.
/// </para>
/// <para>
/// <b>One consumer group across four topics, one subscription each.</b> D6' §2 says "consumer group
/// per service"; four separate <see cref="BackgroundService"/>s share the group id and each holds one
/// topic, so a slow registry event cannot delay a ride offer — which one consumer polling four
/// subscriptions would allow.
/// </para>
/// </remarks>
internal abstract class NotificationEventConsumer<THandler>(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger logger) : KafkaTopicConsumer(kafkaOptions, logger)
    where THandler : notnull, IEventHandler
{
    private readonly NotificationOptions _options =
        notificationOptions?.Value ?? throw new ArgumentNullException(nameof(notificationOptions));

    protected override string GroupId => _options.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = EventEnvelope.TryParse(message);

        if (envelope is null)
        {
            throw new PoisonMessageException(
                $"Unparseable {Topic} message at offset {message.Offset.Value} (no eventType, or not an object).");
        }

        try
        {
            // A scope per message: the handlers take repositories and the notification service, and
            // a singleton hosted service has no scope of its own.
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();

            await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Rethrown with the event named, so the uncommitted-offset line says which fact is stuck
            // rather than only which offset.
            throw new InvalidOperationException(
                $"Notifying for {envelope.EventType} ({envelope.Key}) failed.", exception);
        }
        finally
        {
            envelope.Dispose();
        }
    }
}

/// <summary>E-01's offers and DT-04/DT-08's Destination Filter events.</summary>
internal sealed class DispatchEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<DispatchEventConsumer> logger)
    : NotificationEventConsumer<DispatchEventHandler>(services, kafkaOptions, notificationOptions, logger)
{
    protected override string Topic => EventTopics.DispatchEvents;

    /// <summary>
    /// The offer push is the platform's tightest latency path, so this consumer polls harder than
    /// the others: a driver has fifteen seconds and a quarter of a second of poll delay is a quarter
    /// of a second off the E-01 budget.
    /// </summary>
    protected override TimeSpan PollTimeout => TimeSpan.FromMilliseconds(50);
}

/// <summary>The ride lifecycle, P-02's round-trip and AL-21's package branch.</summary>
internal sealed class RideEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<RideEventConsumer> logger)
    : NotificationEventConsumer<RideEventHandler>(services, kafkaOptions, notificationOptions, logger)
{
    protected override string Topic => EventTopics.RideEvents;
}

/// <summary>US-9.9's low balance and D-13's daily fee.</summary>
internal sealed class WalletEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<WalletEventConsumer> logger)
    : NotificationEventConsumer<WalletEventHandler>(services, kafkaOptions, notificationOptions, logger)
{
    protected override string Topic => EventTopics.WalletEvents;
}

/// <summary>US-2.14's registration result and E-03's document warnings.</summary>
internal sealed class RegistryEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<RegistryEventConsumer> logger)
    : NotificationEventConsumer<RegistryEventHandler>(services, kafkaOptions, notificationOptions, logger)
{
    protected override string Topic => EventTopics.RegistryEvents;
}
