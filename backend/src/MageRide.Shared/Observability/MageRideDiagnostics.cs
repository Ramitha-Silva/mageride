using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MageRide.Shared.Observability;

/// <summary>
/// The platform's own <see cref="ActivitySource"/> and <see cref="Meter"/>. Everything emitted
/// here is scraped as Prometheus metrics and exported over OTLP to Tempo/Loki (D7' §12).
/// </summary>
public static class MageRideDiagnostics
{
    public const string ActivitySourceName = "MageRide";
    public const string MeterName = "MageRide";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Commit-to-publish latency of the transactional outbox, in milliseconds. E-09 budgets a
    /// median under 50 ms; this is the signal an SLO alert watches.
    /// </summary>
    public static readonly Histogram<double> OutboxDispatchLatencyMs =
        Meter.CreateHistogram<double>("mageride.outbox.dispatch.latency", "ms",
            "Time from an outbox row being written to it being acknowledged by the broker.");

    /// <summary>Outbox rows successfully published.</summary>
    public static readonly Counter<long> OutboxDispatched =
        Meter.CreateCounter<long>("mageride.outbox.dispatched", "{event}", "Outbox rows published to the event backbone.");

    /// <summary>Failed publish attempts. Rows stay undispatched and are retried.</summary>
    public static readonly Counter<long> OutboxPublishFailures =
        Meter.CreateCounter<long>("mageride.outbox.publish_failures", "{failure}", "Outbox publish attempts that failed.");

    /// <summary>Requests answered from the command log instead of being executed again (R-14).</summary>
    public static readonly Counter<long> IdempotentReplays =
        Meter.CreateCounter<long>("mageride.idempotency.replays", "{request}", "Requests served as an idempotent replay.");

    /// <summary>Token-bucket rejections, tagged by policy (D-32, P-12).</summary>
    public static readonly Counter<long> RateLimitRejections =
        Meter.CreateCounter<long>("mageride.ratelimit.rejections", "{request}", "Requests rejected by a token-bucket rate limiter.");

    // --- Hot path (C024): EMQX -> Redpanda -> Redis -> SignalR ------------------------------------
    // ADD §13.2's golden signals for the position plane. The end-to-end histogram is the one that
    // answers this component's SLO ("a position reaches the passenger's group in under 5 s, p95");
    // the counters either side of it are what says *where* the time went when it does not.

    /// <summary>Device payloads lifted off EMQX onto <c>telemetry.raw</c>, tagged by stream.</summary>
    public static readonly Counter<long> MqttBridgeForwarded =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.forwarded", "{message}", "MQTT payloads forwarded to telemetry.raw.");

    /// <summary>Payloads the bridge could not forward. Not acknowledged, so EMQX redelivers.</summary>
    public static readonly Counter<long> MqttBridgeFailures =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.failures", "{message}", "MQTT payloads the bridge failed to forward.");

    /// <summary>Samples normalised onto <c>telemetry.normalized</c> and the live indexes.</summary>
    public static readonly Counter<long> PositionsProcessed =
        Meter.CreateCounter<long>("mageride.positions.processed", "{sample}", "Position samples normalised and indexed.");

    /// <summary>Samples dropped before indexing, tagged by reason (undecodable, malformed, replayed).</summary>
    public static readonly Counter<long> PositionsDropped =
        Meter.CreateCounter<long>("mageride.positions.dropped", "{sample}", "Position samples dropped before indexing.");

    /// <summary>
    /// GNSS capture instant to Redis cell stream, in milliseconds — the ingest half of the SLO.
    /// </summary>
    /// <remarks>
    /// Measured from the device's own clock, so a handset with a wrong clock skews it. That is
    /// deliberate: it is the number a passenger experiences, and clock skew is one of the ways the
    /// experience goes wrong.
    /// </remarks>
    public static readonly Histogram<double> PositionIngestLatencyMs =
        Meter.CreateHistogram<double>("mageride.positions.ingest.latency", "ms",
            "Time from a sample's GNSS instant to it landing on its cell stream.");

    /// <summary>Vehicle frames pushed to SignalR geocell groups.</summary>
    public static readonly Counter<long> FanoutFramesSent =
        Meter.CreateCounter<long>("mageride.fanout.frames", "{frame}", "Vehicle frames pushed to geocell groups.");

    /// <summary>
    /// Cell stream entry to SignalR send, in milliseconds — the fan-out half of the SLO.
    /// </summary>
    public static readonly Histogram<double> FanoutLatencyMs =
        Meter.CreateHistogram<double>("mageride.fanout.latency", "ms",
            "Time from a cell stream entry being written to it being pushed to a group.");
}
