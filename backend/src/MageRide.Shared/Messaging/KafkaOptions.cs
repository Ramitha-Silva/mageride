using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Messaging;

/// <summary>
/// Redpanda producer settings (D6' §2, D7' §4.1 <c>Kafka__BootstrapServers</c>).
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    [Required]
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Producer client id. Defaults to the service name when left unset.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Replication acknowledgement. <c>all</c> against the MVP/prod 3-node RF=3 cluster (D6' §2)
    /// — an outbox row is marked dispatched once the broker acknowledges, so a weaker setting
    /// would let an event be lost after the row says it was sent.
    /// </summary>
    public string Acks { get; set; } = "all";

    /// <summary>Broker-side idempotent producer: no duplicates on internal retry.</summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>Linger before sending a batch. Kept small; E-09 budgets under 50 ms end to end.</summary>
    [Range(0, 1000)]
    public int LingerMs { get; set; } = 5;

    /// <summary>Producer-side timeout for a single message's acknowledgement.</summary>
    [Range(100, 300_000)]
    public int MessageTimeoutMs { get; set; } = 15_000;

    /// <summary>Socket/metadata timeout used by the health check (D7' §5.1 readiness pings Kafka).</summary>
    [Range(100, 60_000)]
    public int MetadataTimeoutMs { get; set; } = 3_000;

    /// <summary>Compression codec. Redpanda handles all of these; <c>lz4</c> is cheapest on CPU.</summary>
    public string CompressionType { get; set; } = "lz4";
}
