using System.ComponentModel.DataAnnotations;

namespace MageRide.Notification.Configuration;

/// <summary>
/// notification-svc's knobs. Every default is argued at its declaration; the ones with no spec
/// behind them say so.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    /// <summary>
    /// The interim shared secret <c>/v1/internal/notify/**</c> demands, until mTLS (C042).
    /// </summary>
    /// <remarks>
    /// <b>Unset means the internal family is not mapped at all</b> — the same posture ride-svc,
    /// registry-svc and trip-state-svc take, and the opposite of content-svc's template read. The
    /// difference is what the route does: a template body is public wording, while this one *sends*
    /// — an open send endpoint is a free SMS gateway and a free push channel into every handset on
    /// the platform.
    /// </remarks>
    public string? InternalApiKey { get; set; }

    // -------------------------------------------------------------------------------------------
    // content-svc — the D-26 render path
    // -------------------------------------------------------------------------------------------

    /// <summary>Base address of content-svc. Unset ⇒ nothing renders and nothing is sent.</summary>
    public string? ContentBaseUrl { get; set; }

    /// <summary>Must equal content-svc's <c>Content:InternalApiKey</c>, when it has one.</summary>
    public string? ContentInternalApiKey { get; set; }

    /// <summary>D6' §8.3's internal hop.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan ContentTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a rendered template is held in process. Matches content-svc's own
    /// <c>Cache:Ttl</c> (D7' §4.2, 300 s) — its definition of done is that an edit is visible here
    /// "within the documented cache TTL", and a longer one here would break that promise from this
    /// side.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan TemplateCacheTtl { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// **No spec** — a backstop, not a working limit. Three languages × the two dozen keys §14.4
    /// names is under a hundred entries; at the ceiling the cache is cleared rather than the
    /// process growing until it is killed.
    /// </summary>
    [Range(16, 100_000)]
    public int TemplateCacheMaxEntries { get; set; } = 1_000;

    // -------------------------------------------------------------------------------------------
    // Push — FCM HTTP v1 + APNs HTTP/2 (D6' §7.4, D-27)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>log</c> writes the push to the log instead of sending it; <c>live</c> uses FCM and APNs.
    /// </summary>
    /// <remarks>
    /// Same shape — and the same guard rail — as iam-svc's <c>Sms:Provider=dev</c>: the log
    /// transport is what makes the stack runnable without Google and Apple credentials, and
    /// <c>NotificationApplication</c> refuses to start with it outside Development unless
    /// <see cref="AllowLogTransportOutsideDevelopment"/> says otherwise. The replica sets it; it
    /// runs on synthetic devices.
    /// </remarks>
    [Required]
    public string PushProvider { get; set; } = LogProvider;

    public const string LogProvider = "log";
    public const string LiveProvider = "live";

    /// <summary>FCM project id — the <c>{project}</c> of <c>/v1/projects/{project}/messages:send</c>.</summary>
    public string? FcmProjectId { get; set; }

    /// <summary>Service-account client email, for the OAuth2 assertion FCM HTTP v1 requires.</summary>
    public string? FcmClientEmail { get; set; }

    /// <summary>Service-account RSA private key, PEM. Paired with <see cref="FcmClientEmail"/>.</summary>
    public string? FcmPrivateKeyPem { get; set; }

    public string FcmBaseUrl { get; set; } = "https://fcm.googleapis.com/";

    /// <summary>Google's token endpoint, where the service-account assertion is exchanged.</summary>
    public string GoogleTokenUrl { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>APNs host. The sandbox one is <c>api.sandbox.push.apple.com</c>.</summary>
    public string ApnsBaseUrl { get; set; } = "https://api.push.apple.com/";

    /// <summary>The `kid` of the APNs auth key.</summary>
    public string? ApnsKeyId { get; set; }

    /// <summary>Apple developer team id — the `iss` of the APNs provider token.</summary>
    public string? ApnsTeamId { get; set; }

    /// <summary>The ES256 auth key, PEM.</summary>
    public string? ApnsPrivateKeyPem { get; set; }

    /// <summary>The app's bundle id, sent as <c>apns-topic</c>.</summary>
    public string? ApnsTopic { get; set; }

    /// <summary>
    /// Per-attempt budget for one push. Deliberately short: E-01's whole window is three seconds,
    /// and a push still in flight when the fallback fires has already lost.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan PushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many device tokens one send fans out to concurrently — D6' §7.4's "batch send".
    /// **No spec pins the number**; FCM HTTP v1 removed the multicast endpoint, so a "batch" is
    /// N concurrent single sends and this is how many are in flight at once.
    /// </summary>
    [Range(1, 500)]
    public int PushFanoutBatchSize { get; set; } = 25;

    /// <summary>
    /// A registration token untouched for this long is not worth an attempt. FCM and APNs both
    /// retire one at around 270 days; this is deliberately shorter than that, because a handset
    /// that has not opened the app in six months is not waiting for a ride offer.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan TokenStaleAfter { get; set; } = TimeSpan.FromDays(180);

    /// <summary>See <see cref="PushProvider"/>.</summary>
    public bool AllowLogTransportOutsideDevelopment { get; set; }

    // -------------------------------------------------------------------------------------------
    // E-01 — the offer push and its SMS fallback
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// D6' §7.4 / E-01: "3 s no-ack → SMS fallback to driver". The window starts when the push is
    /// handed to FCM/APNs, not when the offer was armed — the 15 s offer TTL is dispatch-svc's
    /// clock and this one is about the handset.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:00:30")]
    public TimeSpan OfferAckWindow { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How often the ack sweep runs. One second, for the same reason ride-svc's timer loop is one
    /// second (R-04): the deadline is on the row, so the interval decides only how late the
    /// fallback is, and a driver has fifteen seconds in total.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan OfferAckSweepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Off ⇒ an unacked offer push is left alone and no SMS is sent. Announced at start-up: from
    /// the outside every offer still looks delivered.
    /// </summary>
    public bool OfferSmsFallbackEnabled { get; set; } = true;

    /// <summary>
    /// Whether the ack sweep runs on a timer. Off leaves the deadlines armed and nothing sweeping
    /// them — which is only ever what a test wants, so that it can drive one pass and assert what
    /// happened rather than race a background loop.
    /// </summary>
    public bool OfferAckSweepEnabled { get; set; } = true;

    /// <summary>Rows the ack sweep claims per pass.</summary>
    [Range(1, 1_000)]
    public int OfferAckBatchSize { get; set; } = 100;

    // -------------------------------------------------------------------------------------------
    // D-27 — the backoff worker
    // -------------------------------------------------------------------------------------------

    /// <summary>Off ⇒ notifications are enqueued and nothing ever sends them.</summary>
    public bool DeliveryEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan DeliveryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    [Range(1, 1_000)]
    public int DeliveryBatchSize { get; set; } = 50;

    /// <summary>
    /// Attempts before a notification is <c>Failed</c>. **No spec** — D-27 says "exponential-backoff
    /// worker" and names no ceiling. Five attempts over the backoff below spans about eight minutes,
    /// which outlasts a gateway blip and does not outlast the ride the message is about.
    /// </summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay; each subsequent one doubles up to <see cref="BackoffMax"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(5);

    // -------------------------------------------------------------------------------------------
    // Retention — this is also what takes recipient_phone back out of the database
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How long a delivered notification is kept. **No spec pins it.** The row holds an E.164
    /// number for the two recipients who have no account (AL-21, AL-45), so the sweep is a PDPA
    /// control (E-06) as much as a housekeeping one; 30 days is the shortest window that still
    /// answers "did my package notification go out last month".
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    public bool RetentionSweepEnabled { get; set; } = true;

    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(6);

    [Range(1, 100_000)]
    public int RetentionBatchSize { get; set; } = 5_000;

    // -------------------------------------------------------------------------------------------
    // The AL-44 web surface — tokens are minted here and SMSed, never returned
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The public tracking page the minted token is appended to (D6' I-23.3/I-29.2). Unset ⇒ no
    /// link can be built, so the three SMS branches that carry one are refused rather than sent
    /// with a broken URL.
    /// </summary>
    public string? WebTrackBaseUrl { get; set; } = "https://passenger.mageride.lk/track?token=";

    /// <summary>
    /// Bytes of entropy in a share token. **No spec** — 32 bytes is 256 bits, url-safe base64, and
    /// the token is the whole credential for an unauthenticated page (AL-44).
    /// </summary>
    [Range(16, 64)]
    public int ShareTokenBytes { get; set; } = 32;

    /// <summary>D6' I-23.3: the package-recipient token lives for "delivery + 1 h".</summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan PackageRecipientTokenTtl { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// AL-45: 300 s, and the contract pins the location request's own <c>ttl</c> at <c>const: 300</c>.
    /// The token cannot outlive the request it stands in for.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan PickupConfirmTokenTtl { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// D6' I-29.2 says "TTL = trip completion", which is not a duration. This is the ceiling on a
    /// ride that never reaches a terminal state; safety-svc (C052) revokes the token at completion,
    /// which is the usual end.
    /// </summary>
    [Range(typeof(TimeSpan), "01:00:00", "48:00:00")]
    public TimeSpan ProxyRiderTokenTtl { get; set; } = TimeSpan.FromHours(12);

    // -------------------------------------------------------------------------------------------
    // P-12 — the proxy location-request limits
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Off ⇒ the 5/hour and 30/day buckets are not consulted here. ride-svc holds the same limit in
    /// Postgres at the issuing end, so this is the second of two gates and not the only one — but
    /// turning it off is still announced, because this one is what bounds the *pushes*.
    /// </summary>
    public bool LocationRequestLimitsEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // Consumers
    // -------------------------------------------------------------------------------------------

    /// <summary>D6' §2: "consumer group per service".</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "notification-svc";

    /// <summary>
    /// Off ⇒ nothing is consumed: no offer push, no driver-assigned, no low-balance nudge. The
    /// endpoints still work, so from the outside the service is healthy and silent.
    /// </summary>
    public bool ConsumersEnabled { get; set; } = true;

    // -------------------------------------------------------------------------------------------
    // Bounds
    // -------------------------------------------------------------------------------------------

    /// <summary><c>notification.yaml</c>'s <c>maxItems: 1000</c> on the send route.</summary>
    [Range(1, 10_000)]
    public int MaxRecipientsPerSend { get; set; } = 1_000;

    /// <summary>
    /// The ceiling on a US-14.8 broadcast fan-out. **No spec** — a bound, and truncation is logged
    /// rather than silent, because an announcement that reached nine tenths of the platform looks
    /// exactly like one that reached all of it.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxBroadcastRecipients { get; set; } = 50_000;
}

/// <summary>
/// SMS delivery (D7' §4.2 <c>Sms__FitSmsApiToken</c> / <c>Sms__SecondaryGateway</c>).
/// </summary>
/// <remarks>
/// <b>Bound to the same <c>Sms</c> section iam-svc binds</b>, with the same property names, so one
/// set of environment variables configures both. They are two readers of one account, not two
/// accounts: D7' §4.2 declares the keys once, and a deployment where the OTP and the SOS went out
/// under different sender masks would be a deployment where half the messages are unrecognisable.
/// </remarks>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary><c>dev</c> logs the message instead of sending it.</summary>
    public const string DevProvider = "dev";

    /// <summary>
    /// Fit SMS v4 REST — the platform's ONLY SMS gateway (AL-60). D6' §7.3 named Notify.lk as the
    /// primary and it was implemented as one; the account moved and the class is gone rather than
    /// left switchable, because a gateway nobody holds credentials for is a code path no
    /// deployment exercises. §7.3's SECONDARY is unchanged — it is a generic HTTP shape rather
    /// than a named provider, and D-33's SOS still needs a second transport.
    /// </summary>
    public const string FitSmsProvider = "fitsms";

    [Required]
    public string Provider { get; set; } = DevProvider;

    // --- Fit SMS ---------------------------------------------------------------------------
    // The same five names iam-svc's SmsOptions declares, because both bind the SAME `Sms`
    // section (see this class's remarks). A name that differed by one character here would give
    // the OTP and the SOS different senders — or leave one of them with no token at all — from a
    // single set of environment variables that looked complete.

    /// <summary>
    /// Fit SMS v4 REST base address. The gateway posts <c>sms/send</c> relative to it, so it ends
    /// in a slash.
    /// </summary>
    [Required]
    public string FitSmsBaseUrl { get; set; } = "https://app.fitsms.lk/api/v3/";

    /// <summary>
    /// Fit SMS bearer token — <c>Sms__FitSmsApiToken</c>. Issued as <c>{id}|{secret}</c>, and the
    /// whole string is the credential, pipe included.
    /// </summary>
    public string? FitSmsApiToken { get; set; }

    /// <summary>
    /// Registered sender mask on Fit SMS. Their limit is 11 characters for an alphanumeric mask.
    /// </summary>
    [Required]
    public string FitSmsSenderId { get; set; } = "The Change";

    /// <summary>
    /// The <c>type</c> a non-ASCII body is sent as. AL-26 makes Sinhala the default language, so
    /// the common message on this platform is UCS-2 rather than GSM-7 and sending it as
    /// <c>plain</c> is how it arrives as question marks. A setting rather than a constant so a
    /// deployment can fall back to <c>plain</c> if the gateway ever refuses <c>unicode</c>.
    /// </summary>
    public string FitSmsUnicodeType { get; set; } = "unicode";

    /// <summary>
    /// <c>expiry_time</c> for a non-OTP message, in seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// iam-svc derives this from the OTP's own TTL, because a code that has expired makes its
    /// message worthless. Nothing here has that: an SOS, a share link and a low-balance warning
    /// are all worth delivering late. Their API's default and maximum are both 24 hours, so this
    /// is that ceiling stated rather than left implicit.
    /// </para>
    /// <para>
    /// <b>One value, not one per message type.</b> A shorter deadline for an SOS is arguably
    /// right — a safety alert delivered an hour late misleads rather than informs — but
    /// <see cref="ISmsGateway.SendAsync"/> is handed a phone and a body and knows nothing about
    /// which it is. Adding the setting without the argument would be a knob that resolves to
    /// nothing; if D-33 wants its own deadline, the interface has to carry the intent first.
    /// </para>
    /// </remarks>
    public TimeSpan FitSmsExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// D7' §4.2 <c>Sms__SecondaryGateway</c> — the Dialog/Mobitel half of D6' §7.3.
    /// </summary>
    /// <remarks>
    /// Empty is legal for every message except one. <b>D-33 requires two gateways in parallel for
    /// an SOS</b>, so with this unset the SOS path has one gateway and the p99 ≤ 5 s promise rests
    /// on that one — which is announced loudly at start-up rather than discovered during an
    /// emergency.
    /// </remarks>
    public string? SecondaryGateway { get; set; }

    public string? SecondaryApiKey { get; set; }

    public string? SecondarySenderId { get; set; }

    /// <summary>D6' §7.3: "Retry: 2 attempts".</summary>
    [Range(1, 5)]
    public int MaxAttemptsPerGateway { get; set; } = 2;

    /// <summary>
    /// Per-attempt budget. Bounded by D-33: the whole SOS fan-out has five seconds at the 99th
    /// percentile, and a gateway that has not answered in four is not going to save the message.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>The dev sender writes message bodies to the log; outside Development, ask for it.</summary>
    public bool AllowDevSenderOutsideDevelopment { get; set; }
}
