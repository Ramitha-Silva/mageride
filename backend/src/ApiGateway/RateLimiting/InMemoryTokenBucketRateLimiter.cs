using System.Collections.Concurrent;
using MageRide.Shared.Caching;
using MageRide.Shared.Observability;
using MageRide.Shared.RateLimiting;

namespace MageRide.ApiGateway.RateLimiting;

/// <summary>
/// Process-local <see cref="ITokenBucketRateLimiter"/> with the same semantics as the kernel's
/// Redis one, for a single-instance gateway: the dev compose stack, the replica, and the tests.
/// </summary>
/// <remarks>
/// Deliberately not a substitute for the Redis limiter in production. With N replicas behind
/// HAProxy this enforces N times the configured ceiling, which is why
/// <see cref="Configuration.GatewayStateStore.Redis"/> is the default.
/// </remarks>
internal sealed class InMemoryTokenBucketRateLimiter(TimeProvider timeProvider) : ITokenBucketRateLimiter
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public Task<RateLimitDecision> TryAcquireAsync(
        TokenBucketPolicy policy, string subject, int tokens = 1, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(policy, subject, tokens, peek: false));

    public Task<RateLimitDecision> PeekAsync(
        TokenBucketPolicy policy, string subject, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(policy, subject, 1, peek: true));

    private RateLimitDecision Evaluate(TokenBucketPolicy policy, string subject, int wanted, bool peek)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wanted);

        var bucket = _buckets.GetOrAdd(RedisKeys.RateLimit(policy.Name, subject), _ => new Bucket());
        var now = _timeProvider.GetUtcNow();

        lock (bucket.Gate)
        {
            if (bucket.UpdatedAt == default)
            {
                bucket.Tokens = policy.Capacity;
                bucket.UpdatedAt = now;
            }

            var elapsed = (now - bucket.UpdatedAt).TotalSeconds;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(policy.Capacity, bucket.Tokens + (elapsed * policy.RefillRatePerSecond));
                bucket.UpdatedAt = now;
            }

            var cooldown = TimeSpan.Zero;
            if (policy.MinInterval > TimeSpan.Zero && bucket.LastAcquiredAt is { } last)
            {
                var remaining = last + policy.MinInterval - now;
                cooldown = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            if (cooldown <= TimeSpan.Zero && bucket.Tokens >= wanted)
            {
                if (!peek)
                {
                    bucket.Tokens -= wanted;
                    bucket.LastAcquiredAt = now;
                }

                return RateLimitDecision.Allow((int)Math.Floor(bucket.Tokens));
            }

            var waitForTokens = bucket.Tokens < wanted
                ? TimeSpan.FromSeconds((wanted - bucket.Tokens) / policy.RefillRatePerSecond)
                : TimeSpan.Zero;

            if (!peek)
            {
                MageRideDiagnostics.RateLimitRejections.Add(
                    1, new KeyValuePair<string, object?>("policy", policy.Name));
            }

            return RateLimitDecision.Deny(
                (int)Math.Floor(bucket.Tokens), cooldown > waitForTokens ? cooldown : waitForTokens);
        }
    }

    private sealed class Bucket
    {
        public Lock Gate { get; } = new();

        public double Tokens { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? LastAcquiredAt { get; set; }
    }
}
