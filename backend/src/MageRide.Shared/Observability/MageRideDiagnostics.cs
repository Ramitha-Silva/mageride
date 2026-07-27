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
}
