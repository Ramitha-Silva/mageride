using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Messaging;

/// <summary>
/// Thrown by a handler for a message that will never succeed, however many times it is redelivered.
/// The consumer commits past it rather than blocking the partition.
/// </summary>
/// <remarks>
/// The distinction this type draws is the whole reason the base class exists. An unparseable
/// envelope is poison — replaying it produces the same nothing forever — while a failed database
/// write is not, and committing past that one silently loses a booking. Anything else a handler
/// throws is treated as retryable.
/// </remarks>
public sealed class PoisonMessageException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// A <see cref="BackgroundService"/> that consumes one Redpanda topic and hands each message to
/// <see cref="HandleAsync"/> (D6' §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Offsets are committed manually, after the handler returns.</b> D6' §2.3 makes delivery
/// at-least-once; auto-commit would make it at-most-once for anything that throws, which on
/// <c>ride.requested</c> means a passenger who is never offered a driver and on
/// <c>telemetry.raw</c> means a vehicle that vanishes from the map. A handler that throws leaves
/// the offset where it was, so the message is redelivered.
/// </para>
/// <para>
/// <b>No DLQ.</b> D6' §2.3 specifies <c>&lt;topic&gt;.dlq</c> with three retries and jittered
/// backoff. Until a component owns that, a permanently failing message stalls its partition — which
/// is loud, and better than silently dropping it. A handler that knows a message is hopeless says
/// so by throwing <see cref="PoisonMessageException"/>, which is logged and committed past.
/// </para>
/// <para>
/// Values are <c>byte[]</c> because the topics do not agree on an encoding: <c>telemetry.raw</c>
/// carries the device's CBOR verbatim, while <c>ride.events</c> and <c>dispatch.events</c> carry
/// the outbox's UTF-8 JSON. Decoding belongs to the handler, which is the only thing that knows
/// which it is.
/// </para>
/// <para>
/// Introduced by C024 out of <c>Dispatch.Api/Messaging/RideEventConsumer.cs</c> (C023), which asked
/// for exactly this promotion once a second service needed a consumer.
/// </para>
/// </remarks>
public abstract class KafkaTopicConsumer(IOptions<KafkaOptions> kafkaOptions, ILogger logger) : BackgroundService
{
    private readonly KafkaOptions _kafka = kafkaOptions?.Value ?? throw new ArgumentNullException(nameof(kafkaOptions));

    /// <summary>The D6' §2.1 topic this consumer reads.</summary>
    protected abstract string Topic { get; }

    /// <summary>Consumer group. D6' §2: "consumer group per service".</summary>
    protected abstract string GroupId { get; }

    /// <summary>How long a single <c>Consume</c> waits before the loop checks for cancellation.</summary>
    protected virtual TimeSpan PollTimeout => TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Where a fresh consumer group starts. <see cref="AutoOffsetReset.Earliest"/> by default: a
    /// service that restarts must still see what was published while it was down.
    /// </summary>
    protected virtual AutoOffsetReset OffsetReset => AutoOffsetReset.Earliest;

    /// <summary>The logger the loop reports on. Exposed so derived classes share one.</summary>
    protected ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles one message. Returning commits the offset; throwing
    /// <see cref="PoisonMessageException"/> commits past it; anything else leaves the offset alone
    /// for redelivery.
    /// </summary>
    protected abstract Task HandleAsync(ConsumeResult<string, byte[]> message, CancellationToken cancellationToken);

    /// <summary>Hook for extra <see cref="ConsumerConfig"/> a service needs.</summary>
    protected virtual void ConfigureConsumer(ConsumerConfig config)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the synchronous start-up path: Consume() blocks, and a BackgroundService that blocks
        // in ExecuteAsync stalls the host's start.
        await Task.Yield();

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = GroupId,
            ClientId = _kafka.ClientId,
            AutoOffsetReset = OffsetReset,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
        };

        ConfigureConsumer(config);

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => Logger.LogWarning(
                "Kafka consumer error {Code}: {Reason}", error.Code, error.Reason))
            .Build();

        consumer.Subscribe(Topic);
        Logger.LogInformation("Consuming {Topic} as group {Group}", Topic, GroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;

                try
                {
                    result = consumer.Consume(PollTimeout);
                }
                catch (ConsumeException ex)
                {
                    Logger.LogWarning(ex, "Failed to consume from {Topic}", Topic);
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
        IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> result, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(result, cancellationToken);
            consumer.Commit(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PoisonMessageException ex)
        {
            Logger.LogError(
                ex, "Unusable message on {Topic} at offset {Offset}; committing past it", Topic, result.Offset.Value);

            consumer.Commit(result);
        }
        catch (Exception ex)
        {
            // Deliberately not committed: the message is redelivered on the next poll of this
            // partition.
            Logger.LogError(
                ex, "Handling {Topic} offset {Offset} failed; the offset is not committed",
                Topic, result.Offset.Value);
        }
    }
}
