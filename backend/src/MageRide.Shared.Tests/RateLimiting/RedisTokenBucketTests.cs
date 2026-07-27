using MageRide.Shared.RateLimiting;
using MageRide.Shared.Tests.Infrastructure;
using StackExchange.Redis;

namespace MageRide.Shared.Tests.RateLimiting;

/// <summary>
/// The distributed token bucket behind the OTP limits (D-32) and the proxy location-request
/// limits (P-12), against a real Redis.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisTokenBucketTests(RedisFixture redis) : IAsyncLifetime
{
    private IConnectionMultiplexer? _connection;

    public async ValueTask InitializeAsync()
    {
        if (redis.SkipReason is null)
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    private RedisTokenBucketRateLimiter Limiter() => new(_connection!);

    private static TokenBucketPolicy Policy(string name, int capacity, TimeSpan period, TimeSpan minInterval = default) =>
        new(name, capacity, capacity, period, minInterval);

    [Fact]
    public async Task A_bucket_allows_up_to_capacity_then_denies()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"cap-{Guid.NewGuid():N}", 5, TimeSpan.FromHours(1));
        var subject = "+94771234567";

        for (var i = 0; i < 5; i++)
        {
            var decision = await limiter.TryAcquireAsync(policy, subject);
            Assert.True(decision.Allowed, $"Attempt {i + 1} of 5 should have been allowed.");
            Assert.Equal(4 - i, decision.Remaining);
        }

        var denied = await limiter.TryAcquireAsync(policy, subject);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Subjects_have_independent_buckets()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"subj-{Guid.NewGuid():N}", 1, TimeSpan.FromHours(1));

        Assert.True((await limiter.TryAcquireAsync(policy, "+94771111111")).Allowed);
        Assert.False((await limiter.TryAcquireAsync(policy, "+94771111111")).Allowed);
        Assert.True((await limiter.TryAcquireAsync(policy, "+94772222222")).Allowed);
    }

    /// <summary>D-32's second half: a resend inside 60 s is refused even with tokens left.</summary>
    [Fact]
    public async Task The_minimum_interval_blocks_a_resend_even_with_tokens_left()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"otp-{Guid.NewGuid():N}", 5, TimeSpan.FromHours(1), TimeSpan.FromSeconds(60));
        var subject = "+94771234567";

        var first = await limiter.TryAcquireAsync(policy, subject);
        Assert.True(first.Allowed);
        Assert.Equal(4, first.Remaining);

        var tooSoon = await limiter.TryAcquireAsync(policy, subject);
        Assert.False(tooSoon.Allowed);

        // Four tokens are still there — the cooldown is what refused it, not exhaustion.
        Assert.Equal(4, tooSoon.Remaining);
        Assert.InRange(tooSoon.RetryAfter, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_short_cooldown_expires_and_the_next_send_is_allowed()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"cool-{Guid.NewGuid():N}", 5, TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(300));
        var subject = "+94771234567";

        Assert.True((await limiter.TryAcquireAsync(policy, subject)).Allowed);
        Assert.False((await limiter.TryAcquireAsync(policy, subject)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.True((await limiter.TryAcquireAsync(policy, subject)).Allowed);
    }

    [Fact]
    public async Task Tokens_refill_over_time()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();

        // 2 tokens per second, so an exhausted bucket recovers one in ~500 ms.
        var policy = new TokenBucketPolicy($"refill-{Guid.NewGuid():N}", 2, 2, TimeSpan.FromSeconds(1));
        var subject = "vehicle-1";

        Assert.True((await limiter.TryAcquireAsync(policy, subject)).Allowed);
        Assert.True((await limiter.TryAcquireAsync(policy, subject)).Allowed);
        Assert.False((await limiter.TryAcquireAsync(policy, subject)).Allowed);

        await Task.Delay(TimeSpan.FromMilliseconds(700));

        Assert.True((await limiter.TryAcquireAsync(policy, subject)).Allowed);
    }

    /// <summary>
    /// The reason this is a Lua script: a read-modify-write from the app would let concurrent
    /// requests both pass the 5-per-hour check.
    /// </summary>
    [Fact]
    public async Task Concurrent_acquisitions_never_exceed_capacity()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"race-{Guid.NewGuid():N}", 5, TimeSpan.FromHours(1));
        var subject = "+94771234567";

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => limiter.TryAcquireAsync(policy, subject)));

        Assert.Equal(5, decisions.Count(d => d.Allowed));
    }

    [Fact]
    public async Task Peek_reports_the_state_without_consuming()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"peek-{Guid.NewGuid():N}", 3, TimeSpan.FromHours(1));
        var subject = "+94771234567";

        await limiter.TryAcquireAsync(policy, subject);

        var before = await limiter.PeekAsync(policy, subject);
        var after = await limiter.PeekAsync(policy, subject);

        Assert.True(before.Allowed);
        Assert.Equal(2, before.Remaining);
        Assert.Equal(before.Remaining, after.Remaining);
    }

    [Fact]
    public async Task A_denied_call_consumes_nothing()
    {
        Assert.SkipWhen(redis.SkipReason is not null, redis.SkipReason ?? string.Empty);

        var limiter = Limiter();
        var policy = Policy($"nocost-{Guid.NewGuid():N}", 1, TimeSpan.FromHours(1));
        var subject = "s";

        await limiter.TryAcquireAsync(policy, subject);

        var first = await limiter.TryAcquireAsync(policy, subject);
        var second = await limiter.TryAcquireAsync(policy, subject);

        Assert.False(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(first.Remaining, second.Remaining);
    }

    [Fact]
    public void The_spec_policies_carry_the_numbers_D_32_and_P_12_state()
    {
        Assert.Equal(5, RateLimitPolicies.OtpSend.Capacity);
        Assert.Equal(TimeSpan.FromHours(1), RateLimitPolicies.OtpSend.RefillPeriod);
        Assert.Equal(TimeSpan.FromSeconds(60), RateLimitPolicies.OtpSend.MinInterval);

        Assert.Equal(5, RateLimitPolicies.LocationRequestHourly.Capacity);
        Assert.Equal(30, RateLimitPolicies.LocationRequestDaily.Capacity);
    }

    [Fact]
    public void A_nonsensical_policy_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketPolicy("x", 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketPolicy("x", 1, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new TokenBucketPolicy(" ", 1, 1, TimeSpan.FromSeconds(1)));
    }
}
