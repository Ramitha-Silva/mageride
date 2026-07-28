using Confluent.Kafka;
using MageRide.Dispatch.Configuration;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Messaging;

/// <summary>
/// Consumes <c>ride.events</c> and hands each envelope to <see cref="IRideEventHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the service.</b> The kernel (C002) ships an <c>IEventPublisher</c> and the
/// outbox dispatcher but no consumer — dispatch-svc is the first service that needs one. It reads
/// the kernel's <see cref="KafkaOptions"/> so a deployment configures one broker in one place.
/// <b>C024/C034/C039 should promote it to <c>MageRide.Shared.Messaging</c></b> once there is a
/// second caller; today a cross-cutting home would mean untested code in the kernel.
/// </para>
/// <para>
/// <b>Offsets are committed manually, after the handler returns.</b> D6' §2.3 makes delivery
/// at-least-once; auto-commit would make it at-most-once for anything that throws, which for
/// <c>ride.requested</c> means a passenger who is never offered a driver. A handler that throws
/// leaves the offset where it was, so the message is re-delivered.
/// </para>
/// <para>
/// <b>No DLQ.</b> D6' §2.3 specifies <c>&lt;topic&gt;.dlq</c> with three retries and jittered
/// backoff. That is C034's: a poison message here would block the partition, and the honest stub
/// is to log it loudly and keep the partition moving (an unparseable envelope is skipped; a
/// handler failure is retried by re-consuming).
/// </para>
/// </remarks>
public sealed class RideEventConsumer(
    IServiceProvider services,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<DispatchOptions> dispatchOptions,
    ILogger<RideEventConsumer> logger) : BackgroundService
{
    /// <summary>D6' §2.1's topic registry: <c>ride.events</c>, produced by ride-svc's outbox.</summary>
    public const string Topic = "ride.events";

    private readonly KafkaOptions _kafka = kafkaOptions?.Value ?? throw new ArgumentNullException(nameof(kafkaOptions));
    private readonly DispatchOptions _dispatch =
        dispatchOptions?.Value ?? throw new ArgumentNullException(nameof(dispatchOptions));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the synchronous start-up path: Consume() blocks, and a BackgroundService that blocks
        // in ExecuteAsync stalls the host's start.
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _dispatch.ConsumerGroup,
            ClientId = _kafka.ClientId,

            // Earliest, not latest: a dispatch-svc that restarts must still see the ride.requested
            // committed while it was down, or that passenger waits forever.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => logger.LogWarning(
                "Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))
            .Build();

        consumer.Subscribe(Topic);
        logger.LogInformation("Consuming {Topic} as group {Group}", Topic, _dispatch.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;

                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Failed to consume from {Topic}", Topic);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                await DispatchOneAsync(consumer, result, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task DispatchOneAsync(
        IConsumer<string, string> consumer, ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var envelope = RideEventEnvelope.TryParse(result.Message.Value);

        if (envelope is null)
        {
            // Not retryable — replaying it produces the same nothing and blocks the partition.
            logger.LogError(
                "Unparseable {Topic} message at offset {Offset}; skipping it (C034 lands the DLQ)",
                Topic, result.Offset.Value);

            consumer.Commit(result);
            return;
        }

        try
        {
            // A scope per message: IDispatchService takes scoped units of work, and a singleton
            // hosted service has no scope of its own.
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IRideEventHandler>();

            await handler.HandleAsync(envelope, cancellationToken);

            consumer.Commit(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately not committed: the message is re-delivered on the next poll of this
            // partition. Until C034's DLQ exists, a permanently failing message stalls its
            // partition — which is loud, and better than silently losing a booking.
            logger.LogError(
                ex, "Handling {EventType} for ride {RideId} failed; the offset is not committed",
                envelope.EventType, envelope.RideId);
        }
    }
}
