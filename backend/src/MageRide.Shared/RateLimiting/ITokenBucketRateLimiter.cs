namespace MageRide.Shared.RateLimiting;

/// <param name="Allowed">Whether the caller may proceed.</param>
/// <param name="Remaining">Whole tokens left after this call.</param>
/// <param name="RetryAfter">How long until the next token is available. Zero when allowed.</param>
public readonly record struct RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter)
{
    public static RateLimitDecision Allow(int remaining) => new(true, remaining, TimeSpan.Zero);

    public static RateLimitDecision Deny(int remaining, TimeSpan retryAfter) => new(false, remaining, retryAfter);
}

/// <summary>
/// Distributed token bucket over Redis (ADD §9.4 <c>rate:{…}</c>). Backs the OTP limits (D-32),
/// the proxy location-request limits (P-12) and the MQTT publish-rate limiter (D-17, E-08).
/// </summary>
public interface ITokenBucketRateLimiter
{
    /// <summary>
    /// Atomically takes <paramref name="tokens"/> from <paramref name="subject"/>'s bucket under
    /// <paramref name="policy"/>. Nothing is consumed when the call is denied.
    /// </summary>
    /// <param name="policy">The rule to apply.</param>
    /// <param name="subject">Who or what is limited — a user id, phone number, vehicle id or IP.</param>
    /// <param name="tokens">Tokens to take. Usually one.</param>
    Task<RateLimitDecision> TryAcquireAsync(
        TokenBucketPolicy policy, string subject, int tokens = 1, CancellationToken cancellationToken = default);

    /// <summary>Reads the bucket without consuming. For surfacing "resend available in N s" to a client.</summary>
    Task<RateLimitDecision> PeekAsync(
        TokenBucketPolicy policy, string subject, CancellationToken cancellationToken = default);
}
