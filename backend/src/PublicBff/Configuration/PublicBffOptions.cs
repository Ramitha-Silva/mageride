using System.ComponentModel.DataAnnotations;

namespace MageRide.PublicBff.Configuration;

/// <summary>
/// Every knob public-bff has, documented where it is declared.
/// </summary>
/// <remarks>
/// <b>There is no <c>Jwt</c> section and there must not be.</b> The share token is the whole
/// credential on this surface (AL-44), so an authentication scheme here would be a second way in
/// that nothing on the six SCR-WT pages could ever use.
/// </remarks>
public sealed class PublicBffOptions
{
    public const string SectionName = "PublicBff";

    /// <summary>
    /// Reads of one token per minute (D-34's number, applied to the whole <c>/public/track</c>
    /// family by D3' Δ 2026-07-05).
    /// </summary>
    /// <remarks>
    /// Held at the same value safety-svc holds it at for the same reason: the two surfaces are the
    /// same credential seen from two contracts, and a page that polled harder than the share view
    /// would make the number depend on which endpoint somebody happened to call.
    /// </remarks>
    [Range(1, 100_000)]
    public int PerTokenPerMinute { get; set; } = 60;

    /// <summary>
    /// Reads from one address per minute. <b>No spec gives a number</b>; D3' asks for per-token
    /// <em>and</em> per-IP, and this is ten tokens' worth — the same ratio safety-svc chose.
    /// </summary>
    /// <remarks>
    /// A per-token limit alone is no limit at all against somebody who has harvested a hundred
    /// links out of a leaked SMS gateway log, which is the attack this second bucket is for.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int PerIpPerMinute { get; set; } = 600;

    /// <summary>
    /// A position older than this is omitted rather than drawn. <b>No spec pins it</b> — US-7.17's
    /// staleness rule applied to the surface where a frozen marker misleads most.
    /// </summary>
    /// <remarks>
    /// The person watching an SCR-WT page is not in the vehicle and has no other way to tell that
    /// the marker stopped moving twenty minutes ago.
    /// </remarks>
    public TimeSpan PositionMaxAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far from the sender the vehicle must be before a parcel counts as <c>InTransit</c>
    /// rather than <c>PickedUp</c>. <b>No spec pins it.</b>
    /// </summary>
    /// <remarks>
    /// SCR-WT-002 draws four steps and the ride machine has three states for them (ADD Appendix B.2
    /// invariant 6: a package adds none), so the fourth is derived from the one other fact that is
    /// observable — whether the vehicle has left. 150 m is past the far side of a junction and short
    /// of the next one; it exists to be a *departure*, not a distance anybody plans around.
    /// </remarks>
    [Range(1, 100_000)]
    public double PickupDepartureRadiusM { get; set; } = 150;

    /// <summary>
    /// Straight-line distance is multiplied by this to approximate a road path (US-7.11).
    /// </summary>
    /// <remarks>
    /// The same figure and the same caveat as query-svc's estimator: ADD §7.6 puts routing
    /// (OSRM/Valhalla) in Phase 3, so nothing on the platform can measure a real path yet. A setting
    /// rather than a constant so it can be retuned against observed arrivals — and deleted when the
    /// router lands.
    /// </remarks>
    [Range(1.0, 3.0)]
    public double EtaDetourFactor { get; set; } = 1.35;

    /// <summary>
    /// The speed assumed for a vehicle that is reporting a standstill. <b>No spec pins it.</b>
    /// </summary>
    /// <remarks>
    /// An average <em>including</em> stops, which is why it is a fraction of ADD §12.6's anti-spoof
    /// ceilings: those are the speeds above which a fix is a lie, this is the speed a vehicle
    /// actually crosses a city at. Deliberately one number rather than query-svc's per-type table —
    /// a second copy of that table here would be the thing nobody notices drifting.
    /// </remarks>
    [Range(1, 200)]
    public double EtaAssumedSpeedKph { get; set; } = 22;

