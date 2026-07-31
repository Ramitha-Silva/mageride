namespace MageRide.Safety.Domain;

/// <summary><c>safety.sos_events.role</c> (migration 0902).</summary>
public static class SosRoles
{
    public const string Passenger = "passenger";
    public const string Driver = "driver";

    public static bool IsKnown(string? role) =>
        role is Passenger or Driver;
}

/// <summary><c>safety.sos_events.source</c> (migration 0902).</summary>
public static class SosSources
{
    /// <summary>Raised from an app, by an authenticated user.</summary>
    public const string App = "app";

    /// <summary>
    /// Raised from an SCR-WT page (AL-44, US-25.5), where the share token is the only identity.
    /// public-bff (C057) is the caller; this service owns the row either way.
    /// </summary>
    public const string Web = "web";
}

/// <summary>
/// <c>safety.sos_events.sms_status</c> — this service's vocabulary, as C005's comment says.
/// </summary>
public static class SosSmsStatuses
{
    /// <summary>At least one gateway took it. `dispatched_at` is set.</summary>
    public const string Dispatched = "Dispatched";

    /// <summary>
    /// Every gateway refused. The event is still recorded and the admin feed still fires — an SOS
    /// nobody could SMS is the one an operator most needs to see.
    /// </summary>
    public const string Failed = "Failed";

    /// <summary>
    /// AL-13: there is no emergency contact to send to. Distinct from <see cref="Failed"/>, because
    /// the fix is the user's (add a contact) rather than the platform's (a gateway is down).
    /// </summary>
    public const string NoContact = "NoContact";
}

/// <summary><c>safety.vehicle_reports.status</c> (migration 0903).</summary>
public static class VehicleReportStatuses
{
    public const string Pending = "PENDING";
    public const string Confirmed = "CONFIRMED";
    public const string Dismissed = "DISMISSED";

    public static bool IsDecision(string? status) =>
        status is Confirmed or Dismissed;
}

/// <summary>
/// The four <c>safety.trip_share_tokens.scope</c> values (migration 0901).
/// </summary>
/// <remarks>
/// <b>This service issues exactly one of them and revokes all four.</b> D-34's <c>trip_view</c> is
/// the link a passenger shares from the ride screen and is minted here; the other three are minted
/// by notification-svc (C051) and put straight into an SMS, because AL-44/AL-45 say a token is
/// never returned to a client and the recipients of those three have no client to return it to.
/// Revocation is symmetric — every scope on a trip dies when the trip does.
/// </remarks>
public static class ShareTokenScopes
{
    public const string TripView = "trip_view";
    public const string PackageRecipient = "package_recipient";
    public const string ProxyRider = "proxy_rider";
    public const string PickupConfirm = "pickup_confirm";

    /// <summary>The scopes a trip-end revocation closes: every one that names a trip.</summary>
    /// <remarks>
    /// <c>pickup_confirm</c> is absent because it names a <em>location request</em> rather than a
    /// trip (0901's <c>ck_trip_share_tokens_subject</c>) — the round-trip happens before the ride
    /// exists, so there is no trip whose end could close it. Its own 300 s TTL is what does.
    /// </remarks>
    public static readonly IReadOnlyList<string> TripScoped =
        [TripView, PackageRecipient, ProxyRider];
}

/// <summary>The <c>safety.events</c> types this service publishes (migration 0905).</summary>
/// <remarks>
/// <b>Neither the topic nor either name is in D6' §2.1</b> — the same micro-change-set C028, C030,
/// C033, C044 and C046 raised for their own outboxes, and recorded in the C052 handoff. D3' names
/// the consumer of the first one ("admin live-feed WS") and `realtime/signalr-hub.md` has no group
/// for it, so the event is the half this service can own.
/// </remarks>
public static class SafetyEventTypes
{
    /// <summary>US-12.11's admin live feed. Keyed by the person who raised it.</summary>
    public const string SosRaised = "sos.raised";

    /// <summary>US-12.5. Keyed by the driver the report counts against, so the moderation queue orders per person.</summary>
    public const string VehicleReported = "vehicle.reported";

    /// <summary>US-12.6's third confirmation — the one that delists. Keyed by the driver.</summary>
    public const string VehicleReportResolved = "vehicle.report_resolved";
}
