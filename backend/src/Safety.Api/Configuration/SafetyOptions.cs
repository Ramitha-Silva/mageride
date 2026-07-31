using System.ComponentModel.DataAnnotations;

namespace MageRide.Safety.Configuration;

/// <summary>
/// safety-svc's knobs. Every default is argued at its declaration; the ones with no spec behind
/// them say so.
/// </summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>The interim shared secret <c>/v1/internal/safety/**</c> demands, until mTLS (C042).</summary>
    /// <remarks>
    /// <b>Unset leaves the internal family unmapped</b>, the posture ride-svc, registry-svc and
    /// notification-svc take. What is behind it: the moderation decision that delists a vehicle
    /// (US-12.6) and the trip-end revocation that closes every share link. Both are writes, and an
    /// open confirm route is a way to delist any vehicle on the platform in three calls.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // D-33 — the SOS path
    // -------------------------------------------------------------------------------------------

    /// <summary>notification-svc's base address. Unset ⇒ no SOS can be dispatched at all.</summary>
    public string? NotificationBaseUrl { get; set; }

    /// <summary>Must equal notification-svc's <c>Notification:InternalApiKey</c>.</summary>
    public string? NotificationInternalApiKey { get; set; }

    /// <summary>
    /// The budget for the whole dispatch hop.
    /// </summary>
    /// <remarks>
    /// **Bounded by D-33 rather than by D6' §8.3's 2 s internal-hop default.** The alert is
    /// delivered *on* that call (notification-svc dispatches an SOS inline rather than queuing it),
    /// so this timeout has to cover two SMS gateways answering in parallel — and it has to stay
    /// under the five seconds the SLO allows end to end, or the timeout would be the thing that
    /// breaks the promise.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:00:30")]
    public TimeSpan NotificationTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Whether an SOS with no emergency contact on file is refused.
    /// </summary>
    /// <remarks>
    /// D3' says `400 no-emergency-contact`, and that is the default. Turning it off records the
    /// event and the admin live feed without an SMS — which is what a deployment whose admin desk
    /// is staffed would want, and which is **not** what the contract says, so it is off by default
    /// and announced at start-up when on.
    /// </remarks>
    public bool RequireEmergencyContact { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // D-34 — trip sharing
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The public page a share token is appended to. Unset ⇒ `POST /v1/trip-share/{tripId}` is
    /// refused rather than answering with a link nobody can open.
    /// </summary>
    public string? ShareBaseUrl { get; set; } = "https://passenger.mageride.lk/track?token=";

    /// <summary>D-34: "trip + 1 h". The grace after a trip reaches a terminal state.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan ShareGrace { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a token issued for a trip that has not ended yet lives.
    /// </summary>
    /// <remarks>
    /// D-34 pins the *end* of the window to trip end, which is unknown while the trip is running —
    /// so a live trip's token gets this ceiling and the trip-end revocation (`POST
    /// /v1/internal/safety/trips/{tripId}/close`) is what normally closes it. Without the ceiling a
    /// ride that never reached a terminal state would leave a link open for ever.
    /// </remarks>
    [Range(typeof(TimeSpan), "01:00:00", "48:00:00")]
    public TimeSpan ShareMaxLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Bytes of entropy in a share token. **No spec** — 32 bytes is 256 bits, and the token is the
    /// whole credential for an unauthenticated page (D-34, AL-44).
    /// </summary>
    [Range(16, 64)]
    public int ShareTokenBytes { get; set; } = 32;

    /// <summary>D-34: "60 req/min" per token.</summary>
    [Range(1, 6_000)]
    public int PublicViewPerMinute { get; set; } = 60;

    /// <summary>
    /// The per-IP companion. **No spec gives a number** — D3' says the public family is limited
    /// "per token **and** per IP", and a per-token limit alone is no limit at all against somebody
    /// who has harvested a hundred links. Ten tokens' worth.
    /// </summary>
    [Range(1, 60_000)]
    public int PublicViewPerMinutePerIp { get; set; } = 600;

    /// <summary>
    /// How stale a live position may be before the public view omits it.
    /// </summary>
    /// <remarks>
    /// Drawing a marker at a coordinate of unknown age is exactly what US-7.17 removes from the
    /// public map, and a shared link is the surface where a stale marker is most misleading: the
    /// person watching is not in the vehicle and has no other way to tell.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan PositionMaxAge { get; set; } = TimeSpan.FromMinutes(2);

    // -------------------------------------------------------------------------------------------
    // US-12.5 / US-12.6 — reports
    // -------------------------------------------------------------------------------------------

    /// <summary>reputation-svc's gRPC address (D3' reputation-svc, D7' §4.2 <c>Grpc__ListenPort</c>).</summary>
    public string ReputationGrpcAddress { get; set; } = "http://reputation-svc:5005";

    /// <summary>Must equal reputation-svc's <c>Reputation:InternalApiKey</c>.</summary>
    public string? ReputationInternalKey { get; set; }

    /// <summary>D6' §8.3's internal hop.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan ReputationTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Off ⇒ reports are filed and never counted, so no vehicle is ever auto-delisted. Announced
    /// at start-up, because the moderation queue still fills and the third confirmation does
    /// nothing.
    /// </summary>
    public bool ReputationReportingEnabled { get; set; } = true;

    /// <summary>US-12.6 / D5' §4.2: three CONFIRMED reports delist a vehicle.</summary>
    /// <remarks>
    /// Held here as well as in reputation-svc, and the duplication is deliberate: reputation-svc's
    /// threshold decides the *driver's* block state, this one decides what this service reports
    /// back to a moderator about the *vehicle*. Same number, two subjects, and the C052 handoff
    /// says so.
    /// </remarks>
    [Range(1, 100)]
    public int ReportDelistThreshold { get; set; } = 3;

    // -------------------------------------------------------------------------------------------
    // Bounds
    //
    // **There is no switch for the outbox row here, deliberately.** `sos.raised` is written inside
    // the transaction that records the alert (R-13), so an operator learns about an SOS whether or
    // not a gateway took it — a flag that could skip the row would make the one case a human is most
    // needed for the one case nobody is told about. What *is* switchable is publication, and that is
    // the kernel's own `Outbox:DispatcherEnabled`: a deployment with no broker still writes the rows
    // and drains them when one appears.
    // -------------------------------------------------------------------------------------------

    /// <summary>Rows a history or queue read returns at most.</summary>
    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 50;
}
