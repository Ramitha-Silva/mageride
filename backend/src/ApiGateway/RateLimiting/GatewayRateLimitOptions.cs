using System.ComponentModel.DataAnnotations;
using MageRide.Shared.RateLimiting;

namespace MageRide.ApiGateway.RateLimiting;

/// <summary>
/// Edge rate limiting (D6' §8.2 "Gateway applies attestation + version gate + rate-limit before
/// forward"). Bound from <c>Gateway:RateLimits</c>.
/// </summary>
/// <remarks>
/// These are coarse edge ceilings, not the business limits. The named per-endpoint limits — OTP
/// 5/h with a 60 s resend cooldown (D-32), proxy location requests 5/h and 30/d (P-12) — stay in
/// the services that own them, because they key on a phone number or a booker id that the gateway
/// cannot see. The edge only stops a client hammering a route.
/// </remarks>
public sealed class GatewayRateLimitOptions
{
    public const string SectionName = "Gateway:RateLimits";

    /// <summary>Policy applied to a route whose metadata names none.</summary>
    public const string DefaultPolicyName = "default";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Forward the request when the limiter itself fails (Redis down, script error).
    /// <para>
    /// On by default. A limiter that fails closed converts a Redis blip into a total platform
    /// outage, which is a strictly worse failure than a window with no edge ceiling — the services
    /// behind it still enforce their own limits.
    /// </para>
    /// </summary>
    public bool FailOpen { get; set; } = true;

    /// <summary>Named policies, referenced from a route's <c>RateLimit</c> metadata.</summary>
    public IDictionary<string, RateLimitPolicyOptions> Policies { get; init; } =
        new Dictionary<string, RateLimitPolicyOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the kernel policy for a name, or null when the name is unknown.</summary>
    public TokenBucketPolicy? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Policies.TryGetValue(name, out var policy) ? policy.ToTokenBucketPolicy(name) : null;
    }
}

/// <summary>One named edge ceiling.</summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>Burst size.</summary>
    [Range(1, 1_000_000)]
    public int Capacity { get; set; } = 300;

    /// <summary>Tokens restored per <see cref="RefillPeriod"/>. Defaults to a full bucket.</summary>
    [Range(0.0001, 1_000_000)]
    public double RefillTokens { get; set; } = 300;

    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan RefillPeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Minimum gap between two accepted requests. Zero for a plain bucket.</summary>
    public TimeSpan MinInterval { get; set; }

    internal TokenBucketPolicy ToTokenBucketPolicy(string name) =>
        new("gw-" + name, Capacity, RefillTokens, RefillPeriod, MinInterval);
}
