using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Auth;

/// <summary>Access-token validation settings (D3' §0 "Auth", D-29, D-21).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// iam-svc JWKS endpoint (D7' §4.1 <c>Jwt__JwksUrl</c>). A raw JWKS document, not OIDC
    /// discovery.
    /// </summary>
    [Required]
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>Expected <c>iss</c>. Empty disables issuer validation — never in a deployed environment.</summary>
    public string? Issuer { get; set; }

    /// <summary>Expected <c>aud</c> values. Empty disables audience validation.</summary>
    public IList<string> Audiences { get; init; } = [];

    /// <summary>
    /// How long a fetched JWKS is served before a background refresh. D-21 caches for 15 minutes
    /// at the gateway, EMQX and fanout-svc to avoid a thundering herd on iam-svc.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan JwksCacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Floor between forced refetches. A token signed with an unknown <c>kid</c> triggers a
    /// refresh (that is how a 90-day signing-key rotation is picked up before the cache expires,
    /// D7' §13) — this stops a stream of bogus <c>kid</c>s turning into a DoS on iam-svc.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan JwksMinimumRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a JWKS fetch.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan JwksFetchTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tolerance for clock drift between iam-svc and this service. Small deliberately — access
    /// tokens live 30 minutes, so a generous skew materially extends a revoked token's life.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Require HTTPS for the JWKS URL. Off only for local compose, where iam-svc is plain HTTP
    /// inside the Docker network.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