    /// <summary>Nothing above this is reported at all (US-7.11, as query-svc bounds it).</summary>
    public TimeSpan MaxEta { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>
    /// How long an SSE connection is held before the client is asked to reconnect.
    /// </summary>
    /// <remarks>
    /// <b>Bounded on purpose.</b> A no-login page has no session to expire, so the stream is the
    /// only thing that re-reads the token: a link revoked while somebody is watching stops being
    /// live within this window rather than when the browser tab is closed. The client reconnects
    /// with <c>?since</c> and loses nothing (D6' I-29.1's poll fallback is the same path).
    /// </remarks>
    public TimeSpan StreamMaxDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the stream re-reads the position and the state. <b>No spec pins it.</b>
    /// </summary>
    /// <remarks>
    /// D6' §5.1's cadence for a passenger watching a ride is 1 s near pickup and 3 s otherwise; two
    /// seconds is inside that band and is what a page with one marker on it can use. This is a
    /// *read* interval, not a send interval — an unchanged position emits no frame.
    /// </remarks>
    public TimeSpan StreamPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the stream may go without sending anything before a comment frame is written.
    /// </summary>
    /// <remarks>
    /// A stationary vehicle produces no events, and an idle TCP connection through a proxy is
    /// indistinguishable from a dead one. The comment costs two bytes and keeps every intermediary
    /// from reaping the connection under the page.
    /// </remarks>
    public TimeSpan StreamHeartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a presigned proof-photo URL on a receipt lives. <b>No spec pins it.</b>
    /// </summary>
    /// <remarks>
    /// Minted fresh on every receipt read, so it never needs to outlive the page that asked for it.
    /// P-10's photograph is of somebody's doorstep and the link is unauthenticated once issued.
    /// </remarks>
    public TimeSpan ProofPhotoUrlTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>ride-svc's <c>/v1/internal/location-requests/**</c> seam (AL-45).</summary>
    /// <remarks>
    /// <b>Unset ⇒ the two pickup routes answer 503 and stay mapped.</b> A route that disappeared
    /// when a setting was absent is a route the fence tests do not enumerate, and SCR-WT-003's
    /// Decline would look like a token that never existed.
    /// </remarks>
    public UpstreamOptions Ride { get; set; } = new();

    /// <summary>safety-svc's <c>/v1/internal/safety/sos/web</c> seam (US-25.5, D-33).</summary>
    /// <remarks>
    /// <b>Unset ⇒ the SOS route answers 503</b>, which is the honest answer: this service cannot
    /// record a <c>safety.sos_events</c> row itself and must not pretend an alert was raised.
    /// </remarks>
    public UpstreamOptions Safety { get; set; } = new();

    /// <summary>
    /// Budget for one internal hop. <b>No spec pins it</b>; D6' §8.3's default is 2 s and the SOS
    /// leg is inside D-33's five seconds, which is what sets the ceiling.
    /// </summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How wide a double tap on SOS is. <b>No spec pins it.</b>
    /// </summary>
    /// <remarks>
    /// The window a derived <c>Idempotency-Key</c> covers when the page sends none, so two presses
    /// inside it are one alert at safety-svc. Long enough to cover "nothing appeared to happen, press
    /// it again" and short enough that a second, genuine emergency is a second alert.
    /// </remarks>
    public TimeSpan SosDedupeWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Trust <c>X-Forwarded-For</c> for the per-IP bucket.
    /// </summary>
    /// <remarks>
    /// On, because every request arrives through the C008 gateway — the same flag iam-svc and
    /// safety-svc carry. Off makes the per-IP bucket count the gateway rather than the visitor,
    /// which would rate-limit the whole internet as one client.
    /// </remarks>
    public bool TrustForwardedFor { get; set; } = true;
}

/// <summary>One internal upstream: where it is and the shared key that opens its plane.</summary>
public sealed class UpstreamOptions
{
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Matches the callee's own <c>InternalApiKey</c>. D3' §0 puts <c>/v1/internal/**</c> on mTLS;
    /// the shared secret is the interim until the mesh lands (C042).
    /// </summary>
    public string? InternalApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
