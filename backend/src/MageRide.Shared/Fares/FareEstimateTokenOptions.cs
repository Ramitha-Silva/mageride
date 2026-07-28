using System.ComponentModel.DataAnnotations;

namespace MageRide.Shared.Fares;

/// <summary>
/// The key and lifetime behind <c>fareEstimateToken</c> (D3' fare-svc <c>GET /v1/fare/estimate</c>,
/// ride-svc <c>POST /v1/rides/request</c>).
/// </summary>
/// <remarks>
/// Two services bind this section: fare-svc issues tokens with the key, ride-svc verifies them.
/// D7' §4.2 has no row for either — <c>Fare__EstimateTokenKey</c> is a micro-change-set candidate
/// recorded in the C022 handoff and carried in <c>infra/env/.env.app.example</c>.
/// </remarks>
public sealed class FareEstimateTokenOptions
{
    public const string SectionName = "Fare";

    /// <summary>
    /// HMAC-SHA256 key, shared by the issuer and the verifier. Required: a default would make
    /// every quote forgeable, and <c>400 invalid-fare-token</c> exists precisely to stop a client
    /// naming its own price.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string EstimateTokenKey { get; set; } = string.Empty;

    /// <summary>
    /// How long a quote stays bindable. No spec fixes it; 15 minutes is long enough for a
    /// passenger to compare tiers and short enough that a peak window cannot open underneath the
    /// price (D5' §1.1). Recorded in the C022 handoff.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "02:00:00")]
    public TimeSpan EstimateTokenTtl { get; set; } = TimeSpan.FromMinutes(15);
}
