using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Caching;

/// <summary>Redis connection settings (D7' §4.1 <c>ConnectionStrings__Redis</c>).</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>StackExchange.Redis configuration string, e.g. <c>redis:6379</c>.</summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Keep starting when Redis is unreachable. Left on so a service can boot into a degraded
    /// mode and report unready (D7' §5.1) rather than crash-loop — D6' §8.3 defines degraded
    /// behaviour for exactly this case.
    /// </summary>
    public bool AbortOnConnectFail { get; set; }

    [Range(100, 60_000)]
    public int ConnectTimeoutMs { get; set; } = 5_000;

    [Range(100, 60_000)]
    public int SyncTimeoutMs { get; set; } = 5_000;

    /// <summary>Logical database index.</summary>
    [Range(0, 15)]
    public int Database { get; set; }

    /// <summary>Client name reported to Redis; shows up in <c>CLIENT LIST</c>.</summary>
    public string? ClientName { get; set; }
}
