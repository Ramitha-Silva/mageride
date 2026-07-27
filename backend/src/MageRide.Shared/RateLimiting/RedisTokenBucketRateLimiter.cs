using System.Globalization;
using MageRide.Shared.Caching;
using MageRide.Shared.Observability;
using StackExchange.Redis;

namespace MageRide.Shared.RateLimiting;

/// <summary>
/// <see cref="ITokenBucketRateLimiter"/> backed by a single Lua script, so refill, cooldown check
/// and consumption happen atomically on the Redis server — a read-modify-write from the app would
/// let two concurrent OTP requests both pass the 5-per-hour check (D-32).
/// </summary>
public sealed class RedisTokenBucketRateLimiter(IConnectionMultiplexer redis) : ITokenBucketRateLimiter
{
    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));

    /// <summary>
    /// KEYS[1] bucket · ARGV: capacity, refill/s, tokens wanted, min interval s, ttl s, peek.
    /// Returns [allowed, whole tokens left, retry-after ms].
    /// </summary>
    private const string Script =
        """
        local capacity    = tonumber(ARGV[1])
        local rate        = tonumber(ARGV[2])
        local wanted      = tonumber(ARGV[3])
        local minInterval = tonumber(ARGV[4])
        local ttl         = tonumber(ARGV[5])
        local peek        = tonumber(ARGV[6])

        local clock = redis.call('TIME')
        local now = tonumber(clock[1]) + (tonumber(clock[2]) / 1000000)

        local state = redis.call('HMGET', KEYS[1], 'tokens', 'ts', 'last')
        local tokens = tonumber(state[1])
        local ts     = tonumber(state[2])
        local last   = tonumber(state[3])

        if tokens == nil or ts == nil then
          tokens = capacity
          ts = now
        end
        if last == nil then last = -1 end

        local elapsed = now - ts
        if elapsed > 0 then
          tokens = math.min(capacity, tokens + (elapsed * rate))
          ts = now
        end

        local cooldown = 0
        if minInterval > 0 and last >= 0 then
          cooldown = (last + minInterval) - now
          if cooldown < 0 then cooldown = 0 end
        end

        local allowed = 0
        if cooldown <= 0 and tokens >= wanted then
          allowed = 1
          if peek == 0 then
            tokens = tokens - wanted
            last = now
          end
        end

        local retry = 0
        if allowed == 0 then
          local waitForTokens = 0
          if tokens < wanted then waitForTokens = (wanted - tokens) / rate end
          retry = math.max(cooldown, waitForTokens)
        end

        if peek == 0 then
          redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts, 'last', last)
          redis.call('EXPIRE', KEYS[1], ttl)
        end

        return { allowed, math.floor(tokens), math.floor(retry * 1000) }
        """;

    public Task<RateLimitDecision> TryAcquireAsync(
        TokenBucketPolicy policy, string subject, int tokens = 1, CancellationToken cancellationToken = default) =>
        EvaluateAsync(policy, subject, tokens, peek: false, cancellationToken);

    public Task<RateLimitDecision> PeekAsync(
        TokenBucketPolicy policy, string subject, CancellationToken cancellationToken = default) =>
        EvaluateAsync(policy, subject, 1, peek: true, cancellationToken);

    private async Task<RateLimitDecision> EvaluateAsync(
        TokenBucketPolicy policy, string subject, int tokens, bool peek, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokens);

        cancellationToken.ThrowIfCancellationRequested();

        var database = _redis.GetDatabase();
        var key = (RedisKey)RedisKeys.RateLimit(policy.Name, subject);

        var result = await database.ScriptEvaluateAsync(
            Script,
            [key],
            [
                Number(policy.Capacity),
                Number(policy.RefillRatePerSecond),
                Number(tokens),
                Number(policy.MinInterval.TotalSeconds),
                Number((long)policy.StateTtl.TotalSeconds),
                peek ? 1 : 0,
            ]);

        var values = (RedisValue[])result!;
        var allowed = (long)values[0] == 1;
        var remaining = (int)(long)values[1];
        var retryAfter = TimeSpan.FromMilliseconds((long)values[2]);

        if (!allowed && !peek)
        {
            MageRideDiagnostics.RateLimitRejections.Add(1, new KeyValuePair<string, object?>("policy", policy.Name));
        }

        return allowed ? RateLimitDecision.Allow(remaining) : RateLimitDecision.Deny(remaining, retryAfter);
    }

    private static RedisValue Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static RedisValue Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
