using System.ComponentModel.DataAnnotations;

namespace MageRide.Ride.Configuration;

/// <summary>ride-svc's own settings (D7' §4.2 <c>ride-svc</c> row).</summary>
public sealed class RideOptions
{
    public const string SectionName = "Ride";

    /// <summary>Upper bound on an offer window, so a bad <c>ttlSeconds</c> cannot pin a ride open.</summary>
    public static readonly TimeSpan MaxOfferTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long an offer stays acceptable when dispatch-svc does not say (D5' §3.5: 15 s).
    /// <c>offer_expires_at</c> is the authoritative deadline; the Redis key is the fast path and
    /// the Quartz backstop (R-04, C037) is what fires when nobody answers.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan OfferTtl { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Shared secret guarding <c>/v1/internal/**</c>.
    /// <para>
    /// D3' §0 puts the internal family on service-to-service mTLS (Linkerd/SPIFFE) and the API
    /// gateway already refuses the prefix at the edge (C008). Neither is a mesh: until C042 wires
    /// one, an in-cluster caller is only as authenticated as this header. **Unset means the
    /// internal routes are not mapped at all** — a deployment that forgets it gets 404s, not an
    /// open door.
    /// </para>
    /// </summary>
    public string? InternalApiKey { get; set; }
}
