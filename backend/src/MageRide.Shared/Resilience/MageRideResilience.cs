using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace MageRide.Shared.Resilience;

/// <summary>
/// The retry / breaker / timeout pipeline D6' §8.3 specifies, for outbound HTTP.
/// </summary>
public static class MageRideResilience
{
    /// <summary>
    /// Adds retry with jittered exponential backoff, a circuit breaker and a per-attempt timeout.
    /// </summary>
    /// <remarks>
    /// Retry is safe here only because every mutation carries an <c>Idempotency-Key</c> (D3' §0,
    /// R-14) — the callee replays rather than re-executes. A handler for a non-idempotent API
    /// must not use this pipeline.
    /// </remarks>
    public static IHttpClientBuilder AddMageRideResilience(
        this IHttpClientBuilder builder, ResilienceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = options ?? new ResilienceOptions();

        builder.AddResilienceHandler($"mageride:{builder.Name}", pipeline =>
        {
            // Timeout first (outermost of the three) so it bounds the whole retry sequence's
            // per-attempt work rather than the sum.
            pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = settings.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = settings.BaseDelay,
                MaxDelay = settings.MaxDelay,
                UseJitter = false,
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    Jitter(settings, args.AttemptNumber)),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(static response => IsTransient(response.StatusCode)),
            });

            pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                SamplingDuration = settings.BreakerSamplingDuration,
                MinimumThroughput = settings.BreakerMinimumThroughput,
                FailureRatio = settings.BreakerFailureRatio,
                BreakDuration = settings.BreakerBreakDuration,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(static response => IsTransient(response.StatusCode)),
            });

            pipeline.AddTimeout(settings.AttemptTimeout);
        });

        return builder;
    }

    /// <summary>
    /// Exponential backoff with ±<see cref="ResilienceOptions.JitterFactor"/> spread
    /// (D6' §8.3: "exponential 100 ms→2 s, ±25% jitter").
    /// </summary>
    /// <remarks>
    /// Polly's built-in <c>UseJitter</c> applies a decorrelated-jitter curve, which is a different
    /// distribution from the symmetric band the spec asks for, so the delay is generated here.
    /// </remarks>
    internal static TimeSpan Jitter(ResilienceOptions options, int attemptNumber)
    {
        var exponential = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attemptNumber);
        var capped = Math.Min(exponential, options.MaxDelay.TotalMilliseconds);

        var spread = capped * options.JitterFactor;
        var jittered = capped + Random.Shared.NextDouble() * 2 * spread - spread;

        return TimeSpan.FromMilliseconds(Math.Max(0, jittered));
    }

    /// <summary>
    /// Statuses worth retrying. 429 is included because the callee told us to back off; 4xx
    /// otherwise is a client error that will fail identically on retry.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
