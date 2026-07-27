using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Messaging;

/// <summary>
/// <see cref="IEventPublisher"/> over <c>Confluent.Kafka</c> against Redpanda (D6' §2).
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BootstrapServers))
        {
            throw new InvalidOperationException(
                "Kafka:BootstrapServers is not configured (D7' §4.1 lists it as required for every .NET service).");
        }

        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            Acks = ParseAcks(settings.Acks),
            EnableIdempotence = settings.EnableIdempotence,
            LingerMs = settings.LingerMs,
            MessageTimeoutMs = settings.MessageTimeoutMs,
            CompressionType = ParseCompression(settings.CompressionType),
        };

        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) =>
                _logger.LogError("Kafka producer error {Code}: {Reason}", error.Code, error.Reason))
            .Build();
    }

    public async Task PublishAsync(EventMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await ProduceAsync(message, cancellationToken);
    }

    public async Task PublishAsync(IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        // Awaiting each delivery in turn keeps per-key ordering intact and, with LingerMs, still
        // batches on the wire.
        foreach (var message in messages)
        {
            await ProduceAsync(message, cancellationToken);
        }
    }

    public void Dispose()
    {
        // Drain anything still buffered before the process exits.
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }

    private async Task ProduceAsync(EventMessage message, CancellationToken cancellationToken)
    {
        var kafkaMessage = new Message<string, byte[]>
        {
            Key = message.Key,
            Value = message.Value.ToArray(),
        };

        if (message.Headers is { Count: > 0 })
        {
            kafkaMessage.Headers = [];
            foreach (var (name, value) in message.Headers)
            {
                kafkaMessage.Headers.Add(name, Encoding.UTF8.GetBytes(value));
            }
        }

        var result = await _producer.ProduceAsync(message.Topic, kafkaMessage, cancellationToken);

        if (result.Status == PersistenceStatus.NotPersisted)
        {
            throw new ProduceException<string, byte[]>(
                new Error(ErrorCode.Local_Fail, $"Broker did not persist a message on {message.Topic}."),
                result);
        }
    }

    private static Acks ParseAcks(string value) => value.ToLowerInvariant() switch
    {
        "all" or "-1" => Confluent.Kafka.Acks.All,
        "1" => Confluent.Kafka.Acks.Leader,
        "0" => Confluent.Kafka.Acks.None,
        _ => throw new ArgumentException($"'{value}' is not a valid Kafka acks setting (all | 1 | 0)."),
    };

    private static CompressionType ParseCompression(string value) => value.ToLowerInvariant() switch
    {
        "none" => Confluent.Kafka.CompressionType.None,
        "gzip" => Confluent.Kafka.CompressionType.Gzip,
        "snappy" => Confluent.Kafka.CompressionType.Snappy,
        "lz4" => Confluent.Kafka.CompressionType.Lz4,
        "zstd" => Confluent.Kafka.CompressionType.Zstd,
        _ => throw new ArgumentException($"'{value}' is not a valid Kafka compression type."),
    };
}
