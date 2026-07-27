using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Resilience;

/// <summary>
/// Retry, circuit-breaker and timeout budgets from D6' §8.3.
/// </summary>
/// <remarks>
/// The defaults are the numbers the spec states. Polly v8's breaker is ratio-based over a sampling
/// window rather than a raw consecutive-failure count, so "open after 5 failures / 30 s" maps to a
/// 30 s window, a minimum throughput of 5 and a failure ratio of 0.5 — see
/// <see cref="BreakerFailureRatio"/>.
/// </remarks>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Attempts after the first (D6' §8.3: "3 attempts").</summary>
    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>First backoff delay (D6' §8.3: exponential 100 ms → 2 s).</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:30")]
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Backoff ceiling.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Jitter applied to each delay (D6' §8.3: ±25%).</summary>
    [Range(0, 1)]
    public double JitterFactor { get; set; } = 0.25;

    /// <summary>Rolling window the breaker measures over (D6' §8.3: 30 s).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Calls needed in the window before the breaker may open (D6' §8.3: 5 failures).</summary>
    [Range(2, 1000)]
    public int BreakerMinimumThroughput { get; set; } = 5;

    /// <summary>Failure share that opens the breaker once minimum throughput is met.</summary>
    [Range(0.01, 1.0)]
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>How long the breaker stays open before a half-open probe (D6' §8.3: 15 s).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Per-attempt timeout. D6' §8.3 budgets 15 s for an API call.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>The per-dependency timeouts D6' §8.3 names.</summary>
public static class MageRideTimeouts
{
    /// <summary>Inter-service and client-facing HTTP.</summary>
    public static readonly TimeSpan Api = TimeSpan.FromSeconds(15);

    /// <summary>OnePay / LankaQR (D-10, D-12).</summary>
    public static readonly TimeSpan PaymentProvider = TimeSpan.FromSeconds(90);

    /// <summary>Gemini Flash 3.0 / Tesseract (D6' §7.5).</summary>
    public static readonly TimeSpan Ocr = TimeSpan.FromSeconds(30);

    /// <summary>Kafka consumer poll.</summary>
    public static readonly TimeSpan KafkaPoll = TimeSpan.FromMilliseconds(500);
}
