using System.Diagnostics;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.Shared.Observability;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.MqttBridge.Throttling;

/// <summary>Why a backlog sample was not forwarded.</summary>
internal enum ReplayShedReason
{
    /// <summary>The device's lane was already full.</summary>
    QueueFull,

    /// <summary>The wait for a token ran past <see cref="MqttBridgeOptions.ReplayMaxWait"/>.</summary>
    WaitTimeout,
}

/// <summary>
/// T-05's hard limit on the backlog stream: 20 samples per second per device on
/// <c>veh/{vehicleId}/pos/replay</c> (ADD §7.5.2, D6' §3.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>It waits rather than drops.</b> A backlog is a vehicle's history; discarding it because it
/// arrived quickly would lose exactly the data the flash ring buffered it for. Waiting is also what
/// makes the limit reach the broker: unacknowledged QoS 1 messages fill the replay session's
/// inflight window, EMQX stops dispatching, and the back-pressure ends up where ADD §7.5.2 puts it
/// ("the bridge applying a server-issued back-pressure token") instead of in this process's heap.
/// </para>
/// <para>
/// <b>The bucket is in Redis, not in this replica.</b> The replay share group hands each replica a
/// random slice of one device's backlog, so an in-process bucket would let N replicas pass
/// N × 20 samples/s and R-09's "hard rate limit" would be a limit on nothing.
/// </para>
/// <para>
/// <b>It fails open.</b> If Redis is unreachable the sample is forwarded and the failure is logged
/// once per lane rather than per sample. The alternative is losing live-adjacent telemetry to a
/// cache outage, and the broker's own <c>messages_rate</c> ceiling is still in force underneath.
/// </para>
/// </remarks>
internal sealed class ReplayThrottle(
    ITokenBucketRateLimiter limiter, IOptions<MqttBridgeOptions> options, ILogger<ReplayThrottle> logger)
{
    /// <summary>Longest single sleep between attempts, so a bucket refill is never overslept.</summary>
    private static readonly TimeSpan MaxPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Shortest, so a busy lane cannot spin on Redis.</summary>
    private static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(10);

    private readonly MqttBridgeOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private long _throttled;

    /// <summary>Samples that had to wait for a token. Non-zero means T-05 actually bit.</summary>
    public long Throttled => Interlocked.Read(ref _throttled);

    /// <summary>
    /// Waits until <paramref name="vehicleId"/> may send another backlog sample.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a token was taken; <see langword="false"/> when the wait ran past
    /// <see cref="MqttBridgeOptions.ReplayMaxWait"/> and the caller should shed the sample.
    /// </returns>
    public async Task<bool> WaitAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var policy = new TokenBucketPolicy(
            RateLimitPolicies.MqttReplay.Name,
            capacity: _options.ReplaySamplesPerSecond,
            refillTokens: _options.ReplaySamplesPerSecond,
            refillPeriod: TimeSpan.FromSeconds(1));

        var subject = vehicleId.ToString();
        var started = Stopwatch.GetTimestamp();
        var waited = false;

        while (true)
        {
            RateLimitDecision decision;

            try
            {
                decision = await limiter.TryAcquireAsync(policy, subject, 1, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "The T-05 replay bucket for {VehicleId} is unreachable; forwarding unthrottled", vehicleId);
                return true;
            }

            if (decision.Allowed)
            {
                if (waited)
                {
                    MageRideDiagnostics.MqttReplayWaitMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                return true;
            }

            if (Stopwatch.GetElapsedTime(started) + decision.RetryAfter > _options.ReplayMaxWait)
            {
                return false;
            }

            if (!waited)
            {
                waited = true;
                Interlocked.Increment(ref _throttled);
                MageRideDiagnostics.MqttReplayThrottled.Add(1);
            }

            var delay = decision.RetryAfter;
            if (delay < MinPoll)
            {
                delay = MinPoll;
            }
            else if (delay > MaxPoll)
            {
                delay = MaxPoll;
            }

            await Task.Delay(delay, cancellationToken);
        }
    }
}
