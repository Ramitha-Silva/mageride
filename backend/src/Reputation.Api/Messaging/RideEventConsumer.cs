using System.Text;
using Confluent.Kafka;
using MageRide.Reputation.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Reputation.Messaging;

/// <summary>
/// Consumes <c>ride.events</c> and hands each envelope to <see cref="IRideEventHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The loop — manual offset commit after the handler returns, poison messages committed past,
/// everything else redelivered — is <see cref="KafkaTopicConsumer"/>'s (C023, promoted by C024).
/// </para>
/// <para>
/// <b>Redelivery is safe here by construction, not by luck.</b> Every fact is claimed in
/// <c>reputation.intake_log</c> before it moves a counter, so an uncommitted offset replaying a
/// batch counts nothing twice — which is what lets this consumer prefer redelivery over commit on
/// any failure that is not a parse failure.
/// </para>
/// <para>
/// <b>No DLQ.</b> D6' §2.3 specifies <c>ride.events.dlq</c> and no component owns it yet (C034);
/// an unparseable envelope is committed past because replaying it produces the same nothing, and a
/// handler failure stalls its partition, which is loud and better than losing a counted fact.
/// </para>
/// </remarks>
public sealed class RideEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<ReputationOptions> reputationOptions,
    ILogger<RideEventConsumer> logger) : KafkaTopicConsumer(kafkaOptions, logger)
{
    private readonly ReputationOptions _reputation =
        reputationOptions?.Value ?? throw new ArgumentNullException(nameof(reputationOptions));

    protected override string Topic => EventTopics.RideEvents;

    protected override string GroupId => _reputation.ConsumerGroup;

    protected override async Task HandleAsync(
        ConsumeResult<string, byte[]> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The outbox dispatcher produces UTF-8 JSON (MageRideJson.StorageOptions); the telemetry
        // topics carry CBOR, which is why the base class hands out bytes and each consumer decodes.
        var json = message.Message.Value is { Length: > 0 } value ? Encoding.UTF8.GetString(value) : string.Empty;
        var envelope = RideEventEnvelope.TryParse(json);

        if (envelope is null)
        {
            throw new PoisonMessageException(
                $"Unparseable {EventTopics.RideEvents} message at offset {message.Offset.Value} (C034 lands the DLQ).");
        }

        try
        {
            // A scope per message: IReputationService takes scoped units of work and a singleton
            // hosted service has no scope of its own.
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IRideEventHandler>();

            await handler.HandleAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rethrown with the aggregate named, so the uncommitted-offset line says which ride is
            // stuck rather than only which offset.
            throw new InvalidOperationException(
                $"Counting {envelope.EventType} for ride {envelope.RideId} failed.", ex);
        }
    }
}
