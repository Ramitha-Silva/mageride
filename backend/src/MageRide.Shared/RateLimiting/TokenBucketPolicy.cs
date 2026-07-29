namespace MageRide.Shared.RateLimiting;

/// <summary>
/// A token-bucket rule: <paramref name="Capacity"/> tokens, refilled at
/// <paramref name="RefillTokens"/> per <paramref name="RefillPeriod"/>, optionally with a floor
/// between consecutive acquisitions.
/// </summary>
public sealed record TokenBucketPolicy
{
    /// <param name="name">Short identifier used in the Redis key and in metrics.</param>
    /// <param name="capacity">Burst size — the most tokens the bucket can hold.</param>
    /// <param name="refillTokens">Tokens added each <paramref name="refillPeriod"/>.</param>
    /// <param name="refillPeriod">How often the refill happens.</param>
    /// <param name="minInterval">
    /// Minimum gap between two successful acquisitions. Models a resend cooldown, which a plain
    /// bucket cannot express: D-32 needs both "5 per hour" <em>and</em> "not within 60 seconds".
    /// </param>
    public TokenBucketPolicy(
        string name, int capacity, double refillTokens, TimeSpan refillPeriod, TimeSpan minInterval = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refillTokens);

        if (refillPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refillPeriod), refillPeriod, "Refill period must be positive.");
        }

        if (minInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minInterval), minInterval, "Minimum interval cannot be negative.");
        }

        Name = name;
        Capacity = capacity;
        RefillTokens = refillTokens;
        RefillPeriod = refillPeriod;
        MinInterval = minInterval;
    }

    /// <inheritdoc cref="TokenBucketPolicy(string, int, double, TimeSpan, TimeSpan)"/>
    public string Name { get; }

    /// <inheritdoc cref="TokenBucketPolicy(string, int, double, TimeSpan, TimeSpan)"/>
    public int Capacity { get; }

    /// <inheritdoc cref="TokenBucketPolicy(string, int, double, TimeSpan, TimeSpan)"/>
    public double RefillTokens { get; }

    /// <inheritdoc cref="TokenBucketPolicy(string, int, double, TimeSpan, TimeSpan)"/>
    public TimeSpan RefillPeriod { get; }

    /// <inheritdoc cref="TokenBucketPolicy(string, int, double, TimeSpan, TimeSpan)"/>
    public TimeSpan MinInterval { get; }

    /// <summary>Tokens added per second.</summary>
    public double RefillRatePerSecond => RefillTokens / RefillPeriod.TotalSeconds;

    /// <summary>How long an idle bucket keeps its state before Redis may evict it.</summary>
    public TimeSpan StateTtl
    {
        get
        {
            var toRefill = TimeSpan.FromSeconds(Capacity / RefillRatePerSecond);
            var floor = MinInterval > TimeSpan.Zero ? MinInterval : TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(60, Math.Max(toRefill.TotalSeconds, floor.TotalSeconds) * 2));
        }
    }
}

/// <summary>The rules the specs name by number.</summary>
public static class RateLimitPolicies
{
    /// <summary>
    /// OTP send: 5 per hour with a 60-second resend cooldown (D-32; D7' §4.2
    /// <c>Otp__ResendCooldownSec=60</c>, <c>Otp__MaxPerHour=5</c>).
    /// </summary>
    public static readonly TokenBucketPolicy OtpSend =
        new("otp-send", capacity: 5, refillTokens: 5, refillPeriod: TimeSpan.FromHours(1), minInterval: TimeSpan.FromSeconds(60));

    /// <summary>Proxy location requests: 5 per hour (P-12). The daily cap of 30 is enforced separately.</summary>
    public static readonly TokenBucketPolicy LocationRequestHourly =
        new("loc-request-hour", capacity: 5, refillTokens: 5, refillPeriod: TimeSpan.FromHours(1));

    /// <summary>Proxy location requests: 30 per day (P-12).</summary>
    public static readonly TokenBucketPolicy LocationRequestDaily =
        new("loc-request-day", capacity: 30, refillTokens: 30, refillPeriod: TimeSpan.FromDays(1));

    /// <summary>
    /// Backlog replay: 20 samples per second per device on <c>veh/{vehicleId}/pos/replay</c>
    /// (T-05, ADD §7.5.2, D6' §3.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held by mqtt-bridge-svc, and held in <b>Redis</b> rather than per replica: the replay share
    /// group spreads one vehicle's backlog across every replica, so an in-process bucket would let
    /// N replicas pass N × 20 samples/s and the "hard rate limit" R-09 asks for would be a limit on
    /// nothing.
    /// </para>
    /// <para>
    /// Capacity equals the per-second rate, so the bucket carries no burst credit for an idle
    /// vehicle. A tracker that has been offline for an hour is exactly the case this exists for —
    /// letting it spend an hour's worth of accumulated tokens in one go is the reconnect storm.
    /// </para>
    /// </remarks>
    public static readonly TokenBucketPolicy MqttReplay =
        new("mqtt-replay", capacity: 20, refillTokens: 20, refillPeriod: TimeSpan.FromSeconds(1));
}
